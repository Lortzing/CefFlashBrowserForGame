using System;
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

            if (ShouldLoadSpeedGearBackend(args))
            {
                LoadSpeedGearBackend();
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

            if (string.Equals(processType, "gpu-process", StringComparison.OrdinalIgnoreCase)
                || string.Equals(processType, "utility", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.Equals(processType, "renderer", StringComparison.OrdinalIgnoreCase)
                || string.Equals(processType, "ppapi", StringComparison.OrdinalIgnoreCase)
                || string.Equals(processType, "plugin", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return HasSwitch(args, "--ppapi-flash-args")
                || HasSwitch(args, "--ppapi-flash-path")
                || HasSwitch(args, "--ppapi-flash-version");
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

        private static void LoadSpeedGearBackend()
        {
            var path = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "CefFlashBrowser.SpeedGear.dll");

            if (!File.Exists(path))
            {
                Environment.FailFast("SpeedGear backend DLL not found: " + path);
            }

            var module = LoadLibrary(path);
            if (module == IntPtr.Zero)
            {
                Environment.FailFast("Failed to load SpeedGear backend DLL: " + path);
            }

            var initializeAddress = GetProcAddress(module, "CefFlashBrowserSpeedGearInitialize");
            if (initializeAddress == IntPtr.Zero)
            {
                Environment.FailFast("SpeedGear backend initialize export not found: " + path);
            }

            var initialize = (SpeedGearInitializeDelegate)Marshal.GetDelegateForFunctionPointer(
                initializeAddress,
                typeof(SpeedGearInitializeDelegate));
            if (!initialize())
            {
                Environment.FailFast("SpeedGear backend initialization failed: " + path);
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
