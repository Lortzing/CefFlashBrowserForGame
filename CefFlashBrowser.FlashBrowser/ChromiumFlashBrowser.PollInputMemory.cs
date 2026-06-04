using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace CefFlashBrowser.FlashBrowser
{
    public partial class ChromiumFlashBrowser
    {
        private LowLevelInputProc _keyboardHookProc;
        private LowLevelInputProc _mouseHookProc;
        private IntPtr _keyboardHook;
        private IntPtr _mouseHook;
        private int _hookLastButtons;

        public void StartInputMemoryNativeCapture()
        {
            StopInputMemoryNativeCapture();

            _keyboardHookProc = KeyboardHookCallback;
            _mouseHookProc = MouseHookCallback;
            var module = HookNativeMethods.GetModuleHandle(null);
            _keyboardHook = HookNativeMethods.SetWindowsHookEx(HookNativeMethods.WH_KEYBOARD_LL, _keyboardHookProc, module, 0);
            _mouseHook = HookNativeMethods.SetWindowsHookEx(HookNativeMethods.WH_MOUSE_LL, _mouseHookProc, module, 0);
            _hookLastButtons = 0;

            FeatureDiagnostics.Log("InputMemory", $"native capture hooks installed; keyboard={_keyboardHook != IntPtr.Zero}; mouse={_mouseHook != IntPtr.Zero}; error={Marshal.GetLastWin32Error()}");
        }

        public void StopInputMemoryNativeCapture()
        {
            if (_keyboardHook != IntPtr.Zero)
            {
                HookNativeMethods.UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = IntPtr.Zero;
            }

            if (_mouseHook != IntPtr.Zero)
            {
                HookNativeMethods.UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }

            _keyboardHookProc = null;
            _mouseHookProc = null;
        }

        private IntPtr KeyboardHookCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && IsInputMemoryRecording && !IsInputMemoryPlaying && IsInputMemoryContextActive(false))
            {
                var msg = wParam.ToInt32();
                var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if (msg == HookNativeMethods.WM_KEYDOWN || msg == HookNativeMethods.WM_SYSKEYDOWN
                    || msg == HookNativeMethods.WM_KEYUP || msg == HookNativeMethods.WM_SYSKEYUP)
                {
                    RecordHookInput(new HostInputMemoryEvent
                    {
                        Type = msg == HookNativeMethods.WM_KEYDOWN || msg == HookNativeMethods.WM_SYSKEYDOWN ? "keydown" : "keyup",
                        Time = _inputMemoryStopwatch.Elapsed.TotalMilliseconds,
                        KeyCode = unchecked((int)info.vkCode),
                        NativeKeyCode = unchecked((int)info.scanCode),
                        CtrlKey = IsVirtualKeyDown(HookNativeMethods.VK_CONTROL),
                        ShiftKey = IsVirtualKeyDown(HookNativeMethods.VK_SHIFT),
                        AltKey = IsVirtualKeyDown(HookNativeMethods.VK_MENU),
                        MetaKey = IsVirtualKeyDown(HookNativeMethods.VK_LWIN) || IsVirtualKeyDown(HookNativeMethods.VK_RWIN)
                    });
                }
            }

            return HookNativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
        }

        private IntPtr MouseHookCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && IsInputMemoryRecording && !IsInputMemoryPlaying)
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if (IsInputMemoryContextActive(true, info.pt.X, info.pt.Y))
                {
                    var msg = wParam.ToInt32();
                    var x = info.pt.X;
                    var y = info.pt.Y;
                    ConvertScreenToBrowserClientPoint(ref x, ref y);
                    RecordMouseHookMessage(msg, x, y, info.mouseData);
                }
            }

            return HookNativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);
        }

        private void RecordMouseHookMessage(int msg, int x, int y, uint mouseData)
        {
            switch (msg)
            {
                case HookNativeMethods.WM_MOUSEMOVE:
                    return;

                case HookNativeMethods.WM_LBUTTONDOWN:
                    _hookLastButtons |= 1;
                    RecordHookMouse("mousedown", x, y, 0, _hookLastButtons, 0);
                    break;
                case HookNativeMethods.WM_LBUTTONUP:
                    _hookLastButtons &= ~1;
                    RecordHookMouse("mouseup", x, y, 0, _hookLastButtons, 0);
                    break;
                case HookNativeMethods.WM_RBUTTONDOWN:
                    _hookLastButtons |= 2;
                    RecordHookMouse("mousedown", x, y, 2, _hookLastButtons, 0);
                    break;
                case HookNativeMethods.WM_RBUTTONUP:
                    _hookLastButtons &= ~2;
                    RecordHookMouse("mouseup", x, y, 2, _hookLastButtons, 0);
                    break;
                case HookNativeMethods.WM_MBUTTONDOWN:
                    _hookLastButtons |= 4;
                    RecordHookMouse("mousedown", x, y, 1, _hookLastButtons, 0);
                    break;
                case HookNativeMethods.WM_MBUTTONUP:
                    _hookLastButtons &= ~4;
                    RecordHookMouse("mouseup", x, y, 1, _hookLastButtons, 0);
                    break;
                case HookNativeMethods.WM_MOUSEWHEEL:
                    var delta = unchecked((short)((mouseData >> 16) & 0xffff));
                    RecordHookMouse("wheel", x, y, 0, _hookLastButtons, delta);
                    break;
            }
        }

        private void RecordHookMouse(string type, int x, int y, int button, int buttons, int wheelDelta)
        {
            var item = new HostInputMemoryEvent
            {
                Type = type,
                Time = _inputMemoryStopwatch.Elapsed.TotalMilliseconds,
                X = x,
                Y = y,
                Button = button,
                Buttons = buttons,
                DeltaY = wheelDelta,
                CtrlKey = IsVirtualKeyDown(HookNativeMethods.VK_CONTROL),
                ShiftKey = IsVirtualKeyDown(HookNativeMethods.VK_SHIFT),
                AltKey = IsVirtualKeyDown(HookNativeMethods.VK_MENU),
                MetaKey = IsVirtualKeyDown(HookNativeMethods.VK_LWIN) || IsVirtualKeyDown(HookNativeMethods.VK_RWIN)
            };
            FillBrowserRatios(item);
            RecordHookInput(item);
        }

        private void RecordHookInput(HostInputMemoryEvent item)
        {
            if (!IsInputMemoryRecording || IsInputMemoryPlaying || item == null)
                return;

            _inputMemoryEvents.Add(item);
            InputMemoryEventCount = _inputMemoryEvents.Count;
            SetInputMemoryStatus($"正在录制，已记录 {InputMemoryEventCount} 个事件");

            if (InputMemoryEventCount == 1 || InputMemoryEventCount % 10 == 0)
                FeatureDiagnostics.Log("InputMemory", $"native capture count={InputMemoryEventCount}; lastType={item.Type}; x={item.X}; y={item.Y}; rx={item.RatioX:0.####}; ry={item.RatioY:0.####}");
        }

        private bool IsInputMemoryContextActive(bool requireCursorInsideBrowser, int? screenX = null, int? screenY = null)
        {
            var foreground = HookNativeMethods.GetForegroundWindow();
            if (foreground == IntPtr.Zero)
                return false;

            uint pid;
            HookNativeMethods.GetWindowThreadProcessId(foreground, out pid);
            if (!IsCefFlashBrowserProcess(pid))
                return false;

            if (!requireCursorInsideBrowser)
                return true;

            if (!screenX.HasValue || !screenY.HasValue)
                return false;

            return IsScreenPointInsideBrowser(screenX.Value, screenY.Value);
        }

        private bool IsCefFlashBrowserProcess(uint pid)
        {
            if (pid == 0)
                return false;

            if (pid == (uint)Process.GetCurrentProcess().Id)
                return true;

            try
            {
                var process = Process.GetProcessById(unchecked((int)pid));
                return process.ProcessName.StartsWith("CefFlashBrowser", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private bool IsScreenPointInsideBrowser(int x, int y)
        {
            if (BrowserHandle == IntPtr.Zero)
                return false;

            HookNativeMethods.RECT rect;
            if (!HookNativeMethods.GetWindowRect(BrowserHandle, out rect))
                return false;

            return x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom;
        }

        private static bool IsVirtualKeyDown(int virtualKey)
        {
            return (HookNativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
        }

        private delegate IntPtr LowLevelInputProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private static class HookNativeMethods
        {
            public const int WH_KEYBOARD_LL = 13;
            public const int WH_MOUSE_LL = 14;
            public const int WM_KEYDOWN = 0x0100;
            public const int WM_KEYUP = 0x0101;
            public const int WM_SYSKEYDOWN = 0x0104;
            public const int WM_SYSKEYUP = 0x0105;
            public const int WM_MOUSEMOVE = 0x0200;
            public const int WM_LBUTTONDOWN = 0x0201;
            public const int WM_LBUTTONUP = 0x0202;
            public const int WM_RBUTTONDOWN = 0x0204;
            public const int WM_RBUTTONUP = 0x0205;
            public const int WM_MBUTTONDOWN = 0x0207;
            public const int WM_MBUTTONUP = 0x0208;
            public const int WM_MOUSEWHEEL = 0x020A;
            public const int VK_CONTROL = 0x11;
            public const int VK_SHIFT = 0x10;
            public const int VK_MENU = 0x12;
            public const int VK_LWIN = 0x5B;
            public const int VK_RWIN = 0x5C;

            [DllImport("user32.dll", SetLastError = true)]
            public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelInputProc lpfn, IntPtr hMod, uint dwThreadId);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool UnhookWindowsHookEx(IntPtr hhk);

            [DllImport("user32.dll")]
            public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

            [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
            public static extern IntPtr GetModuleHandle(string lpModuleName);

            [DllImport("user32.dll")]
            public static extern short GetAsyncKeyState(int virtualKey);

            [DllImport("user32.dll")]
            public static extern IntPtr GetForegroundWindow();

            [DllImport("user32.dll")]
            public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

            [DllImport("user32.dll")]
            public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

            [StructLayout(LayoutKind.Sequential)]
            public struct RECT
            {
                public int Left;
                public int Top;
                public int Right;
                public int Bottom;
            }
        }
    }
}