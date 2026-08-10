using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Tootega.Cockpit.Util;

namespace Tootega.Cockpit.UI
{
    /// <summary>
    /// Puts a Cockpit button in the Visual Studio title bar, beside the Copilot one.
    ///
    /// There is no supported way to do this. The title bar is the shell's own WPF: it has no
    /// group in vsshlids.h and no extension point, so the button is grafted onto the live
    /// visual tree. That is a deliberate trade, and the code is written for the day it stops
    /// working — every step is a search that can come back empty, and coming back empty means
    /// no button, never an exception and never a broken title bar.
    ///
    /// The search is geometric rather than by name or by type. Control names, template parts
    /// and class names are the shell's private business and change between releases; "the
    /// cluster of small controls at the top of the window, left of the minimise button" is a
    /// description of what the user is pointing at, and it survives renames.
    /// </summary>
    internal static class TitleBarButton
    {
        /// <summary>How many times to look for the title bar before giving up.</summary>
        /// <remarks>
        /// The package loads on ShellInitialized, which is earlier than the main window's
        /// visual tree is finished. There is no event for "the title bar exists", so the
        /// search comes back a few times and then stops rather than costing the user a
        /// search that will not succeed.
        /// </remarks>
        private const int MaxAttempts = 20;

        private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);

        /// <summary>How far down the window the title bar can reach, in device-independent pixels.</summary>
        private const double TitleBarHeight = 56;

        /// <summary>
        /// How much of the right edge belongs to minimise, restore and close.
        ///
        /// Those are a panel of buttons like any other, and dropping our button among them
        /// would put it where a user aims to close the IDE. Their names are localised, so the
        /// edge is measured instead of matched.
        /// </summary>
        private const double SystemButtonsWidth = 150;

        private static Button _button;
        private static CockpitPackage _package;

        /// <summary>Adds the button, or does nothing at all if the title bar is not as expected.</summary>
        public static void Install(CockpitPackage package)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (package == null) return;

