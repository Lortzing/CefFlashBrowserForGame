using CefFlashBrowser.Data;
using CefFlashBrowser.Log;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CefFlashBrowser.Diagnostics
{
    internal static class RuntimeDiagnostics
    {
        private const string SpeedGearMappingName = "Local\\CefFlashBrowser.SpeedGear";
        private static int _started;

        [StructLayout(LayoutKind.Sequential)]
        private struct SpeedGearSharedState
        {
            public long Generation;
            public double Speed;
        }

        public static void StartSpeedGearProbe(string browserSubprocessPath, string speedGearPath, bool nativeSpeedGearEnabled)
        {
            if (Interlocked.Exchange(ref _started, 1) != 0)
                return;

            Task.Run(async () =>
            {
                LogSection("SpeedGear runtime diagnostics started");
                LogInfo($"nativeSpeedGearEnabled={nativeSpeedGearEnabled}");
                LogInfo($"env CEF_FLASH_BROWSER_SPEEDGEAR_ENABLE={Environment.GetEnvironmentVariable("CEF_FLASH_BROWSER_SPEEDGEAR_ENABLE") ?? "<null>"}");
                LogInfo($"env CEF_FLASH_BROWSER_SPEEDGEAR_DEBUG={Environment.GetEnvironmentVariable("CEF_FLASH_BROWSER_SPEEDGEAR_DEBUG") ?? "<null>"}");
                LogInfo($"AppBaseDirectory={GlobalData.AppBaseDirectory}");
                LogInfo($"BrowserSubprocessPath={browserSubprocessPath}");
                LogInfo($"ExpectedSpeedGearPath={speedGearPath}");
                LogInfo($"BrowserSubprocessExists={File.Exists(browserSubprocessPath)}");
                LogInfo($"SpeedGearDllExists={File.Exists(speedGearPath)}");
                LogInfo($"CurrentProcess={Process.GetCurrentProcess().ProcessName} pid={Process.GetCurrentProcess().Id}");
                LogInfo("Diagnostic rule: if CefFlashBrowser.Subprocess.exe is running but hasSpeedGear=false, native speed gear cannot affect Flash.");

                for (var i = 0; i < 90; i++)
                {
                    try
                    {
                        ProbeSharedSpeedState(i);
                        ProbeCefFlashProcesses(i, speedGearPath);
                    }
                    catch (Exception ex)
                    {
                        LogError($"probe iteration {i} failed", ex);
                    }

                    await Task.Delay(2000).ConfigureAwait(false);
                }

                LogSection("SpeedGear runtime diagnostics stopped");
            });
        }

        private static void ProbeSharedSpeedState(int iteration)
        {
            try
            {
                using (var mmf = MemoryMappedFile.OpenExisting(SpeedGearMappingName, MemoryMappedFileRights.Read))
                using (var accessor = mmf.CreateViewAccessor(0, Marshal.SizeOf(typeof(SpeedGearSharedState)), MemoryMappedFileAccess.Read))
                {
                    accessor.Read(0, out SpeedGearSharedState state);
                    LogInfo($"speed_state iteration={iteration} exists=true generation={state.Generation} speed={state.Speed:0.######}");
                }
            }
            catch (FileNotFoundException)
            {
                LogInfo($"speed_state iteration={iteration} exists=false reason=memory_mapping_not_found name={SpeedGearMappingName}");
            }
            catch (Exception ex)
            {
                LogError($"speed_state iteration={iteration} read_failed", ex);
            }
        }

        private static void ProbeCefFlashProcesses(int iteration, string speedGearPath)
        {
            var expectedSpeedGearName = Path.GetFileName(speedGearPath) ?? "CefFlashBrowser.SpeedGear.dll";
            var processes = Process.GetProcesses()
                .Where(p => p.ProcessName.IndexOf("CefFlashBrowser", StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(p => p.ProcessName)
                .ThenBy(p => p.Id)
                .ToArray();

            LogInfo($"process_probe iteration={iteration} count={processes.Length}");

            foreach (var process in processes)
            {
                var moduleNames = SafeGetModuleNames(process).ToArray();
                var hasSpeedGear = moduleNames.Any(m => string.Equals(m, expectedSpeedGearName, StringComparison.OrdinalIgnoreCase));
                var hasLibCef = moduleNames.Any(m => string.Equals(m, "libcef.dll", StringComparison.OrdinalIgnoreCase));
                var hasPepFlash = moduleNames.Any(m => m.IndexOf("pepflash", StringComparison.OrdinalIgnoreCase) >= 0);
                var hasFlash = moduleNames.Any(m => m.IndexOf("flash", StringComparison.OrdinalIgnoreCase) >= 0);
                var interestingModules = string.Join(",", moduleNames
                    .Where(m =>
                        m.IndexOf("CefFlashBrowser", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        m.IndexOf("cef", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        m.IndexOf("flash", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        m.IndexOf("pep", StringComparison.OrdinalIgnoreCase) >= 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(m => m, StringComparer.OrdinalIgnoreCase));

                LogInfo(
                    $"process_probe iteration={iteration} pid={process.Id} name={process.ProcessName} " +
                    $"hasSpeedGear={hasSpeedGear} hasLibCef={hasLibCef} hasPepFlash={hasPepFlash} hasFlash={hasFlash} modules=[{interestingModules}]");
            }
        }

        private static IEnumerable<string> SafeGetModuleNames(Process process)
        {
            ProcessModuleCollection modules;
            try
            {
                modules = process.Modules;
            }
            catch (Exception ex)
            {
                LogInfo($"process_probe pid={SafePid(process)} name={SafeName(process)} modules_unavailable={ex.GetType().Name}:{ex.Message}");
                yield break;
            }

            foreach (ProcessModule module in modules)
            {
                string name;
                try
                {
                    name = module.ModuleName;
                }
                catch
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(name))
                    yield return name;
            }
        }

        private static int SafePid(Process process)
        {
            try { return process.Id; }
            catch { return -1; }
        }

        private static string SafeName(Process process)
        {
            try { return process.ProcessName; }
            catch { return "<unknown>"; }
        }

        private static void LogSection(string message)
        {
            LogInfo("========== " + message + " ==========");
        }

        private static void LogInfo(string message)
        {
            LogHelper.LogInfo("[FeatureDiag] " + message);
            Debug.WriteLine("[FeatureDiag] " + message);
        }

        private static void LogError(string message, Exception ex)
        {
            LogHelper.LogError("[FeatureDiag] " + message, ex);
            Debug.WriteLine("[FeatureDiag] " + message + ": " + ex);
        }
    }
}
