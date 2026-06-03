using System;
using System.Diagnostics;

namespace CefFlashBrowser.FlashBrowser
{
    public static class FeatureDiagnostics
    {
        public static event Action<string> MessageLogged;

        public static void Log(string feature, string message)
        {
            var line = $"[{feature}] {message}";
            Debug.WriteLine(line);
            try
            {
                MessageLogged?.Invoke(line);
            }
            catch
            {
            }
        }

        public static void Log(string feature, string message, Exception exception)
        {
            Log(feature, exception == null ? message : $"{message}{Environment.NewLine}{exception}");
        }
    }
}
