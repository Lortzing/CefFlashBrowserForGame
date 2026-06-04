using CefSharp;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;

namespace CefFlashBrowser.FlashBrowser
{
    public partial class ChromiumFlashBrowser
    {
        public async Task ReplayInputMemoryStableAsync(double speed = 1.0, int loopCount = 1, int loopIntervalMs = 1000, int countdownSeconds = 0)
        {
            IsInputMemoryRecording = false;
            StopInputMemoryNativeCapture();

            if (!IsBrowserInitialized)
            {
                FeatureDiagnostics.Log("InputMemory", "stable replay rejected: browser is not initialized");
                return;
            }

            var events = _inputMemoryEvents.OrderBy(item => item.Time).ToList();
            if (events.Count <= 0)
            {
                SetInputMemoryStatus("当前脚本为空，不能回放");
                FeatureDiagnostics.Log("InputMemory", "stable replay rejected: event list is empty");
                return;
            }

            speed = Math.Max(0.1, speed);
            loopIntervalMs = Math.Max(0, loopIntervalMs);
            var loops = loopCount <= 0 ? int.MaxValue : loopCount;
            var playbackVersion = ++_inputMemoryPlaybackVersion;
            IsInputMemoryPlaying = true;
            _inputMemoryPlayIndex = 0;
            _inputMemoryPlayTotal = events.Count;
            _inputMemoryLoopIndex = 0;
            _inputMemoryLoopTotal = loopCount;

            FeatureDiagnostics.Log("InputMemory", $"stable replay started; count={events.Count}; speed={speed:0.###}; loopCount={loopCount}; loopIntervalMs={loopIntervalMs}; mouseMode=screen-sendinput");

            for (var i = countdownSeconds; i > 0; i--)
            {
                SetInputMemoryStatus($"回放将在 {i} 秒后开始");
                await Task.Delay(1000);
                if (playbackVersion != _inputMemoryPlaybackVersion || !IsInputMemoryPlaying)
                    return;
            }

            try
            {
                var host = GetBrowser()?.GetHost();
                if (host == null)
                {
                    SetInputMemoryStatus("浏览器输入通道不可用，不能回放");
                    FeatureDiagnostics.Log("InputMemory", "stable replay failed: browser host is null");
                    return;
                }

                host.SetFocus(true);
                FocusBrowserWindowForScreenReplay();
                for (var loop = 0; loop < loops; loop++)
                {
                    if (playbackVersion != _inputMemoryPlaybackVersion || !IsInputMemoryPlaying)
                        return;

                    _inputMemoryLoopIndex = loop + 1;
                    _inputMemoryPlayIndex = 0;
                    FeatureDiagnostics.Log("InputMemory", $"stable replay loop started; loop={_inputMemoryLoopIndex}; total={(loopCount <= 0 ? "infinite" : loopCount.ToString())}");

                    double previousTime = 0;
                    for (var i = 0; i < events.Count; i++)
                    {
                        if (playbackVersion != _inputMemoryPlaybackVersion || !IsInputMemoryPlaying)
                            return;

                        var item = events[i];
                        var delay = Math.Max(0, (item.Time - previousTime) / speed);
                        previousTime = Math.Max(previousTime, item.Time);
                        if (delay > 0)
                            await Task.Delay((int)Math.Min(int.MaxValue, delay));

                        if (playbackVersion != _inputMemoryPlaybackVersion || !IsInputMemoryPlaying)
                            return;

                        _inputMemoryPlayIndex = i + 1;
                        await Dispatcher.InvokeAsync(() => SendStableHostInputEvent(item));
                        await RefreshInputMemoryStatusAsync();
                    }

                    if (loop + 1 < loops && loopIntervalMs > 0)
                    {
                        FeatureDiagnostics.Log("InputMemory", $"stable replay loop interval; milliseconds={loopIntervalMs}");
                        await Task.Delay(loopIntervalMs);
                    }
                }
            }
            catch (Exception e)
            {
                LogInputMemoryFailure("stable replay failed", e);
            }
            finally
            {
                if (playbackVersion == _inputMemoryPlaybackVersion)
                {
                    IsInputMemoryPlaying = false;
                    _inputMemoryPlayIndex = 0;
                    _inputMemoryLoopIndex = 0;
                    await RefreshInputMemoryStatusAsync();
                    FeatureDiagnostics.Log("InputMemory", "stable replay completed");
                }
            }
        }

