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

            FeatureDiagnostics.Log("InputMemory", $"stable replay started; count={events.Count}; speed={speed:0.###}; loopCount={loopCount}; loopIntervalMs={loopIntervalMs}; mouseMode=background-postmessage; hwnd={BrowserHandle}");

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
                if (host == null || BrowserHandle == IntPtr.Zero)
                {
                    SetInputMemoryStatus("浏览器输入通道不可用，不能回放");
                    FeatureDiagnostics.Log("InputMemory", $"stable replay failed: hostNull={host == null}; browserHandle={BrowserHandle}");
                    return;
                }

                for (var loop = 0; loop < loops; loop++)
                {
                    if (playbackVersion != _inputMemoryPlaybackVersion || !IsInputMemoryPlaying)
                        return;

                    _inputMemoryLoopIndex = loop + 1;
                    _inputMemoryPlayIndex = 0;
                    FeatureDiagnostics.Log("InputMemory", $"stable replay loop started; loop={_inputMemoryLoopIndex}; total={(loopCount <= 0 ? "infinite" : loopCount.ToString())}; hwnd={BrowserHandle}");

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
                        SendBackgroundInputEvent(item);
                        await RefreshInputMemoryStatusAsync();
                    }

                    if (loop + 1 < loops && loopIntervalMs > 0)
                    {
                        FeatureDiagnostics.Log("InputMemory", $"stable replay loop interval; milliseconds={loopIntervalMs}; nextLoop={loop + 2}");
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

        private void SendBackgroundInputEvent(HostInputMemoryEvent item)
        {
            if (item == null || BrowserHandle == IntPtr.Zero)
                return;

            var type = (item.Type ?? string.Empty).ToLowerInvariant();
            switch (type)
            {
                case "mousemove":
                    SendBackgroundMouseMove(item);
                    break;
                case "mousedown":
                    SendBackgroundMouseButton(item, false);
                    break;
                case "mouseup":
                    SendBackgroundMouseButton(item, true);
                    break;
                case "click":
                    SendBackgroundMouseButton(item, false);
                    SendBackgroundMouseButton(item, true);
                    break;
                case "dblclick":
                    SendBackgroundMouseButton(item, false);
                    SendBackgroundMouseButton(item, true);
                    SendBackgroundMouseButton(item, false);
                    SendBackgroundMouseButton(item, true);
                    break;
                case "wheel":
                    SendBackgroundMouseWheel(item);
                    break;
                case "keydown":
                    SendBackgroundKey(item, false);
                    break;
                case "keyup":
                    SendBackgroundKey(item, true);
                    break;
                case "char":
                    SendBackgroundChar(item);
                    break;
            }
        }

        private void SendBackgroundMouseMove(HostInputMemoryEvent item)
        {
            var point = ResolveStableClientPoint(item);
            var lParam = MakeMouseLParam(point.X, point.Y);
            StableReplayNativeMethods.PostMessage(BrowserHandle, StableReplayNativeMethods.WM_MOUSEMOVE, IntPtr.Zero, lParam);
            FeatureDiagnostics.Log("InputMemory", $"background mousemove; clientX={point.X}; clientY={point.Y}; hwnd={BrowserHandle}");
        }

        private void SendBackgroundMouseButton(HostInputMemoryEvent item, bool mouseUp)
        {
            var point = ResolveStableClientPoint(item);
            var lParam = MakeMouseLParam(point.X, point.Y);
            var message = GetBackgroundMouseMessage(item.Button, mouseUp);
            var wParam = new IntPtr(mouseUp ? 0 : GetBackgroundMouseWParam(item.Button));
            StableReplayNativeMethods.PostMessage(BrowserHandle, StableReplayNativeMethods.WM_MOUSEMOVE, IntPtr.Zero, lParam);
            StableReplayNativeMethods.PostMessage(BrowserHandle, message, wParam, lParam);
            FeatureDiagnostics.Log("InputMemory", $"background mouse; type={(mouseUp ? "mouseup" : "mousedown")}; clientX={point.X}; clientY={point.Y}; button={item.Button}; hwnd={BrowserHandle}");
        }

        private void SendBackgroundMouseWheel(HostInputMemoryEvent item)
        {
            var point = ResolveStableClientPoint(item);
            var screenPoint = point;
            StableReplayNativeMethods.ClientToScreen(BrowserHandle, ref screenPoint);
            var wParam = new IntPtr(((int)item.DeltaY << 16) & unchecked((int)0xffff0000));
            var lParam = MakeMouseLParam(screenPoint.X, screenPoint.Y);
            StableReplayNativeMethods.PostMessage(BrowserHandle, StableReplayNativeMethods.WM_MOUSEWHEEL, wParam, lParam);
            FeatureDiagnostics.Log("InputMemory", $"background wheel; clientX={point.X}; clientY={point.Y}; delta={item.DeltaY}; hwnd={BrowserHandle}");
        }

        private void SendBackgroundKey(HostInputMemoryEvent item, bool keyUp)
        {
            var vk = item.KeyCode ?? item.NativeKeyCode ?? 0;
            if (vk <= 0)
                return;

            var message = keyUp ? StableReplayNativeMethods.WM_KEYUP : StableReplayNativeMethods.WM_KEYDOWN;
            StableReplayNativeMethods.PostMessage(BrowserHandle, message, new IntPtr(vk), IntPtr.Zero);
            FeatureDiagnostics.Log("InputMemory", $"background key; type={(keyUp ? "keyup" : "keydown")}; vk={vk}; hwnd={BrowserHandle}");
        }

        private void SendBackgroundChar(HostInputMemoryEvent item)
        {
            var vk = item.KeyCode ?? item.NativeKeyCode ?? 0;
            if (vk <= 0)
                return;

            StableReplayNativeMethods.PostMessage(BrowserHandle, StableReplayNativeMethods.WM_CHAR, new IntPtr(vk), IntPtr.Zero);
            FeatureDiagnostics.Log("InputMemory", $"background char; vk={vk}; hwnd={BrowserHandle}");
        }

        private static int GetBackgroundMouseMessage(int? button, bool mouseUp)
        {
            switch (button ?? 0)
            {
                case 1:
                    return mouseUp ? StableReplayNativeMethods.WM_MBUTTONUP : StableReplayNativeMethods.WM_MBUTTONDOWN;
                case 2:
                    return mouseUp ? StableReplayNativeMethods.WM_RBUTTONUP : StableReplayNativeMethods.WM_RBUTTONDOWN;
                default:
                    return mouseUp ? StableReplayNativeMethods.WM_LBUTTONUP : StableReplayNativeMethods.WM_LBUTTONDOWN;
            }
        }

        private static int GetBackgroundMouseWParam(int? button)
        {
            switch (button ?? 0)
            {
                case 1:
                    return StableReplayNativeMethods.MK_MBUTTON;
                case 2:
                    return StableReplayNativeMethods.MK_RBUTTON;
                default:
                    return StableReplayNativeMethods.MK_LBUTTON;
            }
        }

        private static IntPtr MakeMouseLParam(int x, int y)
        {
            return new IntPtr(((y & 0xffff) << 16) | (x & 0xffff));
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
            public const int WM_MOUSEMOVE = 0x0200;
            public const int WM_LBUTTONDOWN = 0x0201;
            public const int WM_LBUTTONUP = 0x0202;
            public const int WM_RBUTTONDOWN = 0x0204;
            public const int WM_RBUTTONUP = 0x0205;
            public const int WM_MBUTTONDOWN = 0x0207;
            public const int WM_MBUTTONUP = 0x0208;
            public const int WM_MOUSEWHEEL = 0x020A;
            public const int WM_KEYDOWN = 0x0100;
            public const int WM_KEYUP = 0x0101;
            public const int WM_CHAR = 0x0102;
            public const int MK_LBUTTON = 0x0001;
            public const int MK_RBUTTON = 0x0002;
            public const int MK_MBUTTON = 0x0010;

            [DllImport("user32.dll")]
            public static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

            [DllImport("user32.dll")]
            public static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);

            [DllImport("user32.dll")]
            public static extern bool ScreenToClient(IntPtr hwnd, ref POINT point);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

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