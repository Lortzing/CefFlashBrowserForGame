using CefSharp;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

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

        private bool _isInputMemoryStatusPolling;
        private int _inputMemoryPlaybackVersion;

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
            if (!CanExecuteJavascriptInMainFrame)
            {
                SetInputMemoryStatus("页面尚未就绪，不能开始录制");
                return;
            }

            IsInputMemoryRecording = true;
            IsInputMemoryPlaying = false;
            _inputMemoryPlaybackVersion++;
            InputMemoryEventCount = 0;
            SetInputMemoryStatus("正在录制 00:00，已记录 0 个事件");
            ExecuteScriptAsync(InputMemoryBootstrapScript + "\nwindow.__cefInputMemory.start();");
            _ = PollInputMemoryStatusAsync();
        }

        public void StopInputMemoryRecording()
        {
            IsInputMemoryRecording = false;
            if (CanExecuteJavascriptInMainFrame)
            {
                ExecuteScriptAsync(InputMemoryBootstrapScript + "\nwindow.__cefInputMemory.stop();");
            }
            _ = RefreshInputMemoryStatusAsync();
        }

        public void ReplayInputMemory()
        {
            _ = ReplayInputMemoryAsync();
        }

        public async Task ReplayInputMemoryAsync(double speed = 1.0, int loopCount = 1, int loopIntervalMs = 0, int countdownSeconds = 3)
        {
            IsInputMemoryRecording = false;
            if (!CanExecuteJavascriptInMainFrame)
                return;

            await RefreshInputMemoryStatusAsync();
            if (InputMemoryEventCount <= 0)
            {
                SetInputMemoryStatus("当前脚本为空，不能回放");
                return;
            }

            var playbackVersion = ++_inputMemoryPlaybackVersion;
            IsInputMemoryPlaying = true;
            for (var i = countdownSeconds; i > 0; i--)
            {
                SetInputMemoryStatus($"回放将在 {i} 秒后开始");
                await Task.Delay(1000);
                if (playbackVersion != _inputMemoryPlaybackVersion || !IsInputMemoryPlaying)
                    return;
            }

            SetInputMemoryStatus($"回放中 1/{Math.Max(loopCount, 1)}，按 Esc 停止");
            var script = string.Format(
                CultureInfo.InvariantCulture,
                "\nwindow.__cefInputMemory.play({{ speed: {0}, loopCount: {1}, loopInterval: {2} }});",
                speed,
                loopCount,
                loopIntervalMs);
            ExecuteScriptAsync(InputMemoryBootstrapScript + script);
            _ = PollInputMemoryStatusAsync();
        }

        public void StopInputMemoryPlayback()
        {
            _inputMemoryPlaybackVersion++;
            IsInputMemoryPlaying = false;
            if (CanExecuteJavascriptInMainFrame)
            {
                ExecuteScriptAsync(InputMemoryBootstrapScript + "\nwindow.__cefInputMemory.stopPlay();");
            }
            SetInputMemoryStatus($"已停止，当前脚本 {InputMemoryEventCount} 个事件");
        }

        public void ClearInputMemory()
        {
            IsInputMemoryRecording = false;
            IsInputMemoryPlaying = false;
            _inputMemoryPlaybackVersion++;
            InputMemoryEventCount = 0;
            if (CanExecuteJavascriptInMainFrame)
            {
                ExecuteScriptAsync(InputMemoryBootstrapScript + "\nwindow.__cefInputMemory.clear();");
            }
            SetInputMemoryStatus("当前脚本为空");
        }

        public async Task<string> ExportInputMemoryEventsJsonAsync()
        {
            if (!CanExecuteJavascriptInMainFrame)
                return "[]";

            var response = await EvaluateInputMemoryScriptAsync(
                InputMemoryBootstrapScript + "\nJSON.stringify(window.__cefInputMemory.exportEvents());");
            if (response == null)
                return "[]";

            var json = response.Success ? response.Result as string : null;
            await RefreshInputMemoryStatusAsync();
            return string.IsNullOrWhiteSpace(json) ? "[]" : json;
        }

        public async Task ImportInputMemoryEventsJsonAsync(string eventsJson)
        {
            if (!CanExecuteJavascriptInMainFrame)
                return;

            var script = InputMemoryBootstrapScript
                + "\nwindow.__cefInputMemory.importEvents(JSON.parse("
                + ToJavaScriptStringLiteral(eventsJson ?? "[]")
                + "));";
            await EvaluateInputMemoryScriptAsync(script);
            await RefreshInputMemoryStatusAsync();
        }

        public async Task RefreshInputMemoryStatusAsync()
        {
            if (!CanExecuteJavascriptInMainFrame)
                return;

            var response = await EvaluateInputMemoryScriptAsync(
                InputMemoryBootstrapScript + "\nwindow.__cefInputMemory.state();");
            if (response == null || !response.Success || !(response.Result is System.Collections.Generic.IDictionary<string, object> state))
                return;

            IsInputMemoryRecording = GetBool(state, "recording");
            IsInputMemoryPlaying = GetBool(state, "playing");
            InputMemoryEventCount = GetInt(state, "count");
            var playIndex = GetInt(state, "playIndex");
            var playTotal = GetInt(state, "playTotal");
            var loopIndex = GetInt(state, "loopIndex");
            var loopTotal = GetInt(state, "loopTotal");

            if (IsInputMemoryRecording)
            {
                SetInputMemoryStatus($"正在录制，已记录 {InputMemoryEventCount} 个事件");
            }
            else if (IsInputMemoryPlaying)
            {
                var loopText = loopTotal <= 0 ? $"{loopIndex}/∞" : $"{loopIndex}/{loopTotal}";
                SetInputMemoryStatus($"回放中 {loopText}，事件 {playIndex}/{playTotal}，按 Esc 停止");
            }
            else if (InputMemoryEventCount > 0)
            {
                SetInputMemoryStatus($"当前脚本 {InputMemoryEventCount} 个事件");
            }
            else
            {
                SetInputMemoryStatus("当前脚本为空");
            }
        }

        private async Task PollInputMemoryStatusAsync()
        {
            if (_isInputMemoryStatusPolling)
                return;

            _isInputMemoryStatusPolling = true;
            try
            {
                while (IsInputMemoryRecording || IsInputMemoryPlaying)
                {
                    await Task.Delay(500);
                    await RefreshInputMemoryStatusAsync();
                }
            }
            finally
            {
                _isInputMemoryStatusPolling = false;
            }
        }

        private async Task<JavascriptResponse> EvaluateInputMemoryScriptAsync(string script)
        {
            try
            {
                var cefBrowser = GetBrowser();
                var frame = cefBrowser?.MainFrame;
                return frame == null ? null : await frame.EvaluateScriptAsync(script);
            }
            catch (Exception e)
            {
                Debug.WriteLine($"[InputMemory] Failed to evaluate script: {e.Message}");
                return null;
            }
        }


        protected override void OnFrameLoadStart(FrameLoadStartEventArgs e)
        {
            base.OnFrameLoadStart(e);

            if (e.Frame.IsMain)
            {
                IsInputMemoryRecording = false;
                IsInputMemoryPlaying = false;
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
                e.Frame.ExecuteJavaScriptAsync(InputMemoryBootstrapScript);
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

    }
}
