using System;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace Tootega.Cockpit.UI
{
    /// <summary>
    /// Translates the active Visual Studio theme into the CSS custom properties the React
    /// webview already speaks.
    ///
    /// The webview was written against VS Code's `var(--vscode-*)` tokens. Rewriting ~10k
    /// lines of CSS to a second vocabulary would be churn with no gain, so instead the host
    /// publishes the same variable names filled from VS theme colors. The result is a UI
    /// that repaints correctly in light, dark, blue and high-contrast without the webview
    /// knowing which editor it is running in.
    ///
    /// Tokens VS has no equivalent for (chart palette, diff bands, error red) are derived
    /// from the background luminosity instead of being hardcoded for one theme.
    /// </summary>
    internal static class VsThemeBridge
    {
        /// <summary>Cockpit accent. Deliberately theme-independent: it is brand, not chrome.</summary>
        private const string Accent = "#e8792b";

        public static string BuildCss()
        {
            var bg = Themed(EnvironmentColors.ToolWindowBackgroundColorKey, Color.FromArgb(255, 255, 255));
            var dark = IsDark(bg);

            var sb = new StringBuilder();
            sb.AppendLine(":root {");

            // --- Base surface and text ---
            Var(sb, "vscode-editor-background", bg);
            Var(sb, "vscode-editor-foreground", Themed(EnvironmentColors.ToolWindowTextColorKey, Color.Black));
            Var(sb, "vscode-foreground", Themed(EnvironmentColors.ToolWindowTextColorKey, Color.Black));
            Var(sb, "vscode-sideBar-background", Themed(EnvironmentColors.ToolWindowBackgroundColorKey, bg));
            Var(sb, "vscode-descriptionForeground", Themed(EnvironmentColors.SystemGrayTextColorKey, Color.Gray));
            Var(sb, "vscode-editorWidget-background", Themed(EnvironmentColors.ToolWindowBackgroundColorKey, bg));
            Var(sb, "vscode-menu-background", Themed(EnvironmentColors.CommandBarMenuBackgroundGradientBeginColorKey, bg));

            // --- Borders ---
            var border = Themed(EnvironmentColors.ToolWindowBorderColorKey, Color.FromArgb(200, 200, 200));
            Var(sb, "vscode-panel-border", border);
            Var(sb, "vscode-widget-border", border);
            Var(sb, "vscode-editorWidget-border", border);
            Var(sb, "vscode-menu-border", border);
            Var(sb, "vscode-focusBorder", Themed(EnvironmentColors.ToolWindowBorderColorKey, border));

            // --- Buttons ---
            Var(sb, "vscode-button-background", Themed(CommonControlsColors.ButtonDefaultColorKey, Accent));
            Var(sb, "vscode-button-foreground", Themed(CommonControlsColors.ButtonDefaultTextColorKey, Color.White));
            Var(sb, "vscode-button-hoverBackground", Themed(CommonControlsColors.ButtonHoverColorKey, Accent));
            Var(sb, "vscode-button-border", Themed(CommonControlsColors.ButtonBorderColorKey, border));
            Var(sb, "vscode-button-secondaryBackground", Themed(CommonControlsColors.ButtonColorKey, bg));
            Var(sb, "vscode-button-secondaryForeground", Themed(CommonControlsColors.ButtonTextColorKey, Color.Black));
            Var(sb, "vscode-button-secondaryHoverBackground", Themed(CommonControlsColors.ButtonHoverColorKey, bg));
            Var(sb, "vscode-toolbar-hoverBackground", Themed(EnvironmentColors.CommandBarMouseOverBackgroundBeginColorKey, bg));

            // --- Inputs and dropdowns ---
            Var(sb, "vscode-input-background", Themed(CommonControlsColors.TextBoxBackgroundColorKey, bg));
            Var(sb, "vscode-input-foreground", Themed(CommonControlsColors.TextBoxTextColorKey, Color.Black));
            Var(sb, "vscode-input-border", Themed(CommonControlsColors.TextBoxBorderColorKey, border));
            Var(sb, "vscode-input-placeholderForeground", Themed(EnvironmentColors.SystemGrayTextColorKey, Color.Gray));
            Var(sb, "vscode-dropdown-background", Themed(CommonControlsColors.ComboBoxBackgroundColorKey, bg));
            Var(sb, "vscode-dropdown-foreground", Themed(CommonControlsColors.ComboBoxTextColorKey, Color.Black));
            Var(sb, "vscode-dropdown-border", Themed(CommonControlsColors.ComboBoxBorderColorKey, border));

            // --- Lists and selection ---
            Var(sb, "vscode-list-activeSelectionBackground", Themed(TreeViewColors.SelectedItemActiveColorKey, Themed(EnvironmentColors.SystemHighlightColorKey, Color.SteelBlue)));
            Var(sb, "vscode-list-activeSelectionForeground", Themed(TreeViewColors.SelectedItemActiveTextColorKey, Color.White));
            Var(sb, "vscode-list-hoverBackground", Themed(TreeViewColors.SelectedItemInactiveColorKey, bg));

            // --- Tabs ---
            Var(sb, "vscode-tab-activeBackground", Themed(EnvironmentColors.FileTabSelectedGradientTopColorKey, bg));
            Var(sb, "vscode-tab-inactiveBackground", Themed(EnvironmentColors.FileTabInactiveGradientTopColorKey, bg));

            // --- Badges, links, progress ---
            Var(sb, "vscode-badge-background", Themed(EnvironmentColors.SystemHighlightColorKey, Color.SteelBlue));
            Var(sb, "vscode-badge-foreground", Themed(EnvironmentColors.SystemHighlightTextColorKey, Color.White));
            Var(sb, "vscode-textLink-foreground", Themed(EnvironmentColors.ControlLinkTextColorKey, Color.FromArgb(0, 102, 204)));
            // The progress bar is the Cockpit's own "working" signal — brand, not chrome.
            Var(sb, "vscode-progressBar-background", Accent);

            // --- Code and quotes ---
            Var(sb, "vscode-textCodeBlock-background", Shift(bg, dark ? 0.06 : -0.04));
            Var(sb, "vscode-textBlockQuote-background", Shift(bg, dark ? 0.04 : -0.03));
            Var(sb, "vscode-terminal-background", Shift(bg, dark ? 0.05 : -0.03));
            Var(sb, "vscode-terminal-foreground", Themed(EnvironmentColors.ToolWindowTextColorKey, Color.Black));

            // --- Diagnostics. VS exposes no themed "error red" for arbitrary content,
            //     so the pair is picked per background instead of favouring one theme. ---
            var error = dark ? "#f14c4c" : "#d13438";
            var warning = dark ? "#cca700" : "#bf8803";
            Var(sb, "vscode-errorForeground", error);
            Var(sb, "vscode-editorError-foreground", error);
            Var(sb, "vscode-inputValidation-errorBorder", error);
            Var(sb, "vscode-inputValidation-errorBackground", dark ? "#5a1d1d" : "#f2dede");
            Var(sb, "vscode-inputValidation-warningBorder", warning);
            Var(sb, "vscode-inputValidation-warningBackground", dark ? "#352a05" : "#fcf8e3");
            Var(sb, "vscode-testing-iconPassed", dark ? "#4ec94e" : "#107c10");

            // --- Diff bands (translucent so the code underneath stays readable) ---
            Var(sb, "vscode-diffEditor-insertedLineBackground", dark ? "rgba(70,160,70,0.20)" : "rgba(60,150,60,0.16)");
            Var(sb, "vscode-diffEditor-removedLineBackground", dark ? "rgba(200,70,70,0.20)" : "rgba(190,60,60,0.16)");

            // --- Chart palette: one accessible set per background ---
            Var(sb, "vscode-charts-blue", dark ? "#4aa3df" : "#1f77b4");
            Var(sb, "vscode-charts-green", dark ? "#4ec94e" : "#2ca02c");
            Var(sb, "vscode-charts-orange", Accent);
            Var(sb, "vscode-charts-purple", dark ? "#b48ead" : "#9467bd");
            Var(sb, "vscode-charts-red", dark ? "#e06c6c" : "#d62728");
            Var(sb, "vscode-charts-yellow", dark ? "#e5c07b" : "#bf8803");

            // --- Typography: follow the VS environment font, not a hardcoded stack ---
            var family = EnvironmentFontFamily();
            sb.AppendLine("  --vscode-font-family: " + family + ";");
            sb.AppendLine("  --vscode-font-size: " + EnvironmentFontSize().ToString(CultureInfo.InvariantCulture) + "px;");
            sb.AppendLine("  --vscode-editor-font-family: Consolas, 'Cascadia Mono', 'Courier New', monospace;");

            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>True when the tool window background is dark enough that light-on-dark applies.</summary>
        public static bool IsDarkTheme()
        {
            return IsDark(Themed(EnvironmentColors.ToolWindowBackgroundColorKey, Color.White));
        }

        private static bool IsDark(Color c)
        {
            // Rec. 601 luma — good enough to choose a text polarity.
            var luma = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
            return luma < 0.5;
        }

        private static Color Themed(ThemeResourceKey key, Color fallback)
        {
            try
            {
                return VSColorTheme.GetThemedColor(key);
            }
            catch
            {
                return fallback;
            }
        }

        private static string Themed(ThemeResourceKey key, string fallbackCss)
        {
            try
            {
                return Css(VSColorTheme.GetThemedColor(key));
            }
            catch
            {
                return fallbackCss;
            }
        }

        private static Color Themed(ThemeResourceKey key, ThemeResourceKey fallbackKey, Color fallback)
        {
            try
            {
                return VSColorTheme.GetThemedColor(key);
            }
            catch
            {
                return Themed(fallbackKey, fallback);
            }
        }

        private static void Var(StringBuilder sb, string name, Color value)
        {
            sb.AppendLine("  --" + name + ": " + Css(value) + ";");
        }

        private static void Var(StringBuilder sb, string name, string cssValue)
        {
            sb.AppendLine("  --" + name + ": " + cssValue + ";");
        }

        private static string Css(Color c)
        {
            if (c.A == 255)
                return "#" + c.R.ToString("x2") + c.G.ToString("x2") + c.B.ToString("x2");
            var alpha = (c.A / 255.0).ToString("0.###", CultureInfo.InvariantCulture);
            return "rgba(" + c.R + "," + c.G + "," + c.B + "," + alpha + ")";
        }

        /// <summary>Nudges a color toward white (positive) or black (negative).</summary>
        private static string Shift(Color c, double amount)
        {
            var target = amount >= 0 ? 255.0 : 0.0;
            var f = Math.Abs(amount);
            var r = (int)Math.Round(c.R + (target - c.R) * f);
            var g = (int)Math.Round(c.G + (target - c.G) * f);
            var b = (int)Math.Round(c.B + (target - c.B) * f);
            return Css(Color.FromArgb(Clamp(r), Clamp(g), Clamp(b)));
        }

        private static int Clamp(int v)
        {
            return v < 0 ? 0 : (v > 255 ? 255 : v);
        }

        private static string EnvironmentFontFamily()
        {
            try
            {
                var res = Application.Current?.Resources[VsFonts.EnvironmentFontFamilyKey] as System.Windows.Media.FontFamily;
                if (res != null && !string.IsNullOrEmpty(res.Source))
                    return "'" + res.Source + "', 'Segoe UI', sans-serif";
            }
            catch
            {
                // Outside a themed WPF context (tests) fall through to the default stack.
            }
            return "'Segoe UI', sans-serif";
        }

        private static double EnvironmentFontSize()
        {
            try
            {
                var res = Application.Current?.Resources[VsFonts.EnvironmentFontSizeKey];
                if (res is double d && d > 0) return Math.Round(d);
            }
            catch
            {
            }
            return 13;
        }
    }
}
