using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace CefFlashBrowser.Models
{
    public sealed class InputMacro
    {
        [JsonProperty("version")]
        public int Version { get; set; } = 1;

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("pageUrl")]
        public string PageUrl { get; set; }

        [JsonProperty("createdAt")]
        public DateTime CreatedAt { get; set; }

        [JsonProperty("durationMs")]
        public double DurationMs { get; set; }

        [JsonProperty("events")]
        public List<InputMacroEvent> Events { get; set; } = new List<InputMacroEvent>();
    }
}
