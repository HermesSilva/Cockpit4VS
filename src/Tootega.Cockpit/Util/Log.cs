using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace Tootega.Cockpit.Util
{
    /// <summary>
    /// Output-window logging. Port of src/util/logger.ts: quiet by default, verbose only when
    /// the user turns debug logging on, and it never carries credential content.
    ///
    /// The pane is created once on the UI thread during package initialization; writes
    /// afterwards go through OutputStringThreadSafe, so the CLI reader threads can log without
    /// marshalling.
    ///
    /// The pane is held as <see cref="object"/> and touched only inside a non-inlined method.
    /// That is not style: the JIT resolves every type a method references when it compiles the
    /// method, so naming the interop type in <see cref="Write"/> would try to load the VS
    /// interop assembly even when there is no pane — which throws outside the IDE and takes
    /// down anything that logs, tests included.
    /// </summary>
    internal static class Log
    {
        private static readonly Guid PaneGuid = new Guid("2d9f1a4c-7b6e-4c1f-9a3d-5e8b0c2f7d41");
        private static object _pane;

        public static bool DebugEnabled { get; set; }

        /// <summary>Creates the output pane. Must run on the UI thread, once.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Initialize()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_pane != null) return;

            try
            {
                var output = Package.GetGlobalService(typeof(SVsOutputWindow)) as IVsOutputWindow;
                if (output == null) return;

                var guid = PaneGuid;
                output.CreatePane(ref guid, "Tootega Cockpit", 1, 1);
                output.GetPane(ref guid, out var pane);
                _pane = pane;
            }
            catch (Exception ex)
            {
                Trace.WriteLine("Cockpit: creating the output pane failed: " + ex);
            }
        }

        public static void Info(string message)
        {
            Write(message);
        }

        public static void Debug(string message)
        {
            if (DebugEnabled) Write(message);
        }

        public static void Error(string message, Exception ex = null)
        {
            Write(ex == null ? message : message + ": " + ex);
        }

        private static void Write(string message)
        {
            var line = "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + message + Environment.NewLine;
            Trace.Write("Cockpit " + line);

            var pane = _pane;
            if (pane == null) return;

            try
            {
                WriteToPane(pane, line);
            }
            catch
            {
                // Logging must never take the extension down with it.
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WriteToPane(object pane, string line)
        {
            // Thread-safe by contract, which is the whole reason this logger can be called
            // from the CLI reader threads.
#pragma warning disable VSTHRD010
            ((IVsOutputWindowPane)pane).OutputStringThreadSafe(line);
#pragma warning restore VSTHRD010
        }
    }
}