            _package = package;
            TryInstall(package, 0);
        }

        /// <summary>Removes the button, for when the user turns the option off.</summary>
        public static void Uninstall()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var button = _button;
            _button = null;
            if (button != null && VisualTreeHelper.GetParent(button) is Panel panel) panel.Children.Remove(button);
        }

        private static void TryInstall(CockpitPackage package, int attempt)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_button != null) return;

            try
            {
                if (Place()) return;
            }
            catch (Exception ex)
            {
                // A failure here costs the user a button. It must not cost them the IDE.
                Log.Error("The title bar button could not be added", ex);
                return;
            }

            if (attempt >= MaxAttempts)
            {
                Log.Info("Title bar button: found no place for it. " + Dump());
                return;
            }

            package.JoinableTaskFactory.RunAsync(async delegate
            {
                await System.Threading.Tasks.Task.Delay(RetryDelay).ConfigureAwait(false);
                await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
                TryInstall(package, attempt + 1);
            }).FileAndForget("tootega/cockpit/titlebar");
        }

        private static bool Place()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var window = ShellWindow();
            if (window == null) return false;

            var host = FindHost(window);
            if (host == null) return false;

            var button = Build(host);
            host.Children.Insert(0, button);
            _button = button;

            Log.Debug("Title bar button placed in " + host.GetType().Name + " with " + host.Children.Count + " children.");
            return true;
        }

        /// <summary>
        /// The shell's main window. MainWindow is not always set on the VS application, so the
        /// widest loaded top-level window is taken as the shell when it is not.
        /// </summary>
        private static Window ShellWindow()
        {
            var application = Application.Current;
            if (application == null) return null;

            if (application.MainWindow != null && application.MainWindow.IsLoaded) return application.MainWindow;

            Window widest = null;
            foreach (Window candidate in application.Windows)
            {
                if (!candidate.IsLoaded || candidate.ActualWidth <= 0) continue;
                if (widest == null || candidate.ActualWidth > widest.ActualWidth) widest = candidate;
            }

            return widest;
        }

        /// <summary>
        /// The panel holding the small controls at the top right — where Copilot, sharing and
        /// the account picture sit. The right-most candidate wins, which is the cluster
        /// nearest the window buttons and therefore the one the user pointed at.
        /// </summary>
        private static Panel FindHost(Window window)
        {
            Panel best = null;
            var bestRight = double.MinValue;

            foreach (var element in Descendants(window).OfType<Panel>())
            {
                if (!element.IsVisible || element.ActualHeight <= 0 || element.ActualWidth <= 0) continue;

                var bounds = BoundsIn(window, element);
                if (bounds.IsEmpty) continue;

                // In the title bar, clear of the minimise/restore/close cluster, and holding
                // something clickable — an empty strip is not what we are looking for.
                if (bounds.Top < 0 || bounds.Bottom > TitleBarHeight) continue;
                if (bounds.Right > window.ActualWidth - SystemButtonsWidth) continue;
                if (bounds.Left < window.ActualWidth / 2) continue;
                if (!element.Children.OfType<UIElement>().Any(IsClickable)) continue;

                if (bounds.Right > bestRight)
                {
                    best = element;
                    bestRight = bounds.Right;
                }
            }

            return best;
        }

        private static bool IsClickable(UIElement element)
        {
            return element is ButtonBase || element is ContentControl || element is ItemsControl;
        }

        private static Rect BoundsIn(Visual ancestor, FrameworkElement element)
        {
            try
            {
                var transform = element.TransformToAncestor(ancestor);
                return transform.TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            }
            catch (InvalidOperationException)
            {
                // Not in this window's tree after all — popups and adorners get here.
                return Rect.Empty;
            }
        }

        private static Button Build(Panel host)
        {
            // 20 rather than the usual 16: the mark is a ring with lettering inside it, and
            // at command-bar size the letters close up. The title bar has the room, and its
            // neighbours are drawn at about this size too.
            var image = new CrispImage
            {
                Moniker = CockpitMonikers.Cockpit,
                Width = 20,
                Height = 20,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var button = new Button
            {
                Content = image,
                ToolTip = "Tootega Cockpit",
                VerticalAlignment = VerticalAlignment.Center,
                Focusable = false
            };

            AutomationProperties.SetName(button, "Tootega Cockpit");

            // A neighbour's style is what makes the button look like it belongs: the title
            // bar's hover and pressed brushes are in it, and there is no public key for them.
            var sibling = host.Children.OfType<ButtonBase>().FirstOrDefault();
            if (sibling != null)
            {
                button.Style = sibling.Style;
                button.Margin = sibling.Margin;
                if (!double.IsNaN(sibling.Width)) button.Width = sibling.Width;
                if (!double.IsNaN(sibling.Height)) button.Height = sibling.Height;
            }

            if (button.Style == null)
            {
                button.Background = Brushes.Transparent;
                button.BorderThickness = new Thickness(0);
                button.Padding = new Thickness(6, 2, 6, 2);
                button.Margin = new Thickness(0, 0, 4, 0);
            }

            // Opens the Hub, not a conversation: from the title bar the useful destination is
            // the one that shows every context, the account and the consumption at once, and
            // conversations are one click from there.
#pragma warning disable VSSDK007 // Neither awaited nor joined: a click handler cannot be async.
            button.Click += delegate
            {
                var package = _package;
                if (package == null) return;

                package.JoinableTaskFactory.RunAsync(
                        () => package.ShowToolWindowAsync<HubToolWindow>(package.DisposalToken))
                    .FileAndForget("tootega/cockpit/titleBarClick");
            };
#pragma warning restore VSSDK007

            return button;
        }

        /// <summary>
        /// Writes the top of the window's visual tree to a file and says where it went.
        ///
        /// When the search finds nothing there is nothing to see in the IDE, and the shape of
        /// the title bar is exactly what the next attempt needs to know. It costs one small
        /// text file, and only on the failure path.
        /// </summary>
        private static string Dump()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var path = Path.Combine(Path.GetTempPath(), "tootega-cockpit-titlebar.txt");

            try
            {
                var window = ShellWindow();
                if (window == null) return "There is no shell window to describe.";

                var text = new StringBuilder();
                text.AppendLine("Window: " + window.GetType().FullName +
                                " " + window.ActualWidth.ToString("F0", CultureInfo.InvariantCulture) +
                                "x" + window.ActualHeight.ToString("F0", CultureInfo.InvariantCulture));

                foreach (var element in Descendants(window).OfType<FrameworkElement>())
                {
                    var bounds = BoundsIn(window, element);
                    if (bounds.IsEmpty || bounds.Top < 0 || bounds.Top > TitleBarHeight) continue;

                    text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "{0,-46} name='{1}' auto='{2}' x={3:F0} y={4:F0} w={5:F0} h={6:F0} visible={7}",
                        element.GetType().FullName,
                        element.Name,
                        AutomationProperties.GetName(element),
                        bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                        element.IsVisible));
                }

                File.WriteAllText(path, text.ToString());
                return "The title bar layout was written to " + path + ".";
            }
            catch (Exception ex)
            {
                return "The title bar layout could not be written: " + ex.Message;
            }
        }

        private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            if (root == null) yield break;

            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                yield return child;

                foreach (var descendant in Descendants(child)) yield return descendant;
            }
        }
    }
}
