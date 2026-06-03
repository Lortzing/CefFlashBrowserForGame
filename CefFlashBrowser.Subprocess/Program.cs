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

            LoadSpeedGearBackend();
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