        private void SendStableHostInputEvent(HostInputMemoryEvent item)
        {
            var host = GetBrowser()?.GetHost();
            if (host == null || item == null)
                return;

            var modifiers = GetEventFlags(item);
            var type = (item.Type ?? string.Empty).ToLowerInvariant();

            switch (type)
            {
                case "mousemove":
                    {
                        var clientPoint = ResolveStableClientPoint(item);
                        host.SendMouseMoveEvent(new MouseEvent(clientPoint.X, clientPoint.Y, modifiers), false);
                        break;
                    }
                case "mousedown":
                    SendScreenMouseEvent(item, false);
                    break;
                case "mouseup":
                    SendScreenMouseEvent(item, true);
                    break;
                case "click":
                    SendScreenMouseEvent(item, false);
                    SendScreenMouseEvent(item, true);
                    break;
                case "dblclick":
                    SendScreenMouseEvent(item, false);
                    SendScreenMouseEvent(item, true);
                    SendScreenMouseEvent(item, false);
                    SendScreenMouseEvent(item, true);
                    break;
                case "wheel":
                    SendScreenWheelEvent(item);
                    break;
                case "keydown":
                    host.SendKeyEvent(CreateKeyEvent(item, KeyEventType.RawKeyDown, modifiers));
                    break;
                case "keyup":
                    host.SendKeyEvent(CreateKeyEvent(item, KeyEventType.KeyUp, modifiers));
                    break;
                case "char":
                    host.SendKeyEvent(CreateKeyEvent(item, KeyEventType.Char, modifiers));
                    break;
            }
        }

        private void SendScreenMouseEvent(HostInputMemoryEvent item, bool mouseUp)
        {
            var screenPoint = ResolveStableScreenPoint(item, ResolveStableClientPoint(item));
            ShowInputMemoryScreenMarker(screenPoint.X, screenPoint.Y, mouseUp, "replay");
            StableReplayNativeMethods.SetCursorPos(screenPoint.X, screenPoint.Y);
            var flags = GetScreenMouseFlags(item.Button, mouseUp);
            StableReplayNativeMethods.mouse_event(flags, 0, 0, 0, UIntPtr.Zero);
            FeatureDiagnostics.Log("InputMemory", $"stable replay screen mouse; type={(mouseUp ? "mouseup" : "mousedown")}; screenX={screenPoint.X}; screenY={screenPoint.Y}; button={item.Button}");
        }

