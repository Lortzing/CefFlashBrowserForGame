using CefFlashBrowser.Data;
using CefFlashBrowser.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CefFlashBrowser.Views
{
    public partial class BrowserWindow
    {
        private static readonly bool InputMemoryPanelFixupsRegistered = RegisterInputMemoryPanelFixups();

        private static bool RegisterInputMemoryPanelFixups()
        {
            EventManager.RegisterClassHandler(
                typeof(Window),
                LoadedEvent,
                new RoutedEventHandler(OnAnyWindowLoadedInputMemoryPanelFixups));
            return true;
        }

        private static void OnAnyWindowLoadedInputMemoryPanelFixups(object sender, RoutedEventArgs e)
        {
            if (!(sender is Window panel)
                || !string.Equals(panel.Title, "键鼠精灵", StringComparison.Ordinal)
                || !(panel.Owner is BrowserWindow owner))
            {
                return;
            }

            owner.ApplyInputMemoryPanelFixups(panel);
        }

        private void ApplyInputMemoryPanelFixups(Window panel)
        {
            if (panel.Tag as string == "InputMemoryPanelFixupsApplied")
                return;
            panel.Tag = "InputMemoryPanelFixupsApplied";

            var wrapPanels = FindVisualChildren<WrapPanel>(panel).ToList();
            if (wrapPanels.Count > 0)
            {
                var topPanel = wrapPanels[0];

                var recordButton = CreateInputMemoryPanelButton("开始/停止录制");
                recordButton.Click += async delegate
                {
                    if (browser.IsInputMemoryRecording)
                    {
                        await StopAndAutoSaveInputMacroAsync("键鼠精灵窗口停止录制");
                    }
                    else
                    {
                        browser.StartInputMemoryRecording();
                        SetInputMacroHint("键鼠精灵：开始录制");
                    }
                };
                topPanel.Children.Insert(0, recordButton);

                var stopSaveButton = CreateInputMemoryPanelButton("停止并自动保存");
                stopSaveButton.Click += async delegate
                {
                    if (browser.IsInputMemoryRecording)
                        await StopAndAutoSaveInputMacroAsync("键鼠精灵窗口停止并保存");
                    else
                        await AutoSaveCurrentInputMacroAsync("键鼠精灵窗口手动保存当前记录");
                };
                topPanel.Children.Insert(1, stopSaveButton);
            }

            var saveSettingsButton = FindVisualChildren<Button>(panel)
                .FirstOrDefault(button => string.Equals(button.Content as string, "保存设置", StringComparison.Ordinal));
            if (saveSettingsButton != null)
            {
                saveSettingsButton.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args)
                {
                    if (TrySaveInputMemoryShortcutSettingsFromPanel(panel))
                        args.Handled = true;
                };
            }

            LogHelper.LogInfo("[InputMemory] input macro panel fixups applied");
        }

        private static Button CreateInputMemoryPanelButton(string text)
        {
            return new Button
            {
                Content = text,
                MinWidth = 96,
                Height = 28,
                Margin = new Thickness(0, 0, 8, 6),
                Padding = new Thickness(8, 0, 8, 0)
            };
        }

        private static bool TrySaveInputMemoryShortcutSettingsFromPanel(Window panel)
        {
            var textBoxes = FindVisualChildren<TextBox>(panel).ToList();
            if (textBoxes.Count < 4)
                return false;

            var replayCountBox = textBoxes[0];
            var recordBox = textBoxes[textBoxes.Count - 3];
            var replayBox = textBoxes[textBoxes.Count - 2];
            var stopBox = textBoxes[textBoxes.Count - 1];
            var loopBox = FindVisualChildren<CheckBox>(panel)
                .FirstOrDefault(box => string.Equals(box.Content as string, "持续循环直到停止", StringComparison.Ordinal));

            var loopUntilStopped = loopBox?.IsChecked == true;
            if (!loopUntilStopped && (!int.TryParse(replayCountBox.Text, out var replayCount) || replayCount <= 0))
            {
                WindowManager.Alert("回放次数必须是大于 0 的整数。", "键鼠精灵");
                return true;
            }

            if (!TryParseInputMacroHotkey(recordBox.Text, out _, out _)
                || !TryParseInputMacroHotkey(replayBox.Text, out _, out _)
                || !TryParseInputMacroHotkey(stopBox.Text, out _, out _))
            {
                WindowManager.Alert("快捷键格式无效。可用 Ctrl/Shift/Alt + 字母、数字、F1-F24、-、=、[、]、\\、;、'、,、.、/、`、Space、Esc。", "键鼠精灵");
                return true;
            }

            var shortcuts = new[]
            {
                NormalizeInputMacroShortcut(recordBox.Text),
                NormalizeInputMacroShortcut(replayBox.Text),
                NormalizeInputMacroShortcut(stopBox.Text)
            };

            if (shortcuts.Distinct(StringComparer.OrdinalIgnoreCase).Count() != shortcuts.Length)
            {
                WindowManager.Alert("录制、回放、停止快捷键不能重复。", "键鼠精灵");
                return true;
            }

            GlobalData.Settings.InputMacroReplayCount = loopUntilStopped ? GlobalData.Settings.InputMacroReplayCount : int.Parse(replayCountBox.Text);
            GlobalData.Settings.InputMacroLoopUntilStopped = loopUntilStopped;
            GlobalData.Settings.InputMacroRecordShortcut = shortcuts[0];
            GlobalData.Settings.InputMacroReplayShortcut = shortcuts[1];
            GlobalData.Settings.InputMacroStopShortcut = shortcuts[2];
            GlobalData.SaveSettings();
            WindowManager.Alert("键鼠精灵设置已保存。", "键鼠精灵");
            LogHelper.LogInfo($"[InputMemory] shortcut settings saved; record={shortcuts[0]} replay={shortcuts[1]} stop={shortcuts[2]}");
            return true;
        }

        private static string NormalizeInputMacroShortcut(string shortcut)
        {
            var parts = (shortcut ?? string.Empty)
                .Split('+')
                .Select(part => part.Trim())
                .Where(part => part.Length > 0)
                .ToList();

            for (var i = 0; i < parts.Count; i++)
            {
                if (string.Equals(parts[i], "Control", StringComparison.OrdinalIgnoreCase))
                    parts[i] = "Ctrl";
                else if (string.Equals(parts[i], "Ctrl", StringComparison.OrdinalIgnoreCase))
                    parts[i] = "Ctrl";
                else if (string.Equals(parts[i], "Shift", StringComparison.OrdinalIgnoreCase))
                    parts[i] = "Shift";
                else if (string.Equals(parts[i], "Alt", StringComparison.OrdinalIgnoreCase))
                    parts[i] = "Alt";
                else if (string.Equals(parts[i], "Win", StringComparison.OrdinalIgnoreCase) || string.Equals(parts[i], "Windows", StringComparison.OrdinalIgnoreCase))
                    parts[i] = "Win";
                else
                    parts[i] = parts[i].Length == 1 ? parts[i] : parts[i].ToUpperInvariant();
            }

            return string.Join("+", parts);
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null)
                yield break;

            var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is T result)
                    yield return result;

                foreach (var nested in FindVisualChildren<T>(child))
                    yield return nested;
            }
        }
    }
}
