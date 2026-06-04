using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CefFlashBrowser.Subprocess
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            var cefPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "CefSharp");
            SetDllDirectory(cefPath);
            AppDomain.CurrentDomain.AssemblyResolve += ResolveCefSharpAssembly;
            WriteDiagnostic("started args=" + string.Join(" ", args ?? Array.Empty<string>()));

            if (ShouldLoadSpeedGearBackend(args))
            {
                TryLoadSpeedGearBackend();
            }
            else
            {
                WriteDiagnostic("SpeedGear backend skipped for this subprocess");
            }
            return RunCefSelfHost(args);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int RunCefSelfHost(string[] args)
        {
            return CefSharp.Cef.ExecuteProcess();
        }

        private static Assembly ResolveCefSharpAssembly(object sender, ResolveEventArgs e)
        {
            var assemblyName = new AssemblyName(e.Name).Name;
            if (!assemblyName.StartsWith("CefSharp", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var path = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Assets",
                "CefSharp",
                assemblyName + ".dll");

            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        }

        private static bool ShouldLoadSpeedGearBackend(string[] args)
        {
            var processType = GetSwitchValue(args, "--type=");

            if (string.Equals(processType, "ppapi", StringComparison.OrdinalIgnoreCase)
                || string.Equals(processType, "plugin", StringComparison.OrdinalIgnoreCase))
            {
                WriteDiagnostic("SpeedGear backend selected for Flash plugin subprocess type=" + processType);
                return true;
            }

            if (HasSwitch(args, "--ppapi-flash-args"))
            {
                WriteDiagnostic("SpeedGear backend selected by --ppapi-flash-args");
                return true;
            }

            if (string.Equals(processType, "renderer", StringComparison.OrdinalIgnoreCase))
            {
                WriteDiagnostic("SpeedGear backend skipped for renderer subprocess to avoid CEF page zoom/repaint flicker");
            }

            return false;
        }

        private static string GetSwitchValue(string[] args, string prefix)
        {
            if (args == null)
            {
                return null;
            }

            foreach (var arg in args)
            {
                if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return arg.Substring(prefix.Length);
                }
            }

            return null;
        }

        private static bool HasSwitch(string[] args, string switchName)
        {
            if (args == null)
            {
                return false;
            }

            foreach (var arg in args)
            {
                if (string.Equals(arg, switchName, StringComparison.OrdinalIgnoreCase)
                    || arg.StartsWith(switchName + "=", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryLoadSpeedGearBackend()
        {
            var path = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "CefFlashBrowser.SpeedGear.dll");

            if (!File.Exists(path))
            {
                WriteDiagnostic("SpeedGear backend DLL not found: " + path);
                return false;
            }

            var module = LoadLibrary(path);
            if (module == IntPtr.Zero)
            {
                WriteDiagnostic("failed to load SpeedGear backend DLL: " + path + " error=" + Marshal.GetLastWin32Error());
                return false;
            }

            var initializeAddress = GetProcAddress(module, "CefFlashBrowserSpeedGearInitialize");
            if (initializeAddress == IntPtr.Zero)
            {
                WriteDiagnostic("SpeedGear initialize export not found; DLL loaded and DllMain fallback will be used: " + path);
                return true;
            }

            var initialize = (SpeedGearInitializeDelegate)Marshal.GetDelegateForFunctionPointer(
                initializeAddress,
                typeof(SpeedGearInitializeDelegate));
            if (!initialize())
            {
                WriteDiagnostic("SpeedGear backend initialization failed: " + path);
                return false;
            }

            WriteDiagnostic("SpeedGear backend initialized: " + path);
            return true;
        }

        private static void WriteDiagnostic(string message)
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | [Subprocess] {message}";
            Debug.WriteLine(line);

            try
            {
                var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, $"subprocess_{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch
            {
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool SpeedGearInitializeDelegate();

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);
    }
}