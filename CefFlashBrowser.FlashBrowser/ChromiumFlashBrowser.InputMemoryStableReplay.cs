using CefSharp;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

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

            FeatureDiagnostics.Log("InputMemory", $"stable replay started; count={events.Count}; speed={speed:0.###}; loopCount={loopCount}; loopIntervalMs={loopIntervalMs}");

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

            var clientPoint = ResolveStableClientPoint(item);
            var screenPoint = ResolveStableScreenPoint(item, clientPoint);
            var modifiers = GetEventFlags(item);
            var mouseEvent = new MouseEvent(clientPoint.X, clientPoint.Y, modifiers);
            var type = (item.Type ?? string.Empty).ToLowerInvariant();

            switch (type)
            {
                case "mousemove":
                    host.SendMouseMoveEvent(mouseEvent, false);
                    break;
                case "mousedown":
                    ShowStableReplayClickMarker(screenPoint.X, screenPoint.Y, false);
                    FeatureDiagnostics.Log("InputMemory", $"stable replay marker; type=mousedown; clientX={clientPoint.X}; clientY={clientPoint.Y}; screenX={screenPoint.X}; screenY={screenPoint.Y}; rx={item.RatioX:0.####}; ry={item.RatioY:0.####}");
                    host.SendMouseMoveEvent(mouseEvent, false);
                    host.SendMouseClickEvent(mouseEvent, GetMouseButton(item.Button), false, 1);
                    break;
                case "mouseup":
                    ShowStableReplayClickMarker(screenPoint.X, screenPoint.Y, true);
                    FeatureDiagnostics.Log("InputMemory", $"stable replay marker; type=mouseup; clientX={clientPoint.X}; clientY={clientPoint.Y}; screenX={screenPoint.X}; screenY={screenPoint.Y}; rx={item.RatioX:0.####}; ry={item.RatioY:0.####}");
                    host.SendMouseMoveEvent(mouseEvent, false);
                    host.SendMouseClickEvent(mouseEvent, GetMouseButton(item.Button), true, 1);
                    break;
                case "click":
                    ShowStableReplayClickMarker(screenPoint.X, screenPoint.Y, false);
                    host.SendMouseMoveEvent(mouseEvent, false);
                    host.SendMouseClickEvent(mouseEvent, GetMouseButton(item.Button), false, 1);
                    host.SendMouseClickEvent(mouseEvent, GetMouseButton(item.Button), true, 1);
                    break;
                case "dblclick":
                    ShowStableReplayClickMarker(screenPoint.X, screenPoint.Y, false);
                    host.SendMouseMoveEvent(mouseEvent, false);
                    host.SendMouseClickEvent(mouseEvent, GetMouseButton(item.Button), false, 2);
                    host.SendMouseClickEvent(mouseEvent, GetMouseButton(item.Button), true, 2);
                    break;
                case "wheel":
                    ShowStableReplayClickMarker(screenPoint.X, screenPoint.Y, true);
                    host.SendMouseMoveEvent(mouseEvent, false);
                    host.SendMouseWheelEvent(mouseEvent, (int)item.DeltaX, (int)item.DeltaY);
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

        private void ShowStableReplayClickMarker(int screenX, int screenY, bool release)
        {
            const int size = 28;
            var window = new Window
            {
                Width = size,
                Height = size,
                Left = screenX - size / 2.0,
                Top = screenY - size / 2.0,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Topmost = true,
                ShowInTaskbar = false,
                ShowActivated = false,
                IsHitTestVisible = false,
                Content = new Grid
                {
                    Children =
                    {
                        new Ellipse
                        {
                            Stroke = release ? Brushes.DeepSkyBlue : Brushes.Red,
                            StrokeThickness = 3,
                            Fill = Brushes.Transparent,
                            Width = size - 4,
                            Height = size - 4,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center
                        }
                    }
                }
            };

            window.Show();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(360) };
            timer.Tick += delegate
            {
                timer.Stop();
                window.Close();
            };
            timer.Start();
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
            [DllImport("user32.dll")]
            public static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

            [DllImport("user32.dll")]
            public static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);

            [DllImport("user32.dll")]
            public static extern bool ScreenToClient(IntPtr hwnd, ref POINT point);

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