using System;
using System.Diagnostics;
using System.IO;
using CefFlashBrowser.Data;

namespace CefFlashBrowser.Diagnostics
{
    internal static class LogHelper
    {
        private static readonly object SyncRoot = new object();

        public static void LogInfo(string message)
        {
            Write("INFO", message, null);
        }

        public static void LogError(string message, Exception exception = null)
        {
            Write("ERROR", message, exception);
        }

        private static void Write(string level, string message, Exception exception)
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
            if (exception != null)
                line += Environment.NewLine + exception;

            Debug.WriteLine(line);

            try
            {
                var path = GetDiagnosticLogPath();
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                lock (SyncRoot)
                {
                    File.AppendAllText(path, line + Environment.NewLine);
                }
            }
            catch
            {
                // Diagnostics must never break application startup.
            }
        }

        private static string GetDiagnosticLogPath()
        {
            var baseDir = GlobalData.AppBaseDirectory;
            if (string.IsNullOrWhiteSpace(baseDir))
                baseDir = AppDomain.CurrentDomain.BaseDirectory;

            return Path.Combine(baseDir, "Logs", "feature-diagnostics.log");
        }
    }
}
