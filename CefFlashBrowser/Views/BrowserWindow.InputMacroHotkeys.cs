using CefFlashBrowser.Data;
using CefFlashBrowser.Utils;
using CefFlashBrowser.Utils.InputMacros;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace CefFlashBrowser.Views
{
    public partial class BrowserWindow
    {
        private const int WM_HOTKEY = 0x0312;
        private const int HOTKEY_RECORD = 0x43465201;
        private const int HOTKEY_REPLAY = 0x43465202;
        private const int HOTKEY_STOP = 0x43465203;
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;
        private const uint MOD_NOREPEAT = 0x4000;

        private static readonly bool InputMacroHotkeysRegistered = RegisterInputMacroHotkeyClassHandler();
        private readonly DispatcherTimer _inputMacroStateTimer = new DispatcherTimer(DispatcherPriority.Background);
        private string _inputMacroOriginalTitle;
        private bool _inputMacroWasRecording;
        private string _registeredRecordShortcut;
        private string _registeredReplayShortcut;
        private string _registeredStopShortcut;
        private HwndSource _inputMacroHotkeySource;

        private static bool RegisterInputMacroHotkeyClassHandler()
        {
            EventManager.RegisterClassHandler(
                typeof(BrowserWindow),
                LoadedEvent,
                new RoutedEventHandler(OnBrowserWindowLoadedInputMacroHotkeys));
            return true;
        }

        private static void OnBrowserWindowLoadedInputMacroHotkeys(object sender, RoutedEventArgs e)
        {
            if (sender is BrowserWindow window)
                window.InitializeInputMacroHotkeys();
        }

        private void InitializeInputMacroHotkeys()
        {
            if (_inputMacroStateTimer.IsEnabled)
                return;

            _inputMacroOriginalTitle = Title;
            _inputMacroHotkeySource = HwndSource.FromVisual(this) as HwndSource;
            _inputMacroHotkeySource?.AddHook(InputMacroHotkeyWndProc);
            RegisterInputMacroHotkeysIfNeeded(force: true);

            _inputMacroStateTimer.Interval = TimeSpan.FromMilliseconds(500);
            _inputMacroStateTimer.Tick += async delegate
            {
                RegisterInputMacroHotkeysIfNeeded(force: false);
                await RefreshInputMacroRecordingUiAndAutoSaveAsync();
            };
            _inputMacroStateTimer.Start();

            Closed += delegate
            {
                UnregisterInputMacroHotkeys();
                _inputMacroHotkeySource?.RemoveHook(InputMacroHotkeyWndProc);
            };
        }

        private IntPtr InputMacroHotkeyWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_HOTKEY)
                return IntPtr.Zero;

            handled = true;
            switch (wParam.ToInt32())
            {
                case HOTKEY_RECORD:
                    _ = ToggleInputMacroRecordingFromHotkeyAsync();
                    break;
                case HOTKEY_REPLAY:
                    _ = ReplayInputMacroFromHotkeyAsync();
                    break;
                case HOTKEY_STOP:
                    _ = StopInputMacroFromHotkeyAsync();
                    break;
            }
            return IntPtr.Zero;
        }

        private async Task ToggleInputMacroRecordingFromHotkeyAsync()
        {
            if (browser.IsInputMemoryPlaying)
            {
                browser.StopInputMemoryPlayback();
                SetInputMacroHint("键鼠精灵：已停止回放");
                return;
            }

            if (browser.IsInputMemoryRecording)
            {
                await StopAndAutoSaveInputMacroAsync("快捷键停止录制");
                return;
            }

            browser.StartInputMemoryRecording();
            SetInputMacroHint("键鼠精灵：开始录制");
            LogHelper.LogInfo("[InputMemory] hotkey start recording");
        }

        private async Task StopInputMacroFromHotkeyAsync()
        {
            if (browser.IsInputMemoryRecording)
            {
                await StopAndAutoSaveInputMacroAsync("快捷键停止录制");
                return;
            }

            if (browser.IsInputMemoryPlaying)
            {
                browser.StopInputMemoryPlayback();
                SetInputMacroHint("键鼠精灵：已停止回放");
                LogHelper.LogInfo("[InputMemory] hotkey stop playback");
            }
        }

        private async Task ReplayInputMacroFromHotkeyAsync()
        {
            if (browser.IsInputMemoryRecording)
                await StopAndAutoSaveInputMacroAsync("快捷键回放前自动保存");

            if (!browser.IsInputMemoryPlaying)
            {
                SetInputMacroHint("键鼠精灵：开始回放");
                await ReplaySelectedInputMacroAsync();
            }
        }

        private async Task RefreshInputMacroRecordingUiAndAutoSaveAsync()
        {
            if (browser == null)
                return;

            if (browser.IsInputMemoryRecording)
            {
                _inputMacroWasRecording = true;
                SetInputMacroHint($"键鼠精灵：录制中，{browser.InputMemoryEventCount} 个事件");
                return;
            }

            if (_inputMacroWasRecording)
            {
                _inputMacroWasRecording = false;
                await AutoSaveCurrentInputMacroAsync("录制停止后自动保存");
            }
            else if (!browser.IsInputMemoryPlaying && !string.IsNullOrEmpty(_inputMacroOriginalTitle) && Title.StartsWith("[键鼠录制中]", StringComparison.Ordinal))
            {
                Title = _inputMacroOriginalTitle;
            }
        }

        private async Task StopAndAutoSaveInputMacroAsync(string reason)
        {
            if (!browser.IsInputMemoryRecording)
                return;

            browser.StopInputMemoryRecording();
            _inputMacroWasRecording = false;
            await AutoSaveCurrentInputMacroAsync(reason);
        }

        private async Task AutoSaveCurrentInputMacroAsync(string reason)
        {
            try
            {
                var eventsJson = await browser.ExportInputMemoryEventsJsonAsync();
                var macro = InputMacroService.CreateMacro(InputMacroService.CreateDefaultName(), browser.Address, eventsJson);
                if (macro.Events.Count <= 0)
                {
                    SetInputMacroHint("键鼠精灵：录制结束，但没有记录到事件");
                    LogHelper.LogInfo($"[InputMemory] auto save skipped; reason={reason}; count=0");
                    return;
                }

                var path = InputMacroService.Save(macro);
                _selectedInputMacroPath = path;
                _inputMemoryPanel?.ReloadMacros();
                SetInputMacroHint($"键鼠精灵：已自动保存 {Path.GetFileName(path)}（{macro.Events.Count} 个事件）");
                LogHelper.LogInfo($"[InputMemory] auto saved; reason={reason}; count={macro.Events.Count}; path={path}");
            }
            catch (Exception e)
            {
                SetInputMacroHint("键鼠精灵：自动保存失败");
                LogHelper.LogError("[InputMemory] auto save failed", e);
            }
        }

        private void SetInputMacroHint(string text)
        {
            if (string.IsNullOrEmpty(_inputMacroOriginalTitle))
                _inputMacroOriginalTitle = Title;

            if (browser != null && browser.IsInputMemoryRecording)
                Title = "[键鼠录制中] " + _inputMacroOriginalTitle;
            else
                Title = _inputMacroOriginalTitle;

            LogHelper.LogInfo("[InputMemory] " + text);
        }

        private void RegisterInputMacroHotkeysIfNeeded(bool force)
        {
            var record = GlobalData.Settings.InputMacroRecordShortcut ?? string.Empty;
            var replay = GlobalData.Settings.InputMacroReplayShortcut ?? string.Empty;
            var stop = GlobalData.Settings.InputMacroStopShortcut ?? string.Empty;
            if (!force
                && string.Equals(record, _registeredRecordShortcut, StringComparison.OrdinalIgnoreCase)
                && string.Equals(replay, _registeredReplayShortcut, StringComparison.OrdinalIgnoreCase)
                && string.Equals(stop, _registeredStopShortcut, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            UnregisterInputMacroHotkeys();
            RegisterInputMacroHotkey(HOTKEY_RECORD, record, "record");
            RegisterInputMacroHotkey(HOTKEY_REPLAY, replay, "replay");
            RegisterInputMacroHotkey(HOTKEY_STOP, stop, "stop");
            _registeredRecordShortcut = record;
            _registeredReplayShortcut = replay;
            _registeredStopShortcut = stop;
        }

        private void RegisterInputMacroHotkey(int id, string shortcut, string name)
        {
            if (_hwnd == IntPtr.Zero || !TryParseInputMacroHotkey(shortcut, out var modifiers, out var virtualKey))
            {
                LogHelper.LogInfo($"[InputMemory] hotkey not registered; name={name}; shortcut={shortcut}");
                return;
            }

            var ok = RegisterHotKey(_hwnd, id, modifiers | MOD_NOREPEAT, virtualKey);
            LogHelper.LogInfo($"[InputMemory] hotkey register; name={name}; shortcut={shortcut}; ok={ok}; error={(ok ? 0 : Marshal.GetLastWin32Error())}");
        }

        private void UnregisterInputMacroHotkeys()
        {
            if (_hwnd == IntPtr.Zero)
                return;

            UnregisterHotKey(_hwnd, HOTKEY_RECORD);
            UnregisterHotKey(_hwnd, HOTKEY_REPLAY);
            UnregisterHotKey(_hwnd, HOTKEY_STOP);
        }

        private static bool TryParseInputMacroHotkey(string shortcut, out uint modifiers, out uint virtualKey)
        {
            modifiers = 0;
            virtualKey = 0;
            if (string.IsNullOrWhiteSpace(shortcut))
                return false;

            foreach (var raw in shortcut.Split('+'))
            {
                var part = NormalizeHotkeyPart(raw);
                if (part.Length == 0)
                    return false;

                if (part == "CTRL" || part == "CONTROL")
                    modifiers |= MOD_CONTROL;
                else if (part == "SHIFT")
                    modifiers |= MOD_SHIFT;
                else if (part == "ALT")
                    modifiers |= MOD_ALT;
                else if (part == "WIN" || part == "WINDOWS")
                    modifiers |= MOD_WIN;
                else if (!TryParseInputMacroVirtualKey(part, out virtualKey))
                    return false;
            }

            return virtualKey != 0;
        }

        private static string NormalizeHotkeyPart(string value)
        {
            return (value ?? string.Empty).Trim().ToUpperInvariant();
        }

        private static bool TryParseInputMacroVirtualKey(string key, out uint virtualKey)
        {
            virtualKey = 0;
            if (key.Length == 1)
            {
                var c = key[0];
                if (c >= 'A' && c <= 'Z') { virtualKey = c; return true; }
                if (c >= '0' && c <= '9') { virtualKey = c; return true; }
            }

            if (key.Length >= 2 && key[0] == 'F' && int.TryParse(key.Substring(1), out var f) && f >= 1 && f <= 24)
            {
                virtualKey = (uint)(0x70 + f - 1);
                return true;
            }

            switch (key)
            {
                case "-": case "MINUS": case "OEMMINUS": virtualKey = 0xBD; return true;
                case "=": case "EQUAL": case "EQUALS": case "OEMPLUS": virtualKey = 0xBB; return true;
                case "[": case "【": case "BRACKETLEFT": case "OPENBRACKET": case "OEMOPENBRACKETS": virtualKey = 0xDB; return true;
                case "]": case "】": case "BRACKETRIGHT": case "CLOSEBRACKET": case "OEM6": virtualKey = 0xDD; return true;
                case "\\": case "、": case "BACKSLASH": case "OEM5": virtualKey = 0xDC; return true;
                case ";": case "；": case "SEMICOLON": case "OEM1": virtualKey = 0xBA; return true;
                case "'": case "‘": case "’": case "QUOTE": case "APOSTROPHE": case "OEM7": virtualKey = 0xDE; return true;
                case ",": case "，": case "COMMA": case "OEMCOMMA": virtualKey = 0xBC; return true;
                case ".": case "。": case "PERIOD": case "DOT": case "OEMPERIOD": virtualKey = 0xBE; return true;
                case "/": case "／": case "?": case "？": case "SLASH": case "OEM2": virtualKey = 0xBF; return true;
                case "`": case "~": case "·": case "OEM3": virtualKey = 0xC0; return true;
                case "SPACE": virtualKey = 0x20; return true;
                case "ESC": case "ESCAPE": virtualKey = 0x1B; return true;
                default: return false;
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}
