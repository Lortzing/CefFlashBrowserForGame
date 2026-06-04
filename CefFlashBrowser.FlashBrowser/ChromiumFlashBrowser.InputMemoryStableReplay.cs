using CefSharp;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
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

            LogReplayWindowSnapshot("stable replay started", $"count={events.Count}; speed={speed:0.###}; loopCount={loopCount}; loopIntervalMs={loopIntervalMs}; mouseMode=background-child-postmessage; coordinateMode=ratio-adaptive; version={playbackVersion}");
            LogReplayEventSample(events);
            LogReplayChildWindows();

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
                    {
                        FeatureDiagnostics.Log("InputMemory", $"stable replay interrupted before loop; loop={loop + 1}; version={playbackVersion}; currentVersion={_inputMemoryPlaybackVersion}; playing={IsInputMemoryPlaying}");
                        return;
                    }

                    _inputMemoryLoopIndex = loop + 1;
                    _inputMemoryPlayIndex = 0;
                    LogReplayWindowSnapshot("stable replay loop started", $"loop={_inputMemoryLoopIndex}; total={(loopCount <= 0 ? "infinite" : loopCount.ToString())}; version={playbackVersion}");

                    double previousTime = 0;
                    for (var i = 0; i < events.Count; i++)
                    {
                        if (playbackVersion != _inputMemoryPlaybackVersion || !IsInputMemoryPlaying)
                        {
                            FeatureDiagnostics.Log("InputMemory", $"stable replay interrupted in loop; loop={_inputMemoryLoopIndex}; eventIndex={i}; version={playbackVersion}; currentVersion={_inputMemoryPlaybackVersion}; playing={IsInputMemoryPlaying}");
                            return;
                        }

                        var item = events[i];
                        var delay = Math.Max(0, (item.Time - previousTime) / speed);
                        previousTime = Math.Max(previousTime, item.Time);
                        if (delay > 0)
                            await Task.Delay((int)Math.Min(int.MaxValue, delay));

                        if (playbackVersion != _inputMemoryPlaybackVersion || !IsInputMemoryPlaying)
                        {
                            FeatureDiagnostics.Log("InputMemory", $"stable replay interrupted after delay; loop={_inputMemoryLoopIndex}; eventIndex={i}; version={playbackVersion}; currentVersion={_inputMemoryPlaybackVersion}; playing={IsInputMemoryPlaying}");
                            return;
                        }

                        _inputMemoryPlayIndex = i + 1;
                        SendBackgroundInputEvent(item, i);
                        await RefreshInputMemoryStatusAsync();
                    }

                    FeatureDiagnostics.Log("InputMemory", $"stable replay loop completed; loop={_inputMemoryLoopIndex}; total={(loopCount <= 0 ? "infinite" : loopCount.ToString())}; version={playbackVersion}");

                    if (loop + 1 < loops && loopIntervalMs > 0)
                    {
                        FeatureDiagnostics.Log("InputMemory", $"stable replay loop interval; milliseconds={loopIntervalMs}; nextLoop={loop + 2}; version={playbackVersion}");
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
                    FeatureDiagnostics.Log("InputMemory", $"stable replay completed; version={playbackVersion}");
                }
            }
        }

        private void SendBackgroundInputEvent(HostInputMemoryEvent item, int eventIndex)
        {
            if (item == null || BrowserHandle == IntPtr.Zero)
            {
                FeatureDiagnostics.Log("InputMemory", $"background event skipped; index={eventIndex}; nullItem={item == null}; hwnd={BrowserHandle}");
                return;
            }

            var type = (item.Type ?? string.Empty).ToLowerInvariant();
            switch (type)
            {
                case "mousemove":
                    SendBackgroundMouseMove(item, eventIndex);
                    break;
                case "mousedown":
                    SendBackgroundMouseButton(item, false, eventIndex);
                    break;
                case "mouseup":
                    SendBackgroundMouseButton(item, true, eventIndex);
                    break;
                case "click":
                    SendBackgroundMouseButton(item, false, eventIndex);
                    SendBackgroundMouseButton(item, true, eventIndex);
                    break;
                case "dblclick":
                    SendBackgroundMouseButton(item, false, eventIndex);
                    SendBackgroundMouseButton(item, true, eventIndex);
                    SendBackgroundMouseButton(item, false, eventIndex);
                    SendBackgroundMouseButton(item, true, eventIndex);
                    break;
                case "wheel":
                    SendBackgroundMouseWheel(item, eventIndex);
                    break;
                case "keydown":
                    SendBackgroundKey(item, false, eventIndex);
                    break;
                case "keyup":
                    SendBackgroundKey(item, true, eventIndex);
                    break;
                case "char":
                    SendBackgroundChar(item, eventIndex);
                    break;
                default:
                    FeatureDiagnostics.Log("InputMemory", $"background event skipped; index={eventIndex}; unknownType={type}; rawX={item.X}; rawY={item.Y}; ratioX={item.RatioX}; ratioY={item.RatioY}");
                    break;
            }
        }

        private void SendBackgroundMouseMove(HostInputMemoryEvent item, int eventIndex)
        {
            var target = GetReplayTargetHandle(out var targetClass);
            var point = ResolveTargetClientPoint(item, eventIndex, "mousemove", target, targetClass);
            var lParam = MakeMouseLParam(point.X, point.Y);
            var ok = StableReplayNativeMethods.PostMessage(target, StableReplayNativeMethods.WM_MOUSEMOVE, IntPtr.Zero, lParam);
            LogPostMessage("mousemove", eventIndex, item, target, targetClass, StableReplayNativeMethods.WM_MOUSEMOVE, IntPtr.Zero, lParam, point, ok);
        }

        private void SendBackgroundMouseButton(HostInputMemoryEvent item, bool mouseUp, int eventIndex)
        {
            var target = GetReplayTargetHandle(out var targetClass);
            var point = ResolveTargetClientPoint(item, eventIndex, mouseUp ? "mouseup" : "mousedown", target, targetClass);
            var lParam = MakeMouseLParam(point.X, point.Y);
            var message = GetBackgroundMouseMessage(item.Button, mouseUp);
            var wParam = new IntPtr(mouseUp ? 0 : GetBackgroundMouseWParam(item.Button));
            var moveOk = StableReplayNativeMethods.PostMessage(target, StableReplayNativeMethods.WM_MOUSEMOVE, IntPtr.Zero, lParam);
            var clickOk = StableReplayNativeMethods.PostMessage(target, message, wParam, lParam);
            LogPostMessage("mousemove-before-button", eventIndex, item, target, targetClass, StableReplayNativeMethods.WM_MOUSEMOVE, IntPtr.Zero, lParam, point, moveOk);
            LogPostMessage(mouseUp ? "mouseup" : "mousedown", eventIndex, item, target, targetClass, message, wParam, lParam, point, clickOk);
        }

        private void SendBackgroundMouseWheel(HostInputMemoryEvent item, int eventIndex)
        {
            var target = GetReplayTargetHandle(out var targetClass);
            var point = ResolveTargetClientPoint(item, eventIndex, "wheel", target, targetClass);
            var screenPoint = point;
            StableReplayNativeMethods.ClientToScreen(target, ref screenPoint);
            var wParam = new IntPtr(((int)item.DeltaY << 16) & unchecked((int)0xffff0000));
            var lParam = MakeMouseLParam(screenPoint.X, screenPoint.Y);
            var ok = StableReplayNativeMethods.PostMessage(target, StableReplayNativeMethods.WM_MOUSEWHEEL, wParam, lParam);
            LogPostMessage("wheel", eventIndex, item, target, targetClass, StableReplayNativeMethods.WM_MOUSEWHEEL, wParam, lParam, point, ok, $"screenX={screenPoint.X}; screenY={screenPoint.Y}; delta={item.DeltaY}");
        }

        private void SendBackgroundKey(HostInputMemoryEvent item, bool keyUp, int eventIndex)
        {
            var vk = item.KeyCode > 0 ? item.KeyCode : item.NativeKeyCode;
            if (vk <= 0)
            {
                FeatureDiagnostics.Log("InputMemory", $"background key skipped; index={eventIndex}; type={(keyUp ? "keyup" : "keydown")}; keyCode={item.KeyCode}; nativeKeyCode={item.NativeKeyCode}; hwnd={BrowserHandle}");
                return;
            }

            if (IsInputMacroControlVirtualKey(vk))
            {
                FeatureDiagnostics.Log("InputMemory", $"background key skipped macro-control; index={eventIndex}; type={(keyUp ? "keyup" : "keydown")}; vk={vk}; keyCode={item.KeyCode}; nativeKeyCode={item.NativeKeyCode}; hwnd={BrowserHandle}");
                return;
            }

            var target = GetReplayTargetHandle(out var targetClass);
            var message = keyUp ? StableReplayNativeMethods.WM_KEYUP : StableReplayNativeMethods.WM_KEYDOWN;
            var ok = StableReplayNativeMethods.PostMessage(target, message, new IntPtr(vk), IntPtr.Zero);
            LogPostMessage(keyUp ? "keyup" : "keydown", eventIndex, item, target, targetClass, message, new IntPtr(vk), IntPtr.Zero, null, ok, $"vk={vk}");
        }

        private void SendBackgroundChar(HostInputMemoryEvent item, int eventIndex)
        {
            var vk = item.KeyCode > 0 ? item.KeyCode : item.NativeKeyCode;
            if (vk <= 0)
            {
                FeatureDiagnostics.Log("InputMemory", $"background char skipped; index={eventIndex}; keyCode={item.KeyCode}; nativeKeyCode={item.NativeKeyCode}; hwnd={BrowserHandle}");
                return;
            }

            if (IsInputMacroControlVirtualKey(vk))
            {
                FeatureDiagnostics.Log("InputMemory", $"background char skipped macro-control; index={eventIndex}; vk={vk}; hwnd={BrowserHandle}");
                return;
            }

            var target = GetReplayTargetHandle(out var targetClass);
            var ok = StableReplayNativeMethods.PostMessage(target, StableReplayNativeMethods.WM_CHAR, new IntPtr(vk), IntPtr.Zero);
            LogPostMessage("char", eventIndex, item, target, targetClass, StableReplayNativeMethods.WM_CHAR, new IntPtr(vk), IntPtr.Zero, null, ok, $"vk={vk}");
        }

        private static bool IsInputMacroControlVirtualKey(int vk)
        {
            return vk == 113;
        }

        private static int GetBackgroundMouseMessage(int? button, bool mouseUp)
        {
            switch (button ?? 0)
            {
                case 1: return mouseUp ? StableReplayNativeMethods.WM_MBUTTONUP : StableReplayNativeMethods.WM_MBUTTONDOWN;
                case 2: return mouseUp ? StableReplayNativeMethods.WM_RBUTTONUP : StableReplayNativeMethods.WM_RBUTTONDOWN;
                default: return mouseUp ? StableReplayNativeMethods.WM_LBUTTONUP : StableReplayNativeMethods.WM_LBUTTONDOWN;
            }
        }

        private static int GetBackgroundMouseWParam(int? button)
        {
            switch (button ?? 0)
            {
                case 1: return StableReplayNativeMethods.MK_MBUTTON;
                case 2: return StableReplayNativeMethods.MK_RBUTTON;
                default: return StableReplayNativeMethods.MK_LBUTTON;
            }
        }

        private static IntPtr MakeMouseLParam(int x, int y)
        {
            return new IntPtr(((y & 0xffff) << 16) | (x & 0xffff));
        }

        private StableReplayNativeMethods.POINT ResolveBrowserClientPoint(HostInputMemoryEvent item, int eventIndex, string reason)
        {
            var size = GetBrowserClientSize();
            if (item.RatioX.HasValue && item.RatioY.HasValue && size.Width > 0 && size.Height > 0)
            {
                var ratio = new StableReplayNativeMethods.POINT
                {
                    X = ClampToClient((int)Math.Round(item.RatioX.Value * size.Width), size.Width),
                    Y = ClampToClient((int)Math.Round(item.RatioY.Value * size.Height), size.Height)
                };
                FeatureDiagnostics.Log("InputMemory", $"resolve browser client; index={eventIndex}; reason={reason}; source=ratio-adaptive; rawX={item.X}; rawY={item.Y}; ratioX={item.RatioX}; ratioY={item.RatioY}; clientX={ratio.X}; clientY={ratio.Y}; browserClientSize={size.Width}x{size.Height}; browserHwnd={BrowserHandle}");
                return ratio;
            }

            if (item.X.HasValue && item.Y.HasValue)
            {
                var direct = new StableReplayNativeMethods.POINT
                {
                    X = ClampToClient((int)Math.Round(item.X.Value), size.Width),
                    Y = ClampToClient((int)Math.Round(item.Y.Value), size.Height)
                };
                FeatureDiagnostics.Log("InputMemory", $"resolve browser client; index={eventIndex}; reason={reason}; source=direct-client-x-y-fallback; rawX={item.X}; rawY={item.Y}; ratioX={item.RatioX}; ratioY={item.RatioY}; clientX={direct.X}; clientY={direct.Y}; browserClientSize={size.Width}x{size.Height}; browserHwnd={BrowserHandle}");
                return direct;
            }

            FeatureDiagnostics.Log("InputMemory", $"resolve browser client; index={eventIndex}; reason={reason}; source=empty; rawX={item.X}; rawY={item.Y}; ratioX={item.RatioX}; ratioY={item.RatioY}; browserClientSize={size.Width}x{size.Height}; browserHwnd={BrowserHandle}");
            return new StableReplayNativeMethods.POINT { X = 0, Y = 0 };
        }

        private StableReplayNativeMethods.POINT ResolveTargetClientPoint(HostInputMemoryEvent item, int eventIndex, string reason, IntPtr target, string targetClass)
        {
            var browserPoint = ResolveBrowserClientPoint(item, eventIndex, reason);
            if (target == BrowserHandle || target == IntPtr.Zero)
                return browserPoint;

            var screenPoint = browserPoint;
            var toScreenOk = StableReplayNativeMethods.ClientToScreen(BrowserHandle, ref screenPoint);
            var targetPoint = screenPoint;
            var toTargetOk = StableReplayNativeMethods.ScreenToClient(target, ref targetPoint);
            FeatureDiagnostics.Log("InputMemory", $"resolve target client; index={eventIndex}; reason={reason}; browserClientX={browserPoint.X}; browserClientY={browserPoint.Y}; screenX={screenPoint.X}; screenY={screenPoint.Y}; targetClientX={targetPoint.X}; targetClientY={targetPoint.Y}; toScreenOk={toScreenOk}; toTargetOk={toTargetOk}; target={target}; targetClass={targetClass}");
            return targetPoint;
        }

        private IntPtr GetReplayTargetHandle(out string targetClass)
        {
            targetClass = GetWindowClassName(BrowserHandle);
            var best = BrowserHandle;
            var bestClass = targetClass;
            var bestScore = 0;
            foreach (var child in EnumerateChildWindows(BrowserHandle))
            {
                var cls = GetWindowClassName(child);
                var score = GetReplayTargetScore(cls);
                if (score > bestScore)
                {
                    best = child;
                    bestClass = cls;
                    bestScore = score;
                }
            }
            targetClass = bestClass;
            return best;
        }

        private static int GetReplayTargetScore(string className)
        {
            if (string.IsNullOrEmpty(className)) return 0;
            if (className.IndexOf("Chrome_RenderWidgetHostHWND", StringComparison.OrdinalIgnoreCase) >= 0) return 100;
            if (className.IndexOf("Chrome_RenderWidget", StringComparison.OrdinalIgnoreCase) >= 0) return 95;
            if (className.IndexOf("CefBrowserWindow", StringComparison.OrdinalIgnoreCase) >= 0) return 80;
            if (className.IndexOf("Chrome_WidgetWin", StringComparison.OrdinalIgnoreCase) >= 0) return 60;
            return 0;
        }

        private IEnumerable<IntPtr> EnumerateChildWindows(IntPtr root)
        {
            var result = new List<IntPtr>();
            if (root == IntPtr.Zero) return result;
            StableReplayNativeMethods.EnumChildWindows(root, (hwnd, lParam) =>
            {
                result.Add(hwnd);
                return true;
            }, IntPtr.Zero);
            return result;
        }

        private string GetWindowClassName(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return string.Empty;
            var builder = new StringBuilder(256);
            var length = StableReplayNativeMethods.GetClassName(hwnd, builder, builder.Capacity);
            return length > 0 ? builder.ToString() : string.Empty;
        }

        private void LogReplayChildWindows()
        {
            var children = EnumerateChildWindows(BrowserHandle).Take(30).ToList();
            FeatureDiagnostics.Log("InputMemory", $"replay child windows; browserHwnd={BrowserHandle}; browserClass={GetWindowClassName(BrowserHandle)}; count={children.Count}; children={string.Join(" | ", children.Select(hwnd => hwnd + ":" + GetWindowClassName(hwnd)))}");
            var target = GetReplayTargetHandle(out var targetClass);
            FeatureDiagnostics.Log("InputMemory", $"replay target selected; target={target}; targetClass={targetClass}; browserHwnd={BrowserHandle}; targetRect={GetWindowRectText(target)}; browserRect={GetWindowRectText(BrowserHandle)}");
        }

        private void LogPostMessage(string action, int eventIndex, HostInputMemoryEvent item, IntPtr target, string targetClass, int msg, IntPtr wParam, IntPtr lParam, StableReplayNativeMethods.POINT? point, bool ok, string extra = null)
        {
            var error = ok ? 0 : Marshal.GetLastWin32Error();
            var clientText = point.HasValue ? $"clientX={point.Value.X}; clientY={point.Value.Y};" : string.Empty;
            FeatureDiagnostics.Log("InputMemory", $"post message; action={action}; index={eventIndex}; ok={ok}; error={error}; msg=0x{msg:X}; wParam={wParam}; lParam={lParam}; {clientText} rawX={item?.X}; rawY={item?.Y}; ratioX={item?.RatioX}; ratioY={item?.RatioY}; button={item?.Button}; target={target}; targetClass={targetClass}; targetRect={GetWindowRectText(target)}; browserHwnd={BrowserHandle}; browserRect={GetWindowRectText(BrowserHandle)}; {extra}");
        }

        private void LogReplayWindowSnapshot(string title, string extra)
        {
            FeatureDiagnostics.Log("InputMemory", $"{title}; hwnd={BrowserHandle}; class={GetWindowClassName(BrowserHandle)}; clientSize={GetBrowserClientSize().Width}x{GetBrowserClientSize().Height}; rect={GetWindowRectText(BrowserHandle)}; {extra}");
        }

        private void LogReplayEventSample(IList<HostInputMemoryEvent> events)
        {
            for (var i = 0; i < Math.Min(events.Count, 8); i++)
            {
                var item = events[i];
                FeatureDiagnostics.Log("InputMemory", $"replay sample; index={i}; type={item.Type}; time={item.Time:0}; rawX={item.X}; rawY={item.Y}; ratioX={item.RatioX}; ratioY={item.RatioY}; button={item.Button}; key={item.KeyCode}; nativeKey={item.NativeKeyCode}");
            }
            if (events.Count > 8)
                FeatureDiagnostics.Log("InputMemory", $"replay sample truncated; total={events.Count}");
        }

        private string GetWindowRectText(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return "hwnd=0";

            StableReplayNativeMethods.RECT rect;
            if (!StableReplayNativeMethods.GetWindowRect(hwnd, out rect))
                return "GetWindowRect=false";

            return $"L={rect.Left},T={rect.Top},R={rect.Right},B={rect.Bottom},W={rect.Right - rect.Left},H={rect.Bottom - rect.Top}";
        }

        private static int ClampToClient(int value, int max)
        {
            if (max <= 0) return Math.Max(0, value);
            if (value < 0) return 0;
            if (value >= max) return max - 1;
            return value;
        }

        private BrowserClientSize GetBrowserClientSize()
        {
            if (BrowserHandle != IntPtr.Zero)
            {
                StableReplayNativeMethods.RECT rect;
                if (StableReplayNativeMethods.GetClientRect(BrowserHandle, out rect))
                    return new BrowserClientSize(Math.Max(1, rect.Right - rect.Left), Math.Max(1, rect.Bottom - rect.Top));
            }
            return new BrowserClientSize((int)Math.Max(1, ActualWidth), (int)Math.Max(1, ActualHeight));
        }

        private void FillBrowserRatios(HostInputMemoryEvent item)
        {
            if (item == null || !item.X.HasValue || !item.Y.HasValue) return;
            var size = GetBrowserClientSize();
            if (size.Width > 0) item.RatioX = Math.Max(0, Math.Min(1, item.X.Value / size.Width));
            if (size.Height > 0) item.RatioY = Math.Max(0, Math.Min(1, item.Y.Value / size.Height));
        }

        private readonly struct BrowserClientSize
        {
            public BrowserClientSize(int width, int height) { Width = width; Height = height; }
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

            public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

            [DllImport("user32.dll")]
            public static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

            [DllImport("user32.dll")]
            public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

            [DllImport("user32.dll")]
            public static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);

            [DllImport("user32.dll")]
            public static extern bool ScreenToClient(IntPtr hwnd, ref POINT point);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll")]
            public static extern bool EnumChildWindows(IntPtr hwndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

            [DllImport("user32.dll", CharSet = CharSet.Auto)]
            public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

            [StructLayout(LayoutKind.Sequential)]
            public struct POINT { public int X; public int Y; }

            [StructLayout(LayoutKind.Sequential)]
            public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
        }
    }
}