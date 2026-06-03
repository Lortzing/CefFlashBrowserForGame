using CefSharp;
using CefFlashBrowser.WinformCefSharp4WPF;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace CefFlashBrowser.FlashBrowser
{
    public class ChromiumFlashBrowser : ChromiumWebBrowserEx
    {
        public static readonly DependencyProperty BlockedSwfsProperty;
        public static readonly DependencyProperty HasBlockedSwfsProperty;
        public static readonly DependencyProperty InputMemoryStatusTextProperty;

        private const string InputMemoryBootstrapScript = @"
(function() {
    if (window.__cefInputMemory) return;
    var memory = {
        recording: false,
        playing: false,
        events: [],
        timers: [],
        playIndex: 0,
        playTotal: 0,
        loopIndex: 0,
        loopTotal: 0,
        startTime: 0,
        startedAt: null,
        lastMoveTime: 0,
        lastMoveX: null,
        lastMoveY: null,
        start: function() {
            this.stopPlay();
            this.events = [];
            this.startTime = Date.now();
            this.startedAt = document.activeElement;
            this.recording = true;
            this.lastMoveTime = 0;
            this.lastMoveX = null;
            this.lastMoveY = null;
        },
        stop: function() {
            this.recording = false;
        },
        clear: function() {
            this.stopPlay();
            this.events = [];
            this.recording = false;
        },
        state: function() {
            return {
                recording: this.recording,
                playing: this.playing,
                count: this.events.length,
                playIndex: this.playIndex,
                playTotal: this.playTotal,
                loopIndex: this.loopIndex,
                loopTotal: this.loopTotal
            };
        },
        exportEvents: function() {
            return this.events.slice(0);
        },
        importEvents: function(events) {
            this.stopPlay();
            this.recording = false;
            this.events = Array.isArray(events) ? events.slice(0).sort(function(a, b) {
                return (a.time || 0) - (b.time || 0);
            }) : [];
        },
        record: function(e) {
            if (!this.recording || !e || !e.type) return;
            var now = Date.now();
            if (e.type === 'mousemove') {
                var x = typeof e.clientX === 'number' ? e.clientX : null;
                var y = typeof e.clientY === 'number' ? e.clientY : null;
                if (this.lastMoveTime && now - this.lastMoveTime < 30) return;
                if (x !== null && y !== null && this.lastMoveX !== null && this.lastMoveY !== null) {
                    var dx = x - this.lastMoveX;
                    var dy = y - this.lastMoveY;
                    if (dx * dx + dy * dy < 4) return;
                }
                this.lastMoveTime = now;
                this.lastMoveX = x;
                this.lastMoveY = y;
            }
            this.events.push({
                type: e.type,
                time: now - this.startTime,
                x: typeof e.clientX === 'number' ? e.clientX : null,
                y: typeof e.clientY === 'number' ? e.clientY : null,
                ratioX: typeof e.clientX === 'number' && window.innerWidth ? e.clientX / window.innerWidth : null,
                ratioY: typeof e.clientY === 'number' && window.innerHeight ? e.clientY / window.innerHeight : null,
                button: typeof e.button === 'number' ? e.button : 0,
                buttons: typeof e.buttons === 'number' ? e.buttons : 0,
                deltaX: typeof e.deltaX === 'number' ? e.deltaX : 0,
                deltaY: typeof e.deltaY === 'number' ? e.deltaY : 0,
                key: e.key || '',
                code: e.code || '',
                keyCode: e.keyCode || 0,
                ctrlKey: !!e.ctrlKey,
                shiftKey: !!e.shiftKey,
                altKey: !!e.altKey,
                metaKey: !!e.metaKey
            });
        },
        targetFor: function(item) {
            var x = item.x;
            var y = item.y;
            if ((x === null || y === null || typeof x !== 'number' || typeof y !== 'number')
                && typeof item.ratioX === 'number' && typeof item.ratioY === 'number') {
                x = item.ratioX * window.innerWidth;
                y = item.ratioY * window.innerHeight;
            }
            if (x !== null && y !== null && typeof x === 'number' && typeof y === 'number') {
                return document.elementFromPoint(x, y) || document.body || document.documentElement;
            }
            return this.startedAt || document.activeElement || document.body || document.documentElement;
        },
        dispatch: function(item) {
            var x = item.x;
            var y = item.y;
            if ((x === null || y === null || typeof x !== 'number' || typeof y !== 'number')
                && typeof item.ratioX === 'number' && typeof item.ratioY === 'number') {
                x = item.ratioX * window.innerWidth;
                y = item.ratioY * window.innerHeight;
            }
            var target = this.targetFor(item);
            var init = {
                bubbles: true,
                cancelable: true,
                composed: true,
                clientX: x || 0,
                clientY: y || 0,
                button: item.button || 0,
                buttons: item.buttons || 0,
                deltaX: item.deltaX || 0,
                deltaY: item.deltaY || 0,
                key: item.key || '',
                code: item.code || '',
                keyCode: item.keyCode || 0,
                which: item.keyCode || 0,
                ctrlKey: !!item.ctrlKey,
                shiftKey: !!item.shiftKey,
                altKey: !!item.altKey,
                metaKey: !!item.metaKey
            };
            var evt;
            if (item.type.indexOf('key') === 0) {
                evt = new KeyboardEvent(item.type, init);
            } else if (item.type === 'wheel') {
                evt = new WheelEvent(item.type, init);
            } else {
                evt = new MouseEvent(item.type, init);
            }
            target.dispatchEvent(evt);
        },
        stopPlay: function() {
            this.timers.forEach(function(timer) { window.clearTimeout(timer); });
            this.timers = [];
            this.playing = false;
            this.playIndex = 0;
            this.playTotal = 0;
            this.loopIndex = 0;
            this.loopTotal = 0;
        },
        play: function(options) {
            options = options || {};
            this.stopPlay();
            var list = Array.isArray(options.events) ? options.events.slice(0) : this.events.slice(0);
            if (!list.length) return;
            this.recording = false;
            this.playing = true;
            this.playTotal = list.length;
            this.loopTotal = typeof options.loopCount === 'number' ? options.loopCount : 1;
            var speed = Math.max(0.1, typeof options.speed === 'number' ? options.speed : 1);
            var loopInterval = Math.max(0, typeof options.loopInterval === 'number' ? options.loopInterval : 0);
            var loopCount = this.loopTotal <= 0 ? Number.MAX_SAFE_INTEGER : this.loopTotal;

            var scheduleLoop = function(loop) {
                if (!memory.playing || loop >= loopCount) {
                    memory.stopPlay();
                    return;
                }
                memory.loopIndex = loop + 1;
                list.forEach(function(item, index) {
                    var timer = window.setTimeout(function() {
                        if (!memory.playing) return;
                        memory.playIndex = index + 1;
                        memory.dispatch(item);
                    }, Math.max(0, (item.time || 0) / speed));
                    memory.timers.push(timer);
                });

                var duration = list.length ? Math.max(0, list[list.length - 1].time || 0) / speed : 0;
                var nextTimer = window.setTimeout(function() {
                    scheduleLoop(loop + 1);
                }, duration + loopInterval);
                memory.timers.push(nextTimer);
            };
            scheduleLoop(0);
        }
    };

    ['mousedown','mouseup','mousemove','click','dblclick','wheel','keydown','keyup'].forEach(function(type) {
        document.addEventListener(type, function(e) { memory.record(e); }, true);
    });
    window.__cefInputMemory = memory;
})();";

        static ChromiumFlashBrowser()
        {
            BlockedSwfsProperty = DependencyProperty.Register(
                nameof(BlockedSwfs), typeof(ObservableCollection<string>), typeof(ChromiumFlashBrowser), new PropertyMetadata(null));

            HasBlockedSwfsProperty = DependencyProperty.Register(
                nameof(HasBlockedSwfs), typeof(bool), typeof(ChromiumFlashBrowser), new PropertyMetadata(false));

            InputMemoryStatusTextProperty = DependencyProperty.Register(
                nameof(InputMemoryStatusText), typeof(string), typeof(ChromiumFlashBrowser), new PropertyMetadata(string.Empty));
        }

        public ChromiumFlashBrowser()
        {
            SetValue(BlockedSwfsProperty, new ObservableCollection<string>());
            NativeMessageReceived += OnNativeMessageReceived;
        }


        public ObservableCollection<string> BlockedSwfs
        {
            get => (ObservableCollection<string>)GetValue(BlockedSwfsProperty);
        }

        public bool HasBlockedSwfs
        {
            get => (bool)GetValue(HasBlockedSwfsProperty);
        }

        public bool IsInputMemoryRecording { get; private set; }

        public bool IsInputMemoryPlaying { get; private set; }

        public int InputMemoryEventCount { get; private set; }

        public event EventHandler<InputMemoryNativeKeyEventArgs> InputMemoryNativeKeyDown;

        private readonly List<HostInputMemoryEvent> _inputMemoryEvents = new List<HostInputMemoryEvent>();
        private readonly Stopwatch _inputMemoryStopwatch = new Stopwatch();
        private int _inputMemoryPlaybackVersion;
        private DateTime _lastInputMemoryFailureLogTime = DateTime.MinValue;
        private int _inputMemoryPlayIndex;
        private int _inputMemoryPlayTotal;
        private int _inputMemoryLoopIndex;
        private int _inputMemoryLoopTotal;
        private int _lastInputMemoryMoveTime;
        private int? _lastInputMemoryMoveX;
        private int? _lastInputMemoryMoveY;

        public string InputMemoryStatusText
        {
            get => (string)GetValue(InputMemoryStatusTextProperty);
        }

        public double SpeedGearFactor => SpeedGearController.CurrentFactor;

        public void SetSpeedGearFactor(double factor)
        {
            SpeedGearController.SetFactor(factor);
        }

        public void ResetSpeedGear()
        {
            SetSpeedGearFactor(SpeedGearController.DefaultFactor);
        }

        public void StartInputMemoryRecording()
        {
            if (!IsBrowserInitialized)
            {
                SetInputMemoryStatus("浏览器尚未就绪，不能开始录制");
                FeatureDiagnostics.Log("InputMemory", "start recording rejected: browser is not initialized");
                return;
            }

            FeatureDiagnostics.Log("InputMemory", "start recording requested; backend=cef-host-input");
            IsInputMemoryRecording = true;
            IsInputMemoryPlaying = false;
            _inputMemoryPlaybackVersion++;
            _inputMemoryEvents.Clear();
            _inputMemoryStopwatch.Restart();
            _lastInputMemoryMoveTime = 0;
            _lastInputMemoryMoveX = null;
            _lastInputMemoryMoveY = null;
            InputMemoryEventCount = 0;
            SetInputMemoryStatus("正在录制 00:00，已记录 0 个事件");
        }

        public void StopInputMemoryRecording()
        {
            FeatureDiagnostics.Log("InputMemory", $"stop recording requested; count={InputMemoryEventCount}");
            IsInputMemoryRecording = false;
            _inputMemoryStopwatch.Stop();
            _ = RefreshInputMemoryStatusAsync();
        }

        public void ReplayInputMemory()
        {
            _ = ReplayInputMemoryAsync();
        }

        public async Task ReplayInputMemoryAsync(double speed = 1.0, int loopCount = 1, int loopIntervalMs = 0, int countdownSeconds = 3)
        {
            IsInputMemoryRecording = false;
            if (!IsBrowserInitialized)
            {
                FeatureDiagnostics.Log("InputMemory", "replay rejected: browser is not initialized");
                return;
            }

            var events = _inputMemoryEvents
                .OrderBy(item => item.Time)
                .ToList();

            if (events.Count <= 0)
            {
                SetInputMemoryStatus("当前脚本为空，不能回放");
                FeatureDiagnostics.Log("InputMemory", "replay rejected: event list is empty");
                return;
            }

            FeatureDiagnostics.Log("InputMemory", $"replay requested; count={InputMemoryEventCount} speed={speed:0.###} loopCount={loopCount} loopIntervalMs={loopIntervalMs}");
            var playbackVersion = ++_inputMemoryPlaybackVersion;
            IsInputMemoryPlaying = true;
            _inputMemoryPlayIndex = 0;
            _inputMemoryPlayTotal = events.Count;
            _inputMemoryLoopIndex = 0;
            _inputMemoryLoopTotal = loopCount;
            for (var i = countdownSeconds; i > 0; i--)
            {
                SetInputMemoryStatus($"回放将在 {i} 秒后开始");
                await Task.Delay(1000);
                if (playbackVersion != _inputMemoryPlaybackVersion || !IsInputMemoryPlaying)
                    return;
            }

            SetInputMemoryStatus($"回放中 1/{Math.Max(loopCount, 1)}，按 Esc 停止");
            await ReplayHostInputEventsAsync(events, speed, loopCount, loopIntervalMs, playbackVersion);
        }

        public void StopInputMemoryPlayback()
        {
            FeatureDiagnostics.Log("InputMemory", $"stop playback requested; count={InputMemoryEventCount}");
            _inputMemoryPlaybackVersion++;
            IsInputMemoryPlaying = false;
            SetInputMemoryStatus($"已停止，当前脚本 {InputMemoryEventCount} 个事件");
        }

        public void ClearInputMemory()
        {
            FeatureDiagnostics.Log("InputMemory", $"clear requested; count={InputMemoryEventCount}");
            IsInputMemoryRecording = false;
            IsInputMemoryPlaying = false;
            _inputMemoryPlaybackVersion++;
            _inputMemoryEvents.Clear();
            InputMemoryEventCount = 0;
            SetInputMemoryStatus("当前脚本为空");
        }

        public async Task<string> ExportInputMemoryEventsJsonAsync()
        {
            await RefreshInputMemoryStatusAsync();
            var json = JsonConvert.SerializeObject(_inputMemoryEvents);
            await RefreshInputMemoryStatusAsync();
            FeatureDiagnostics.Log("InputMemory", $"export completed; count={InputMemoryEventCount}");
            return string.IsNullOrWhiteSpace(json) ? "[]" : json;
        }

        public async Task ImportInputMemoryEventsJsonAsync(string eventsJson)
        {
            FeatureDiagnostics.Log("InputMemory", $"import requested; jsonLength={(eventsJson ?? string.Empty).Length}");
            var events = JsonConvert.DeserializeObject<List<HostInputMemoryEvent>>(eventsJson ?? "[]")
                ?? new List<HostInputMemoryEvent>();

            _inputMemoryEvents.Clear();
            _inputMemoryEvents.AddRange(events.OrderBy(item => item.Time));
            InputMemoryEventCount = _inputMemoryEvents.Count;
            await RefreshInputMemoryStatusAsync();
        }

        public Task RefreshInputMemoryStatusAsync()
        {
            InputMemoryEventCount = _inputMemoryEvents.Count;

            if (IsInputMemoryRecording)
            {
                SetInputMemoryStatus($"正在录制，已记录 {InputMemoryEventCount} 个事件");
            }
            else if (IsInputMemoryPlaying)
            {
                var loopText = _inputMemoryLoopTotal <= 0 ? $"{_inputMemoryLoopIndex}/∞" : $"{_inputMemoryLoopIndex}/{_inputMemoryLoopTotal}";
                SetInputMemoryStatus($"回放中 {loopText}，事件 {_inputMemoryPlayIndex}/{_inputMemoryPlayTotal}，按 Esc 停止");
            }
            else if (InputMemoryEventCount > 0)
            {
                SetInputMemoryStatus($"当前脚本 {InputMemoryEventCount} 个事件");
            }
            else
            {
                SetInputMemoryStatus("当前脚本为空");
            }
            return Task.FromResult(0);
        }

        private void LogInputMemoryFailure(string message, Exception exception = null)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastInputMemoryFailureLogTime).TotalSeconds < 5)
                return;

            _lastInputMemoryFailureLogTime = now;
            FeatureDiagnostics.Log("InputMemory", message, exception);
        }

        private async Task ReplayHostInputEventsAsync(
            IList<HostInputMemoryEvent> events,
            double speed,
            int loopCount,
            int loopIntervalMs,
            int playbackVersion)
        {
            speed = Math.Max(0.1, speed);
            loopIntervalMs = Math.Max(0, loopIntervalMs);
            var loops = loopCount <= 0 ? int.MaxValue : loopCount;

            try
            {
                var host = GetBrowser()?.GetHost();
                if (host == null)
                {
                    SetInputMemoryStatus("浏览器输入通道不可用，不能回放");
                    FeatureDiagnostics.Log("InputMemory", "replay failed: browser host is null");
                    return;
                }

                host.SetFocus(true);
                for (var loop = 0; loop < loops; loop++)
                {
                    _inputMemoryLoopIndex = loop + 1;
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
                        await Dispatcher.InvokeAsync(() => SendHostInputEvent(item));
                        await RefreshInputMemoryStatusAsync();
                    }

                    if (loop + 1 < loops && loopIntervalMs > 0)
                        await Task.Delay(loopIntervalMs);
                }
            }
            catch (Exception e)
            {
                LogInputMemoryFailure("replay failed", e);
            }
            finally
            {
                if (playbackVersion == _inputMemoryPlaybackVersion)
                {
                    IsInputMemoryPlaying = false;
                    _inputMemoryPlayIndex = 0;
                    _inputMemoryLoopIndex = 0;
                    await RefreshInputMemoryStatusAsync();
                }
            }
        }

        private void SendHostInputEvent(HostInputMemoryEvent item)
        {
            var host = GetBrowser()?.GetHost();
            if (host == null || item == null)
                return;

            var x = ResolveInputX(item);
            var y = ResolveInputY(item);
            var modifiers = GetEventFlags(item);

            switch ((item.Type ?? string.Empty).ToLowerInvariant())
            {
                case "mousemove":
                    host.SendMouseMoveEvent(new MouseEvent(x, y, modifiers), false);
                    break;
                case "mousedown":
                    host.SendMouseClickEvent(new MouseEvent(x, y, modifiers), GetMouseButton(item.Button), false, 1);
                    break;
                case "mouseup":
                    host.SendMouseClickEvent(new MouseEvent(x, y, modifiers), GetMouseButton(item.Button), true, 1);
                    break;
                case "click":
                    host.SendMouseClickEvent(new MouseEvent(x, y, modifiers), GetMouseButton(item.Button), false, 1);
                    host.SendMouseClickEvent(new MouseEvent(x, y, modifiers), GetMouseButton(item.Button), true, 1);
                    break;
                case "dblclick":
                    host.SendMouseClickEvent(new MouseEvent(x, y, modifiers), GetMouseButton(item.Button), false, 2);
                    host.SendMouseClickEvent(new MouseEvent(x, y, modifiers), GetMouseButton(item.Button), true, 2);
                    break;
                case "wheel":
                    host.SendMouseWheelEvent(new MouseEvent(x, y, modifiers), (int)item.DeltaX, (int)item.DeltaY);
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

        private void OnNativeMessageReceived(object sender, NativeMessageEventArgs e)
        {
            if (HandleInputMemoryNativeKeyDown(e.Message, e.WParam, e.LParam))
            {
                e.Handled = true;
                return;
            }

            RecordInputMessage(e.Hwnd, e.Message, e.WParam, e.LParam);
        }

        private bool HandleInputMemoryNativeKeyDown(int msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg != NativeMethods.WM_KEYDOWN && msg != NativeMethods.WM_SYSKEYDOWN)
                return false;

            var nativeFlags = lParam.ToInt64();
            var isRepeat = (nativeFlags & (1L << 30)) != 0;
            if (isRepeat)
                return false;

            var handler = InputMemoryNativeKeyDown;
            if (handler == null)
                return false;

            var args = new InputMemoryNativeKeyEventArgs(wParam.ToInt32());
            handler(this, args);
            return args.Handled;
        }

        private void RecordInputMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
        {
            if (!IsInputMemoryRecording || IsInputMemoryPlaying)
                return;

            var type = GetInputMessageType(msg);
            if (type == null)
                return;

            var item = new HostInputMemoryEvent
            {
                Type = type,
                Time = _inputMemoryStopwatch.Elapsed.TotalMilliseconds,
                CtrlKey = (Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0,
                ShiftKey = (Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0,
                AltKey = (Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Alt) != 0,
                MetaKey = (Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Windows) != 0
            };

            if (IsMouseInputMessage(msg))
            {
                var x = GetSignedLowWord(lParam);
                var y = GetSignedHighWord(lParam);
                ConvertToBrowserClientPoint(hwnd, ref x, ref y);
                item.X = x;
                item.Y = y;
                item.RatioX = ActualWidth > 0 ? x / ActualWidth : (double?)null;
                item.RatioY = ActualHeight > 0 ? y / ActualHeight : (double?)null;
                item.Button = GetMouseButtonCode(msg);
                item.Buttons = GetMouseButtons();

                if (msg == NativeMethods.WM_MOUSEMOVE)
                {
                    var now = Environment.TickCount;
                    if (_lastInputMemoryMoveTime != 0 && now - _lastInputMemoryMoveTime < 30)
                        return;

                    if (_lastInputMemoryMoveX.HasValue && _lastInputMemoryMoveY.HasValue)
                    {
                        var dx = x - _lastInputMemoryMoveX.Value;
                        var dy = y - _lastInputMemoryMoveY.Value;
                        if (dx * dx + dy * dy < 4)
                            return;
                    }

                    _lastInputMemoryMoveTime = now;
                    _lastInputMemoryMoveX = x;
                    _lastInputMemoryMoveY = y;
                }
            }
            else if (msg == NativeMethods.WM_MOUSEWHEEL)
            {
                var x = GetSignedLowWord(lParam);
                var y = GetSignedHighWord(lParam);
                ConvertScreenToBrowserClientPoint(ref x, ref y);
                item.X = x;
                item.Y = y;
                item.DeltaY = GetSignedHighWord(wParam);
            }
            else
            {
                item.KeyCode = wParam.ToInt32();
                item.NativeKeyCode = unchecked((int)lParam.ToInt64());
            }

            _inputMemoryEvents.Add(item);
            InputMemoryEventCount = _inputMemoryEvents.Count;
            SetInputMemoryStatus($"正在录制，已记录 {InputMemoryEventCount} 个事件");
        }

        private static string GetInputMessageType(int msg)
        {
            switch (msg)
            {
                case NativeMethods.WM_MOUSEMOVE:
                    return "mousemove";
                case NativeMethods.WM_LBUTTONDOWN:
                case NativeMethods.WM_RBUTTONDOWN:
                case NativeMethods.WM_MBUTTONDOWN:
                    return "mousedown";
                case NativeMethods.WM_LBUTTONUP:
                case NativeMethods.WM_RBUTTONUP:
                case NativeMethods.WM_MBUTTONUP:
                    return "mouseup";
                case NativeMethods.WM_LBUTTONDBLCLK:
                case NativeMethods.WM_RBUTTONDBLCLK:
                case NativeMethods.WM_MBUTTONDBLCLK:
                    return "dblclick";
                case NativeMethods.WM_MOUSEWHEEL:
                    return "wheel";
                case NativeMethods.WM_KEYDOWN:
                case NativeMethods.WM_SYSKEYDOWN:
                    return "keydown";
                case NativeMethods.WM_KEYUP:
                case NativeMethods.WM_SYSKEYUP:
                    return "keyup";
                case NativeMethods.WM_CHAR:
                    return "char";
                default:
                    return null;
            }
        }

        private static bool IsMouseInputMessage(int msg)
        {
            return msg == NativeMethods.WM_MOUSEMOVE
                || msg == NativeMethods.WM_LBUTTONDOWN
                || msg == NativeMethods.WM_LBUTTONUP
                || msg == NativeMethods.WM_LBUTTONDBLCLK
                || msg == NativeMethods.WM_RBUTTONDOWN
                || msg == NativeMethods.WM_RBUTTONUP
                || msg == NativeMethods.WM_RBUTTONDBLCLK
                || msg == NativeMethods.WM_MBUTTONDOWN
                || msg == NativeMethods.WM_MBUTTONUP
                || msg == NativeMethods.WM_MBUTTONDBLCLK;
        }

        private int ResolveInputX(HostInputMemoryEvent item)
        {
            if (item.X.HasValue)
                return (int)Math.Round(item.X.Value);
            if (item.RatioX.HasValue)
                return (int)Math.Round(item.RatioX.Value * Math.Max(1, ActualWidth));
            return 0;
        }

        private int ResolveInputY(HostInputMemoryEvent item)
        {
            if (item.Y.HasValue)
                return (int)Math.Round(item.Y.Value);
            if (item.RatioY.HasValue)
                return (int)Math.Round(item.RatioY.Value * Math.Max(1, ActualHeight));
            return 0;
        }

        private void ConvertToBrowserClientPoint(IntPtr sourceHwnd, ref int x, ref int y)
        {
            if (sourceHwnd == IntPtr.Zero || BrowserHandle == IntPtr.Zero || sourceHwnd == BrowserHandle)
                return;

            var point = new NativeMethods.POINT { X = x, Y = y };
            if (NativeMethods.ClientToScreen(sourceHwnd, ref point)
                && NativeMethods.ScreenToClient(BrowserHandle, ref point))
            {
                x = point.X;
                y = point.Y;
            }
        }

        private void ConvertScreenToBrowserClientPoint(ref int x, ref int y)
        {
            if (BrowserHandle == IntPtr.Zero)
                return;

            var point = new NativeMethods.POINT { X = x, Y = y };
            if (NativeMethods.ScreenToClient(BrowserHandle, ref point))
            {
                x = point.X;
                y = point.Y;
            }
        }

        private static KeyEvent CreateKeyEvent(HostInputMemoryEvent item, KeyEventType type, CefEventFlags modifiers)
        {
            return new KeyEvent
            {
                Type = type,
                WindowsKeyCode = item.KeyCode,
                NativeKeyCode = item.NativeKeyCode != 0 ? item.NativeKeyCode : item.KeyCode,
                Modifiers = modifiers,
                IsSystemKey = item.AltKey
            };
        }

        private static CefEventFlags GetEventFlags(HostInputMemoryEvent item)
        {
            var flags = CefEventFlags.None;
            if (item.CtrlKey)
                flags |= CefEventFlags.ControlDown;
            if (item.ShiftKey)
                flags |= CefEventFlags.ShiftDown;
            if (item.AltKey)
                flags |= CefEventFlags.AltDown;
            if (item.MetaKey)
                flags |= CefEventFlags.CommandDown;
            if ((item.Buttons & 1) != 0)
                flags |= CefEventFlags.LeftMouseButton;
            if ((item.Buttons & 2) != 0)
                flags |= CefEventFlags.RightMouseButton;
            if ((item.Buttons & 4) != 0)
                flags |= CefEventFlags.MiddleMouseButton;
            return flags;
        }

        private static MouseButtonType GetMouseButton(int button)
        {
            switch (button)
            {
                case 1:
                    return MouseButtonType.Middle;
                case 2:
                    return MouseButtonType.Right;
                default:
                    return MouseButtonType.Left;
            }
        }

        private static int GetMouseButtonCode(int msg)
        {
            switch (msg)
            {
                case NativeMethods.WM_RBUTTONDOWN:
                case NativeMethods.WM_RBUTTONUP:
                case NativeMethods.WM_RBUTTONDBLCLK:
                    return 2;
                case NativeMethods.WM_MBUTTONDOWN:
                case NativeMethods.WM_MBUTTONUP:
                case NativeMethods.WM_MBUTTONDBLCLK:
                    return 1;
                default:
                    return 0;
            }
        }

        private static int GetMouseButtons()
        {
            var buttons = 0;
            if (NativeMethods.GetKeyState(NativeMethods.VK_LBUTTON) < 0)
                buttons |= 1;
            if (NativeMethods.GetKeyState(NativeMethods.VK_RBUTTON) < 0)
                buttons |= 2;
            if (NativeMethods.GetKeyState(NativeMethods.VK_MBUTTON) < 0)
                buttons |= 4;
            return buttons;
        }

        private static int GetSignedLowWord(IntPtr value)
        {
            var raw = unchecked((int)value.ToInt64());
            return (short)(raw & 0xffff);
        }

        private static int GetSignedHighWord(IntPtr value)
        {
            var raw = unchecked((int)value.ToInt64());
            return (short)((raw >> 16) & 0xffff);
        }


        protected override void OnFrameLoadStart(FrameLoadStartEventArgs e)
        {
            base.OnFrameLoadStart(e);

            if (e.Frame.IsMain)
            {
                FeatureDiagnostics.Log("InputMemory", "main frame load start; resetting input memory state");
                IsInputMemoryRecording = false;
                IsInputMemoryPlaying = false;
                _inputMemoryEvents.Clear();
                InputMemoryEventCount = 0;
                SetInputMemoryStatus(string.Empty);
                BlockedSwfs.Clear();
                SetCurrentValue(HasBlockedSwfsProperty, false);
            }
        }

        protected override void OnFrameLoadEnd(FrameLoadEndEventArgs e)
        {
            base.OnFrameLoadEnd(e);

            if (e.Frame.IsMain)
            {
                FeatureDiagnostics.Log("InputMemory", "main frame load end; host input memory backend ready");
            }
        }

        private static bool GetBool(System.Collections.Generic.IDictionary<string, object> state, string key)
        {
            return state.TryGetValue(key, out var value) && value is bool b && b;
        }

        private static int GetInt(System.Collections.Generic.IDictionary<string, object> state, string key)
        {
            if (!state.TryGetValue(key, out var value) || value == null)
                return 0;
            try
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return 0;
            }
        }

        private void SetInputMemoryStatus(string status)
        {
            SetCurrentValue(InputMemoryStatusTextProperty, status ?? string.Empty);
        }

        private static string ToJavaScriptStringLiteral(string value)
        {
            var builder = new StringBuilder(value.Length + 2);
            builder.Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (char.IsControl(c) || c == '\u2028' || c == '\u2029')
                            builder.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:x4}", (int)c);
                        else
                            builder.Append(c);
                        break;
                }
            }
            builder.Append('"');
            return builder.ToString();
        }

        protected override void OnConsoleMessage(ConsoleMessageEventArgs e)
        {
            base.OnConsoleMessage(e);

            if (e.Level != LogSeverity.Info)
            {
                return;
            }

            var msg = e.Message;
            if (msg == null || !msg.StartsWith("Cross-origin plugin content from", StringComparison.Ordinal))
            {
                return;
            }

            var parts = msg.Split(' ');
            if (parts.Length <= 4)
            {
                return;
            }

            var url = parts[4];
            if (string.IsNullOrWhiteSpace(url) || BlockedSwfs.Contains(url))
            {
                return;
            }

            BlockedSwfs.Add(url);
            SetCurrentValue(HasBlockedSwfsProperty, true);
        }

        protected override void OnIsBrowserInitializedChanged(EventArgs e)
        {
            base.OnIsBrowserInitializedChanged(e);

            if (!IsBrowserInitialized)
            {
                return;
            }

            Cef.UIThreadTaskFactory.StartNew(() =>
            { // enable flash contents automatically
                var browser = GetBrowser();
                if (browser == null || browser.IsDisposed)
                {
                    return;
                }

                var host = browser.GetHost();
                if (host == null)
                {
                    return;
                }

                var ok = host.RequestContext.SetPreference(
                    "profile.default_content_setting_values.plugins", 1, out var error);
                if (!ok && !string.IsNullOrEmpty(error))
                {
                    Debug.WriteLine($"[ChromiumFlashBrowser] Failed to enable plugins preference: {error}");
                }
            });
        }

        private sealed class HostInputMemoryEvent
        {
            [JsonProperty("type")]
            public string Type { get; set; }

            [JsonProperty("time")]
            public double Time { get; set; }

            [JsonProperty("x")]
            public double? X { get; set; }

            [JsonProperty("y")]
            public double? Y { get; set; }

            [JsonProperty("ratioX")]
            public double? RatioX { get; set; }

            [JsonProperty("ratioY")]
            public double? RatioY { get; set; }

            [JsonProperty("button")]
            public int Button { get; set; }

            [JsonProperty("buttons")]
            public int Buttons { get; set; }

            [JsonProperty("deltaX")]
            public double DeltaX { get; set; }

            [JsonProperty("deltaY")]
            public double DeltaY { get; set; }

            [JsonProperty("key")]
            public string Key { get; set; }

            [JsonProperty("code")]
            public string Code { get; set; }

            [JsonProperty("keyCode")]
            public int KeyCode { get; set; }

            [JsonProperty("nativeKeyCode")]
            public int NativeKeyCode { get; set; }

            [JsonProperty("ctrlKey")]
            public bool CtrlKey { get; set; }

            [JsonProperty("shiftKey")]
            public bool ShiftKey { get; set; }

            [JsonProperty("altKey")]
            public bool AltKey { get; set; }

            [JsonProperty("metaKey")]
            public bool MetaKey { get; set; }
        }

        public sealed class InputMemoryNativeKeyEventArgs : EventArgs
        {
            public InputMemoryNativeKeyEventArgs(int virtualKey)
            {
                VirtualKey = virtualKey;
            }

            public int VirtualKey { get; }

            public bool Handled { get; set; }
        }

        private static class NativeMethods
        {
            public const int WM_KEYDOWN = 0x0100;
            public const int WM_KEYUP = 0x0101;
            public const int WM_CHAR = 0x0102;
            public const int WM_SYSKEYDOWN = 0x0104;
            public const int WM_SYSKEYUP = 0x0105;
            public const int WM_MOUSEMOVE = 0x0200;
            public const int WM_LBUTTONDOWN = 0x0201;
            public const int WM_LBUTTONUP = 0x0202;
            public const int WM_LBUTTONDBLCLK = 0x0203;
            public const int WM_RBUTTONDOWN = 0x0204;
            public const int WM_RBUTTONUP = 0x0205;
            public const int WM_RBUTTONDBLCLK = 0x0206;
            public const int WM_MBUTTONDOWN = 0x0207;
            public const int WM_MBUTTONUP = 0x0208;
            public const int WM_MBUTTONDBLCLK = 0x0209;
            public const int WM_MOUSEWHEEL = 0x020A;
            public const int VK_LBUTTON = 0x01;
            public const int VK_RBUTTON = 0x02;
            public const int VK_MBUTTON = 0x04;

            [DllImport("user32.dll")]
            public static extern short GetKeyState(int virtualKey);

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
        }

    }
}
