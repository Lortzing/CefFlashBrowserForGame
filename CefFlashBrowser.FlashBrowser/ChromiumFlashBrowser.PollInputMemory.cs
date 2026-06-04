using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace CefFlashBrowser.FlashBrowser
{
    public partial class ChromiumFlashBrowser
    {
        private static readonly bool PollInputMemoryRegistered = RegisterPollInputMemory();
        private DispatcherTimer _pollInputMemoryTimer;
        private readonly bool[] _pollInputKeyDown = new bool[256];
        private int _pollLastButtons;
        private int _pollLastMoveTick;
        private int? _pollLastX;
        private int? _pollLastY;
        private bool _pollInputMemoryLogged;

        private static bool RegisterPollInputMemory()
        {
            EventManager.RegisterClassHandler(
                typeof(ChromiumFlashBrowser),
                LoadedEvent,
                new RoutedEventHandler(OnPollInputMemoryLoaded));
            return true;
        }

        private static void OnPollInputMemoryLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is ChromiumFlashBrowser browser)
            {
                browser.EnsurePollInputMemoryTimer();
            }
        }

        private void EnsurePollInputMemoryTimer()
        {
            if (_pollInputMemoryTimer != null)
                return;

            _pollInputMemoryTimer = new DispatcherTimer(DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _pollInputMemoryTimer.Tick += delegate { PollInputMemory(); };
            _pollInputMemoryTimer.Start();
            FeatureDiagnostics.Log("InputMemory", "polling recorder attached");
        }

        private void PollInputMemory()
        {
            if (!IsInputMemoryRecording || IsInputMemoryPlaying)
            {
                ResetPollInputState();
                return;
            }

            if (!_pollInputMemoryLogged)
            {
                _pollInputMemoryLogged = true;
                FeatureDiagnostics.Log("InputMemory", "polling recorder active");
            }

            var foregroundHwnd = PollNativeMethods.GetForegroundWindow();
            if (!IsCurrentProcessWindow(foregroundHwnd))
            {
                ResetPollInputState();
                return;
            }

            var cursor = new PollNativeMethods.POINT();
            if (!PollNativeMethods.GetCursorPos(ref cursor))
                return;

            var insideBrowser = IsScreenPointInsideBrowser(cursor.X, cursor.Y);
            PollKeyboardInput(insideBrowser);
            PollMouseInput(cursor, insideBrowser);
        }

        private void PollKeyboardInput(bool acceptKeyboard)
        {
            for (var vk = 1; vk < _pollInputKeyDown.Length; vk++)
            {
                var down = (PollNativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;
                if (down == _pollInputKeyDown[vk])
                    continue;

                _pollInputKeyDown[vk] = down;
                if (!acceptKeyboard && down)
                    continue;

                RecordPolledInput(new HostInputMemoryEvent
                {
                    Type = down ? "keydown" : "keyup",
                    Time = _inputMemoryStopwatch.Elapsed.TotalMilliseconds,
                    KeyCode = vk,
                    NativeKeyCode = vk,
                    CtrlKey = IsVirtualKeyDown(0x11),
                    ShiftKey = IsVirtualKeyDown(0x10),
                    AltKey = IsVirtualKeyDown(0x12),
                    MetaKey = IsVirtualKeyDown(0x5B) || IsVirtualKeyDown(0x5C)
                });
            }
        }

        private void PollMouseInput(PollNativeMethods.POINT screenPoint, bool insideBrowser)
        {
            if (!insideBrowser)
            {
                _pollLastButtons = GetPolledMouseButtons();
                return;
            }

            var x = screenPoint.X;
            var y = screenPoint.Y;
            ConvertScreenToBrowserClientPoint(ref x, ref y);

            var now = Environment.TickCount;
            if (!_pollLastX.HasValue || !_pollLastY.HasValue || now - _pollLastMoveTick >= 40)
            {
                if (!_pollLastX.HasValue || !_pollLastY.HasValue)
                {
                    RecordMouseMove(x, y);
                }
                else
                {
                    var dx = x - _pollLastX.Value;
                    var dy = y - _pollLastY.Value;
                    if (dx * dx + dy * dy >= 9)
                        RecordMouseMove(x, y);
                }
            }

            var buttons = GetPolledMouseButtons();
            RecordMouseButtonTransitions(_pollLastButtons, buttons, x, y);
            _pollLastButtons = buttons;
        }

        private void RecordMouseMove(int x, int y)
        {
            _pollLastX = x;
            _pollLastY = y;
            _pollLastMoveTick = Environment.TickCount;
            RecordPolledInput(new HostInputMemoryEvent
            {
                Type = "mousemove",
                Time = _inputMemoryStopwatch.Elapsed.TotalMilliseconds,
                X = x,
                Y = y,
                Button = 0,
                Buttons = GetPolledMouseButtons(),
                CtrlKey = IsVirtualKeyDown(0x11),
                ShiftKey = IsVirtualKeyDown(0x10),
                AltKey = IsVirtualKeyDown(0x12),
                MetaKey = IsVirtualKeyDown(0x5B) || IsVirtualKeyDown(0x5C)
            });
        }

        private void RecordMouseButtonTransitions(int previous, int current, int x, int y)
        {
            RecordMouseButtonTransition(previous, current, 1, 0, x, y);
            RecordMouseButtonTransition(previous, current, 2, 2, x, y);
            RecordMouseButtonTransition(previous, current, 4, 1, x, y);
        }

        private void RecordMouseButtonTransition(int previous, int current, int mask, int button, int x, int y)
        {
            var wasDown = (previous & mask) != 0;
            var isDown = (current & mask) != 0;
            if (wasDown == isDown)
                return;

            RecordPolledInput(new HostInputMemoryEvent
            {
                Type = isDown ? "mousedown" : "mouseup",
                Time = _inputMemoryStopwatch.Elapsed.TotalMilliseconds,
                X = x,
                Y = y,
                Button = button,
                Buttons = current,
                CtrlKey = IsVirtualKeyDown(0x11),
                ShiftKey = IsVirtualKeyDown(0x10),
                AltKey = IsVirtualKeyDown(0x12),
                MetaKey = IsVirtualKeyDown(0x5B) || IsVirtualKeyDown(0x5C)
            });
        }

        private void RecordPolledInput(HostInputMemoryEvent item)
        {
            if (!IsInputMemoryRecording || IsInputMemoryPlaying || item == null)
                return;

            _inputMemoryEvents.Add(item);
            InputMemoryEventCount = _inputMemoryEvents.Count;
            SetInputMemoryStatus($"正在录制，已记录 {InputMemoryEventCount} 个事件");

            if (InputMemoryEventCount == 1 || InputMemoryEventCount % 20 == 0)
                FeatureDiagnostics.Log("InputMemory", $"polling recorder count={InputMemoryEventCount}; lastType={item.Type}");
        }

        private void ResetPollInputState()
        {
            Array.Clear(_pollInputKeyDown, 0, _pollInputKeyDown.Length);
            _pollLastButtons = GetPolledMouseButtons();
            _pollLastX = null;
            _pollLastY = null;
            _pollInputMemoryLogged = false;
        }

        private bool IsScreenPointInsideBrowser(int x, int y)
        {
            if (BrowserHandle == IntPtr.Zero)
                return false;

            PollNativeMethods.RECT rect;
            if (!PollNativeMethods.GetWindowRect(BrowserHandle, out rect))
                return false;

            return x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom;
        }

        private bool IsCurrentProcessWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return false;

            uint pid;
            PollNativeMethods.GetWindowThreadProcessId(hwnd, out pid);
            return pid == (uint)Process.GetCurrentProcess().Id;
        }

        private static int GetPolledMouseButtons()
        {
            var buttons = 0;
            if (IsVirtualKeyDown(0x01))
                buttons |= 1;
            if (IsVirtualKeyDown(0x02))
                buttons |= 2;
            if (IsVirtualKeyDown(0x04))
                buttons |= 4;
            return buttons;
        }

        private static bool IsVirtualKeyDown(int virtualKey)
        {
            return (PollNativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
        }

        private static class PollNativeMethods
        {
            [DllImport("user32.dll")]
            public static extern short GetAsyncKeyState(int virtualKey);

            [DllImport("user32.dll")]
            public static extern IntPtr GetForegroundWindow();

            [DllImport("user32.dll")]
            public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

            [DllImport("user32.dll")]
            public static extern bool GetCursorPos(ref POINT point);

            [DllImport("user32.dll")]
            public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

            [StructLayout(LayoutKind.Sequential)]
            public struct POINT
            {
                public int X;
                public int Y;
            }

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
