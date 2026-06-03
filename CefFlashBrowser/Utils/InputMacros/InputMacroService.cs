using CefFlashBrowser.Data;
using CefFlashBrowser.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CefFlashBrowser.Utils.InputMacros
{
    public static class InputMacroService
    {
        public static string DirectoryPath => Path.Combine(GlobalData.DataPath, "InputMacros");

        public static InputMacro CreateMacro(string name, string pageUrl, string eventsJson)
        {
            var events = JsonConvert.DeserializeObject<List<InputMacroEvent>>(eventsJson ?? "[]")
                ?? new List<InputMacroEvent>();

            return new InputMacro
            {
                Name = string.IsNullOrWhiteSpace(name) ? CreateDefaultName() : name.Trim(),
                PageUrl = pageUrl,
                CreatedAt = DateTime.Now,
                DurationMs = events.Count == 0 ? 0 : events.Max(item => item.Time),
                Events = events
            };
        }

        public static string Save(InputMacro macro)
        {
            if (macro == null)
            {
                throw new ArgumentNullException(nameof(macro));
            }

            Directory.CreateDirectory(DirectoryPath);
            var fileName = SanitizeFileName(macro.Name);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = CreateDefaultName();
            }

            var path = Path.Combine(DirectoryPath, $"{fileName}_{DateTime.Now:yyyyMMdd_HHmmss}.json");
            SafeWriteFile(path, JsonConvert.SerializeObject(macro, Formatting.Indented));
            return path;
        }

        public static InputMacro Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Macro path is empty.", nameof(path));
            }

            var macro = JsonConvert.DeserializeObject<InputMacro>(File.ReadAllText(path));
            if (macro?.Events == null)
            {
                throw new InvalidDataException("Input macro file is invalid.");
            }

            return macro;
        }

        public static string ExportEventsJson(InputMacro macro)
        {
            if (macro?.Events == null)
            {
                return "[]";
            }

            return JsonConvert.SerializeObject(macro.Events);
        }

        public static string CreateDefaultName()
        {
            return $"键鼠宏_{DateTime.Now:yyyyMMdd_HHmmss}";
        }

        private static string SanitizeFileName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = (value ?? string.Empty).Select(c => invalid.Contains(c) ? '_' : c).ToArray();
            return new string(chars).Trim();
        }

        private static void SafeWriteFile(string path, string contents)
        {
            var tmpPath = path + $".{Guid.NewGuid()}.tmp";
            File.WriteAllText(tmpPath, contents);

            if (File.Exists(path))
            {
                File.Replace(tmpPath, path, null);
            }
            else
            {
                File.Move(tmpPath, path);
            }
        }
    }
}
