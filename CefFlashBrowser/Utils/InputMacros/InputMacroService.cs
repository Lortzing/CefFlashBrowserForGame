using CefFlashBrowser.Data;
using CefFlashBrowser.Models;
using CefFlashBrowser.Utils;
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
        private static string OrderPath => Path.Combine(DirectoryPath, ".order");

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
            AppendOrder(Path.GetFileName(path));
            return path;
        }

        public static IReadOnlyList<InputMacroFile> ListSavedMacros()
        {
            Directory.CreateDirectory(DirectoryPath);
            var files = Directory.GetFiles(DirectoryPath, "*.json")
                .Select(TryLoadFile)
                .Where(item => item != null)
                .ToList();

            var order = LoadOrder();
            if (order.Count == 0)
            {
                return files.OrderByDescending(item => item.Macro.CreatedAt).ThenBy(item => item.FileName).ToArray();
            }

            var orderIndex = order
                .Select((fileName, index) => new { fileName, index })
                .GroupBy(item => item.fileName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().index, StringComparer.OrdinalIgnoreCase);

            return files
                .OrderBy(item => orderIndex.TryGetValue(item.FileName, out var index) ? index : int.MaxValue)
                .ThenByDescending(item => item.Macro.CreatedAt)
                .ThenBy(item => item.FileName)
                .ToArray();
        }

        public static void SaveOrder(IEnumerable<InputMacroFile> macros)
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllLines(OrderPath, macros.Where(item => item != null).Select(item => item.FileName));
        }

        public static InputMacroFile Rename(InputMacroFile item, string newName)
        {
            if (item == null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            if (string.IsNullOrWhiteSpace(newName))
            {
                throw new ArgumentException("Macro name is empty.", nameof(newName));
            }

            item.Macro.Name = newName.Trim();
            var fileName = SanitizeFileName(item.Macro.Name);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = CreateDefaultName();
            }

            var newPath = Path.Combine(DirectoryPath, $"{fileName}_{DateTime.Now:yyyyMMdd_HHmmss}.json");
            SafeWriteFile(newPath, JsonConvert.SerializeObject(item.Macro, Formatting.Indented));
            if (!string.Equals(item.Path, newPath, StringComparison.OrdinalIgnoreCase) && File.Exists(item.Path))
            {
                File.Delete(item.Path);
            }

            ReplaceOrderFileName(item.FileName, Path.GetFileName(newPath));
            return new InputMacroFile(newPath, item.Macro);
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

        public static string FormatDuration(double durationMs)
        {
            if (durationMs < 0 || double.IsNaN(durationMs) || double.IsInfinity(durationMs))
            {
                durationMs = 0;
            }

            var duration = TimeSpan.FromMilliseconds(durationMs);
            return duration.TotalHours >= 1
                ? duration.ToString(@"h\:mm\:ss")
                : duration.ToString(@"m\:ss");
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

        private static InputMacroFile TryLoadFile(string path)
        {
            try
            {
                return new InputMacroFile(path, Load(path));
            }
            catch (Exception e)
            {
                LogHelper.LogError($"Failed to load input macro file: {path}", e);
                return null;
            }
        }

        private static List<string> LoadOrder()
        {
            if (!File.Exists(OrderPath))
            {
                return new List<string>();
            }

            return File.ReadAllLines(OrderPath)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => line.Trim())
                .ToList();
        }

        private static void AppendOrder(string fileName)
        {
            var order = LoadOrder();
            order.RemoveAll(item => string.Equals(item, fileName, StringComparison.OrdinalIgnoreCase));
            order.Insert(0, fileName);
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllLines(OrderPath, order);
        }

        private static void ReplaceOrderFileName(string oldFileName, string newFileName)
        {
            var order = LoadOrder();
            for (var i = 0; i < order.Count; i++)
            {
                if (string.Equals(order[i], oldFileName, StringComparison.OrdinalIgnoreCase))
                {
                    order[i] = newFileName;
                    File.WriteAllLines(OrderPath, order);
                    return;
                }
            }

            AppendOrder(newFileName);
        }
    }

    public sealed class InputMacroFile
    {
        public InputMacroFile(string path, InputMacro macro)
        {
            Path = path;
            Macro = macro;
        }

        public string Path { get; }

        public InputMacro Macro { get; }

        public string FileName => System.IO.Path.GetFileName(Path);

        public int EventCount => Macro?.Events?.Count ?? 0;

        public string DurationText => InputMacroService.FormatDuration(Macro?.DurationMs ?? 0);

        public override string ToString()
        {
            var name = string.IsNullOrWhiteSpace(Macro?.Name) ? FileName : Macro.Name;
            return $"{name}    {DurationText}    {EventCount}事件";
        }
    }
}
