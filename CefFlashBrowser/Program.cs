using CefFlashBrowser.Data;
using CefFlashBrowser.Diagnostics;
using CefFlashBrowser.FlashBrowser;
using CefFlashBrowser.Log;
using CefFlashBrowser.Singleton;
using CefFlashBrowser.Utils;
using CefSharp;
using Newtonsoft.Json;
using SimpleMvvm.Ioc;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace CefFlashBrowser
{
    internal static class Program
    {
        private static bool _restart = false;
        private static bool _dataInitialized = false;
        private static Mutex _mutex = null;


        [STAThread]
        private static void Main(string[] args)
        {
            try
            {
                RegisterServices();
                _mutex = new Mutex(true, "CefFlashBrowser", out bool isNewInstance);

                Win32.SetDllDirectory(GlobalData.CefDllPath);
                AppDomain.CurrentDomain.AssemblyResolve += ResolveCefSharpAssembly;
                FeatureDiagnostics.MessageLogged += OnFeatureDiagnosticMessageLogged;

                if (isNewInstance)
                {
                    var app = new App();
                    app.InitializeComponent();

                    _dataInitialized = GlobalData.InitData();
                    LanguageManager.InitLanguage();

                    var cts = new CancellationTokenSource();
                    var delLogsTask = LogHelper.DeleteExpiredLogsAsync(cts.Token);

                    InitCefFlash();
                    InitTheme();

                    LogHelper.LogInfo($"Application started successfully, pid: {Process.GetCurrentProcess().Id}");
                    LogHelper.LogInfo($"CefFlashBrowser Version: {Assembly.GetExecutingAssembly().GetName().Version}");

                    app.Run();
                    cts.Cancel();
                    WaitAllTask(delLogsTask);
                }
                else
                {
                    string json = JsonConvert.SerializeObject(args);
                    LogHelper.LogInfo($"Another instance is running, send args to it: {json}");
                    MsgReceiver.SendGlobalData(Encoding.UTF8.GetBytes(json));
                }
            }
            catch (Exception e)
            {
                LogHelper.LogWtf("Unhandled exception in Main method", e);
                WindowManager.ShowError(e.Message);
            }
            finally
            {
                OnTerminate();
                UnregisterServices();
            }
        }

        private static void WaitAllTask(params Task[] tasks)
        {
            try { Task.WaitAll(tasks); }
            catch (Exception e) { LogHelper.LogError("Exception occurred while waiting for tasks", e); }
        }

        private static void InitTheme()
        {
            ThemeManager.ChangeTheme(GlobalData.Settings.FollowSystemTheme ? ThemeManager.GetSystemTheme() : GlobalData.Settings.Theme);
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += UserPreferenceChangedHandler;
        }

        private static void UserPreferenceChangedHandler(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
        {
            if (e.Category == Microsoft.Win32.UserPreferenceCategory.General && GlobalData.Settings.FollowSystemTheme)
            {
                var theme = ThemeManager.GetSystemTheme();
                ThemeManager.ChangeTheme(theme);
            }
        }

        private static Assembly ResolveCefSharpAssembly(object sender, ResolveEventArgs e)
        {
            // Load CefSharp dlls
            string assemblyName = new AssemblyName(e.Name).Name;
            string assemblyPath = Path.Combine(GlobalData.CefDllPath, assemblyName + ".dll");
            return File.Exists(assemblyPath) ? Assembly.LoadFrom(assemblyPath) : null;
        }

        private static void InitCefFlash()
        {
            Environment.SetEnvironmentVariable("ComSpec", GlobalData.EmptyExePath); //Remove black popup window
            var nativeSpeedGearEnabled = IsNativeSpeedGearEnabled();
            SpeedGearController.SetNativeBackendEnabled(nativeSpeedGearEnabled);
            SpeedGearController.EnsureInitialized();

            var settings = new CefFlashSettings()
            {
                Locale = LanguageManager.GetLocale(LanguageManager.CurrentLanguage),
                LogFile = GlobalData.CefLogPath,
                CachePath = GlobalData.CachesPath,
                PpapiFlashPath = GlobalData.FlashPath,
                EnableSystemFlash = true,
                BrowserSubprocessPath = GlobalData.SubprocessPath
            };

            var appSubprocessPath = Path.Combine(GlobalData.AppBaseDirectory, "CefFlashBrowser.Subprocess.exe");
            var speedGearPath = Path.Combine(GlobalData.AppBaseDirectory, "CefFlashBrowser.SpeedGear.dll");
            if (nativeSpeedGearEnabled && File.Exists(appSubprocessPath) && File.Exists(speedGearPath))
            {
                settings.BrowserSubprocessPath = appSubprocessPath;
                LogHelper.LogInfo($"SpeedGear native backend enabled. BrowserSubprocessPath={appSubprocessPath}");
            }
            else if (nativeSpeedGearEnabled)
            {
                LogHelper.LogError(
                    $"SpeedGear backend files not found, falling back to default CefSharp subprocess. Subprocess: {appSubprocessPath}, SpeedGear: {speedGearPath}");
            }
            else
            {
                LogHelper.LogInfo("SpeedGear native backend disabled. Using stock CefSharp browser subprocess; speed factor changes will not hook CEF subprocess time APIs.");
            }

            RuntimeDiagnostics.StartSpeedGearProbe(settings.BrowserSubprocessPath, speedGearPath, nativeSpeedGearEnabled);

            if (GlobalData.Settings.FakeFlashVersionSetting.Enable)
            {
                settings.PpapiFlashVersion = GlobalData.Settings.FakeFlashVersionSetting.FlashVersion;
            }
            else
            {
                settings.PpapiFlashVersion = FileVersionInfo.GetVersionInfo(GlobalData.FlashPath).FileVersion.Replace(',', '.');
            }

            if (GlobalData.Settings.UserAgentSetting.EnableCustom)
            {
                settings.UserAgent = GlobalData.Settings.UserAgentSetting.UserAgent;
            }

            if (GlobalData.Settings.ProxySettings.EnableProxy)
            {
                var proxySettings = GlobalData.Settings.ProxySettings;
                CefSharpSettings.Proxy = new ProxyOptions(proxySettings.IP, proxySettings.Port, proxySettings.UserName, proxySettings.Password);
            }

            if (GlobalData.Settings.DisableGpuAcceleration)
            {
                settings.CefCommandLineArgs["disable-gpu"] = "1";
                settings.CefCommandLineArgs["disable-gpu-compositing"] = "1";
                settings.CefCommandLineArgs["disable-gpu-vsync"] = "1";
            }

#if !DEBUG
            settings.LogSeverity = LogSeverity.Error;
#endif

            settings.CefCommandLineArgs["autoplay-policy"] = "no-user-gesture-required";
            Cef.Initialize(settings);
            LogHelper.LogInfo($"CEF initialized. BrowserSubprocessPath={settings.BrowserSubprocessPath}, CachePath={settings.CachePath}, CefLogPath={settings.LogFile}");
        }

        private static void OnFeatureDiagnosticMessageLogged(string message)
        {
            try
            {
                LogHelper.LogInfo(message);
            }
            catch
            {
                Debug.WriteLine(message);
            }
        }

        private static bool IsNativeSpeedGearEnabled()
        {
            var value = Environment.GetEnvironmentVariable("CEF_FLASH_BROWSER_SPEEDGEAR_ENABLE");
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
        }

        private static void OnTerminate()
        {
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= UserPreferenceChangedHandler;
            _mutex?.Dispose();

            if (_dataInitialized)
            {
                GlobalData.SaveData();
            }

            if (Cef.IsInitialized)
            {
                Cef.Shutdown();
            }

            if (_restart)
            {
                LogHelper.LogInfo("Restarting application...");
                Process.Start(Process.GetCurrentProcess().MainModule.FileName);
            }
            else
            {
                LogHelper.LogInfo($"Application terminated, pid: {Process.GetCurrentProcess().Id}");
            }
        }

        public static void Restart()
        {
            _restart = true;
            Application.Current.Shutdown();
        }

        private static void RegisterServices()
        {
            SimpleIoc.Global.Register<ILogger>(() => new FileLogger(GlobalData.AppLogPath));
        }

        private static void UnregisterServices()
        {
            FeatureDiagnostics.MessageLogged -= OnFeatureDiagnosticMessageLogged;
            SimpleIoc.Global.Unregister<ILogger>();
        }
    }
}