        private void SendScreenWheelEvent(HostInputMemoryEvent item)
        {
            var screenPoint = ResolveStableScreenPoint(item, ResolveStableClientPoint(item));
            ShowInputMemoryScreenMarker(screenPoint.X, screenPoint.Y, true, "replay");
            StableReplayNativeMethods.SetCursorPos(screenPoint.X, screenPoint.Y);
            StableReplayNativeMethods.mouse_event(StableReplayNativeMethods.MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)(int)item.DeltaY), UIntPtr.Zero);
            FeatureDiagnostics.Log("InputMemory", $"stable replay screen wheel; screenX={screenPoint.X}; screenY={screenPoint.Y}; delta={item.DeltaY}");
        }

        private void FocusBrowserWindowForScreenReplay()
        {
            if (BrowserHandle != IntPtr.Zero)
            {
                StableReplayNativeMethods.SetForegroundWindow(BrowserHandle);
                StableReplayNativeMethods.SetFocus(BrowserHandle);
                FeatureDiagnostics.Log("InputMemory", $"stable replay focus browser handle={BrowserHandle}");
            }
        }

        private static uint GetScreenMouseFlags(int? button, bool mouseUp)
        {
            switch (button ?? 0)
            {
                case 1:
                    return mouseUp ? StableReplayNativeMethods.MOUSEEVENTF_MIDDLEUP : StableReplayNativeMethods.MOUSEEVENTF_MIDDLEDOWN;
                case 2:
                    return mouseUp ? StableReplayNativeMethods.MOUSEEVENTF_RIGHTUP : StableReplayNativeMethods.MOUSEEVENTF_RIGHTDOWN;
                default:
                    return mouseUp ? StableReplayNativeMethods.MOUSEEVENTF_LEFTUP : StableReplayNativeMethods.MOUSEEVENTF_LEFTDOWN;
            }
        }

        private StableReplayNativeMethods.POINT ResolveStableClientPoint(HostInputMemoryEvent item)
        {
            var size = GetBrowserClientSize();
            if (item.RatioX.HasValue && item.RatioY.HasValue && size.Width > 0 && size.Height > 0)
            {
                return new StableReplayNativeMethods.POINT
                {
                    X = ClampToClient((int)Math.Round(item.RatioX.Value * size.Width), size.Width),
                    Y = ClampToClient((int)Math.Round(item.RatioY.Value * size.Height), size.Height)
                };
            }

            if (item.X.HasValue && item.Y.HasValue && BrowserHandle != IntPtr.Zero)
            {
                var point = new StableReplayNativeMethods.POINT
                {
                    X = (int)Math.Round(item.X.Value),
                    Y = (int)Math.Round(item.Y.Value)
                };
                if (StableReplayNativeMethods.ScreenToClient(BrowserHandle, ref point))
                {
                    point.X = ClampToClient(point.X, size.Width);
                    point.Y = ClampToClient(point.Y, size.Height);
                    return point;
                }
            }

            return new StableReplayNativeMethods.POINT
            {
                X = item.X.HasValue ? ClampToClient((int)Math.Round(item.X.Value), size.Width) : 0,
                Y = item.Y.HasValue ? ClampToClient((int)Math.Round(item.Y.Value), size.Height) : 0
            };
        }

        private StableReplayNativeMethods.POINT ResolveStableScreenPoint(HostInputMemoryEvent item, StableReplayNativeMethods.POINT clientPoint)
        {
            if (!item.RatioX.HasValue && !item.RatioY.HasValue && item.X.HasValue && item.Y.HasValue)
            {
                return new StableReplayNativeMethods.POINT
                {
                    X = (int)Math.Round(item.X.Value),
                    Y = (int)Math.Round(item.Y.Value)
                };
            }

            var point = clientPoint;
            if (BrowserHandle != IntPtr.Zero)
                StableReplayNativeMethods.ClientToScreen(BrowserHandle, ref point);
            return point;
        }

        private static int ClampToClient(int value, int max)
        {
            if (max <= 0)
                return Math.Max(0, value);
            if (value < 0)
                return 0;
            if (value >= max)
                return max - 1;
            return value;
        }

        private BrowserClientSize GetBrowserClientSize()
        {
            if (BrowserHandle != IntPtr.Zero)
            {
                StableReplayNativeMethods.RECT rect;
                if (StableReplayNativeMethods.GetClientRect(BrowserHandle, out rect))
                {
                    var width = Math.Max(1, rect.Right - rect.Left);
                    var height = Math.Max(1, rect.Bottom - rect.Top);
                    return new BrowserClientSize(width, height);
                }
            }

            return new BrowserClientSize((int)Math.Max(1, ActualWidth), (int)Math.Max(1, ActualHeight));
        }

        private void FillBrowserRatios(HostInputMemoryEvent item)
        {
            if (item == null || !item.X.HasValue || !item.Y.HasValue)
                return;

            var size = GetBrowserClientSize();
            if (size.Width > 0)
                item.RatioX = Math.Max(0, Math.Min(1, item.X.Value / size.Width));
            if (size.Height > 0)
                item.RatioY = Math.Max(0, Math.Min(1, item.Y.Value / size.Height));
        }

        private readonly struct BrowserClientSize
        {
            public BrowserClientSize(int width, int height)
            {
                Width = width;
                Height = height;
            }

            public int Width { get; }
            public int Height { get; }
        }

        private static class StableReplayNativeMethods
        {
            public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
            public const uint MOUSEEVENTF_LEFTUP = 0x0004;
            public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
            public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
            public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
            public const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
            public const uint MOUSEEVENTF_WHEEL = 0x0800;

            [DllImport("user32.dll")]
            public static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

            [DllImport("user32.dll")]
            public static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);

            [DllImport("user32.dll")]
            public static extern bool ScreenToClient(IntPtr hwnd, ref POINT point);

            [DllImport("user32.dll")]
            public static extern bool SetCursorPos(int X, int Y);

            [DllImport("user32.dll")]
            public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

            [DllImport("user32.dll")]
            public static extern bool SetForegroundWindow(IntPtr hWnd);

            [DllImport("user32.dll")]
            public static extern IntPtr SetFocus(IntPtr hWnd);

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
