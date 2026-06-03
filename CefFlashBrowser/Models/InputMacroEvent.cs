using Newtonsoft.Json;

namespace CefFlashBrowser.Models
{
    public sealed class InputMacroEvent
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

        [JsonProperty("ctrlKey")]
        public bool CtrlKey { get; set; }

        [JsonProperty("shiftKey")]
        public bool ShiftKey { get; set; }

        [JsonProperty("altKey")]
        public bool AltKey { get; set; }

        [JsonProperty("metaKey")]
        public bool MetaKey { get; set; }
    }
}
