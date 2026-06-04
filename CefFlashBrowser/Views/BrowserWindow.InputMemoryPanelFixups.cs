using CefFlashBrowser.Data;
using CefFlashBrowser.Utils;
using CefFlashBrowser.Utils.InputMacros;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CefFlashBrowser.Views
{
    public partial class BrowserWindow
    {
        private const string InputMemoryRecordButtonTag = "InputMemoryRecordButton";
        private const string InputMemoryDeleteButtonTag = "InputMemoryDeleteButton";
        private const string InputMemoryLoopIntervalBoxTag = "InputMemoryLoopIntervalSecondsBox";
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
                FixInputMemoryTopButtonRow(panel, wrapPanels[0]);

            if (wrapPanels.Count > 1)
                FixInputMemoryPlaybackRow(panel, wrapPanels[1]);

            FixInputMemoryShortcutRow(panel);
            RefreshInputMemoryPanelRecordingButton();
            LogHelper.LogInfo("[InputMemory] input macro panel playback fixups applied");
        }

        private void FixInputMemoryTopButtonRow(Window panel, WrapPanel topPanel)
        {
            RemoveInputMemoryPanelButton(topPanel, "保存当前记录");
            RemoveInputMemoryPanelButton(topPanel, "开始/停止录制");
            RemoveInputMemoryPanelButton(topPanel, "停止并自动保存");

            if (!topPanel.Children.OfType<Button>().Any(button => string.Equals(button.Tag as string, InputMemoryRecordButtonTag, StringComparison.Ordinal)))
            {
                var recordButton = CreateInputMemoryPanelButton(GetInputMemoryRecordButtonText(), 112);
                recordButton.Tag = InputMemoryRecordButtonTag;
                recordButton.Click += async delegate
                {
                    if (browser.IsInputMemoryRecording)
                    {
                        browser.StopInputMemoryNativeCapture();
                        await StopAndAutoSaveInputMacroAsync("键鼠精灵窗口停止录制并保存");
                    }
                    else
                    {
                        browser.StartInputMemoryRecording();
                        browser.StartInputMemoryNativeCapture();
                        SetInputMacroHint("键鼠精灵：开始录制");
                    }

                    RefreshInputMemoryPanelRecordingButton();
                };
                topPanel.Children.Insert(0, recordButton);
            }

            var loadButton = topPanel.Children
                .OfType<Button>()
                .FirstOrDefault(button => string.Equals(button.Content as string, "载入", StringComparison.Ordinal));
            if (loadButton != null)
            {
                loadButton.ToolTip = "从任意文件夹选择键鼠脚本，并复制到默认脚本文件夹后载入";
                loadButton.PreviewMouseLeftButtonDown += async delegate(object sender, MouseButtonEventArgs args)
                {
                    args.Handled = true;
                    await ImportInputMacroFileIntoDefaultFolderAsync(panel);
                };
            }

            if (!topPanel.Children.OfType<Button>().Any(button => string.Equals(button.Tag as string, InputMemoryDeleteButtonTag, StringComparison.Ordinal)))
            {
                var deleteButton = CreateInputMemoryPanelButton("删除记录", 78);
                deleteButton.Tag = InputMemoryDeleteButtonTag;
                deleteButton.Click += delegate { DeleteSelectedInputMacroFromPanel(panel); };
                var insertAfterLoad = loadButton == null ? -1 : topPanel.Children.IndexOf(loadButton);
                topPanel.Children.Insert(insertAfterLoad >= 0 ? insertAfterLoad + 1 : topPanel.Children.Count, deleteButton);
            }
        }

        private void FixInputMemoryPlaybackRow(Window panel, WrapPanel playbackPanel)
        {
            var loopCheckBox = playbackPanel.Children
                .OfType<CheckBox>()
                .FirstOrDefault(box => string.Equals(box.Content as string, "持续循环直到停止", StringComparison.Ordinal) || string.Equals(box.Content as string, "持续播放", StringComparison.Ordinal));
            if (loopCheckBox != null)
                loopCheckBox.Content = "持续播放";

            var replayButton = playbackPanel.Children
                .OfType<Button>()
                .FirstOrDefault(button => string.Equals(button.Content as string, "回放", StringComparison.Ordinal)
                    || string.Equals(button.Content as string, "开始播放", StringComparison.Ordinal)
                    || string.Equals(button.Content as string, "停止播放", StringComparison.Ordinal));
            if (replayButton != null)
            {
                replayButton.Content = browser.IsInputMemoryPlaying ? "停止播放" : "开始播放";
                replayButton.PreviewMouseLeftButtonDown += async delegate(object sender, MouseButtonEventArgs args)
                {
                    args.Handled = true;
                    await PlaySelectedInputMacroFromPanelAsync(panel);
                };
            }

            var stopButton = playbackPanel.Children
                .OfType<Button>()
                .FirstOrDefault(button => string.Equals(button.Content as string, "停止回放", StringComparison.Ordinal));
            if (stopButton != null)
                stopButton.Visibility = Visibility.Collapsed;

            if (!FindVisualChildren<TextBox>(playbackPanel).Any(box => string.Equals(box.Tag as string, InputMemoryLoopIntervalBoxTag, StringComparison.Ordinal)))
            {
                playbackPanel.Children.Add(new TextBlock
                {
                    Text = "两次间隔(秒)",
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 6, 0)
                });
                playbackPanel.Children.Add(new TextBox
                {
                    Width = 62,
                    Text = "1",
                    Tag = InputMemoryLoopIntervalBoxTag,
                    Margin = new Thickness(0, 0, 12, 0)
                });
            }
        }

        private void FixInputMemoryShortcutRow(Window panel)
        {
            var textBoxes = FindVisualChildren<TextBox>(panel).ToList();
            if (textBoxes.Count < 3)
                return;

            var recordBox = textBoxes[textBoxes.Count - 3];
            var replayBox = textBoxes[textBoxes.Count - 2];
            var stopBox = textBoxes[textBoxes.Count - 1];

            if (string.IsNullOrWhiteSpace(replayBox.Text) && !string.IsNullOrWhiteSpace(GlobalData.Settings.InputMacroStopShortcut))
                replayBox.Text = GlobalData.Settings.InputMacroStopShortcut;

            stopBox.Text = "Ctrl+F24";
            stopBox.Visibility = Visibility.Collapsed;

            var stopLabel = FindVisualChildren<TextBlock>(panel)
                .FirstOrDefault(item => string.Equals(item.Text, "停止", StringComparison.Ordinal));
            if (stopLabel != null)
                stopLabel.Visibility = Visibility.Collapsed;

            var saveSettingsButton = FindVisualChildren<Button>(panel)
                .FirstOrDefault(button => string.Equals(button.Content as string, "保存设置", StringComparison.Ordinal));
            if (saveSettingsButton != null)
            {
                saveSettingsButton.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args)
                {
                    if (TrySaveInputMemoryShortcutSettingsFromPanel(recordBox, replayBox))
                        args.Handled = true;
                };
            }
        }

        private void RefreshInputMemoryPanelRecordingButton()
        {
            if (_inputMemoryPanel == null)
                return;

            var recordButton = FindVisualChildren<Button>(_inputMemoryPanel)
                .FirstOrDefault(item => string.Equals(item.Tag as string, InputMemoryRecordButtonTag, StringComparison.Ordinal));
            if (recordButton != null)
            {
                recordButton.Content = GetInputMemoryRecordButtonText();
                recordButton.ToolTip = browser.IsInputMemoryRecording ? "停止录制并自动保存到默认脚本文件夹" : "开始记录键盘和鼠标操作";
            }

            var replayButton = FindVisualChildren<Button>(_inputMemoryPanel)
                .FirstOrDefault(button => string.Equals(button.Content as string, "开始播放", StringComparison.Ordinal)
                    || string.Equals(button.Content as string, "停止播放", StringComparison.Ordinal)
                    || string.Equals(button.Content as string, "回放", StringComparison.Ordinal));
            if (replayButton != null)
                replayButton.Content = browser.IsInputMemoryPlaying ? "停止播放" : "开始播放";
        }

        private string GetInputMemoryRecordButtonText()
        {
            return browser != null && browser.IsInputMemoryRecording ? "停止录制并保存" : "开始录制";
        }

        private async System.Threading.Tasks.Task PlaySelectedInputMacroFromPanelAsync(Window panel)
        {
            if (browser.IsInputMemoryPlaying)
            {
                browser.StopInputMemoryPlayback();
                SetInputMacroHint("键鼠精灵：已停止播放");
                RefreshInputMemoryPanelRecordingButton();
                return;
            }

            var selected = GetSelectedInputMacroFileFromPanel(panel);
            if (selected == null)
            {
                WindowManager.Alert("请先选择一个脚本。", "键鼠精灵");
                return;
            }

            var replayCountBox = GetReplayCountTextBox(panel);
            var loopUntilStopped = GetLoopUntilStopped(panel);
            var replayCount = GetInputMacroReplayCount();
            if (!loopUntilStopped && (replayCountBox == null || !int.TryParse(replayCountBox.Text, out replayCount) || replayCount <= 0))
            {
                WindowManager.Alert("回放次数必须是大于 0 的整数。", "键鼠精灵");
                return;
            }

            if (!TryGetLoopIntervalMs(panel, out var intervalMs))
                return;

            GlobalData.Settings.InputMacroReplayCount = replayCount;
            GlobalData.Settings.InputMacroLoopUntilStopped = loopUntilStopped;
            GlobalData.SaveSettings();

            try
            {
                await LoadInputMemoryMacroAsync(selected.Path);
                await ReplayInputMemoryAsync(
                    loopCount: loopUntilStopped ? 0 : replayCount,
                    loopIntervalMs: intervalMs);
            }
            catch (Exception e)
            {
                LogHelper.LogError("Failed to replay selected input macro", e);
                WindowManager.ShowError(e.Message);
            }
            finally
            {
                RefreshInputMemoryPanelRecordingButton();
            }
        }

        private bool TryGetLoopIntervalMs(Window panel, out int intervalMs)
        {
            intervalMs = 1000;
            var intervalBox = FindVisualChildren<TextBox>(panel)
                .FirstOrDefault(box => string.Equals(box.Tag as string, InputMemoryLoopIntervalBoxTag, StringComparison.Ordinal));
            if (intervalBox == null || string.IsNullOrWhiteSpace(intervalBox.Text))
                return true;

            if (!double.TryParse(intervalBox.Text, out var seconds) || seconds < 0.01 || seconds > 30)
            {
                WindowManager.Alert("两次之间间隔时间必须在 0.01 到 30 秒之间。", "键鼠精灵");
                return false;
            }

            intervalMs = (int)Math.Round(seconds * 1000);
            return true;
        }

        private bool GetLoopUntilStopped(Window panel)
        {
            var checkBox = FindVisualChildren<CheckBox>(panel)
                .FirstOrDefault(box => string.Equals(box.Content as string, "持续播放", StringComparison.Ordinal) || string.Equals(box.Content as string, "持续循环直到停止", StringComparison.Ordinal));
            return checkBox?.IsChecked == true;
        }

        private TextBox GetReplayCountTextBox(Window panel)
        {
            var textBoxes = FindVisualChildren<TextBox>(panel).ToList();
            return textBoxes.Count > 0 ? textBoxes[0] : null;
        }

        private InputMacroFile GetSelectedInputMacroFileFromPanel(Window panel)
        {
            return FindVisualChildren<ListBox>(panel).FirstOrDefault()?.SelectedItem as InputMacroFile;
        }

        private void DeleteSelectedInputMacroFromPanel(Window panel)
        {
            var listBox = FindVisualChildren<ListBox>(panel).FirstOrDefault();
            var selected = listBox?.SelectedItem as InputMacroFile;
            if (selected == null)
            {
                WindowManager.Alert("请先选择一个脚本。", "键鼠精灵");
                return;
            }

            WindowManager.Confirm($"确定删除记录：{selected.Macro?.Name ?? selected.FileName}？", "键鼠精灵", result =>
            {
                if (result != true)
                    return;

                try
                {
                    if (File.Exists(selected.Path))
                        File.Delete(selected.Path);

                    if (string.Equals(_selectedInputMacroPath, selected.Path, StringComparison.OrdinalIgnoreCase))
                        _selectedInputMacroPath = null;

                    RefreshInputMemoryPanelList(panel, null);
                    InputMacroService.SaveOrder(InputMacroService.ListSavedMacros());
                    LogHelper.LogInfo($"[InputMemory] deleted macro; path={selected.Path}");
                }
                catch (Exception e)
                {
                    LogHelper.LogError("[InputMemory] failed to delete input macro", e);
                    WindowManager.ShowError(e.Message);
                }
            });
        }

        private async System.Threading.Tasks.Task ImportInputMacroFileIntoDefaultFolderAsync(Window panel)
        {
            try
            {
                Directory.CreateDirectory(InputMacroService.DirectoryPath);
                var dialog = new OpenFileDialog
                {
                    InitialDirectory = Directory.Exists(InputMacroService.DirectoryPath)
                        ? InputMacroService.DirectoryPath
                        : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    Filter = "键鼠脚本|*.json|所有文件|*.*",
                    CheckFileExists = true,
                    Multiselect = false,
                    Title = "选择要载入的键鼠脚本"
                };

                if (dialog.ShowDialog(panel) != true)
                    return;

                var sourcePath = dialog.FileName;
                var macro = InputMacroService.Load(sourcePath);
                var importedPath = InputMacroService.Save(macro);
                await LoadInputMemoryMacroAsync(importedPath);
                _selectedInputMacroPath = importedPath;

                RefreshInputMemoryPanelList(panel, importedPath);
                WindowManager.Alert($"已载入并复制到默认文件夹：{Path.GetFileName(importedPath)}（{macro.Events.Count} 个事件）", "键鼠精灵");
                LogHelper.LogInfo($"[InputMemory] imported macro; source={sourcePath}; target={importedPath}; count={macro.Events.Count}");
            }
            catch (Exception e)
            {
                LogHelper.LogError("[InputMemory] failed to import input macro file", e);
                WindowManager.ShowError(e.Message);
            }
        }

        private static void RefreshInputMemoryPanelList(Window panel, string selectedPath)
        {
            var listBox = FindVisualChildren<ListBox>(panel).FirstOrDefault();
            if (listBox == null)
                return;

            if (listBox.ItemsSource is System.Collections.IList items)
            {
                items.Clear();
                foreach (var macro in InputMacroService.ListSavedMacros())
                    items.Add(macro);
            }

            foreach (var item in listBox.Items)
            {
                if (item is InputMacroFile macroFile
                    && string.Equals(macroFile.Path, selectedPath, StringComparison.OrdinalIgnoreCase))
                {
                    listBox.SelectedItem = item;
                    listBox.ScrollIntoView(item);
                    return;
                }
            }

            if (listBox.Items.Count > 0 && listBox.SelectedItem == null)
                listBox.SelectedIndex = 0;
        }

        private static void RemoveInputMemoryPanelButton(Panel panel, string text)
        {
            var buttons = panel.Children
                .OfType<Button>()
                .Where(button => string.Equals(button.Content as string, text, StringComparison.Ordinal))
                .ToList();

            foreach (var button in buttons)
                panel.Children.Remove(button);
        }

        private static Button CreateInputMemoryPanelButton(string text, double minWidth)
        {
            return new Button
            {
                Content = text,
                MinWidth = minWidth,
                Height = 28,
                Margin = new Thickness(0, 0, 8, 6),
                Padding = new Thickness(8, 0, 8, 0)
            };
        }

        private bool TrySaveInputMemoryShortcutSettingsFromPanel(TextBox recordBox, TextBox replayBox)
        {
            if (recordBox == null || replayBox == null)
                return false;

            if (!TryParseInputMacroHotkey(recordBox.Text, out _, out _)
                || !TryParseInputMacroHotkey(replayBox.Text, out _, out _))
            {
                WindowManager.Alert("快捷键格式无效。可用 Ctrl/Shift/Alt + 字母、数字、F1-F24、-、=、[、]、\\、;、'、,、.、/、`、Space、Esc。", "键鼠精灵");
                return true;
            }

            var recordShortcut = NormalizeInputMacroShortcut(recordBox.Text);
            var playbackShortcut = NormalizeInputMacroShortcut(replayBox.Text);
            if (string.Equals(recordShortcut, playbackShortcut, StringComparison.OrdinalIgnoreCase))
            {
                WindowManager.Alert("录制和播放快捷键不能重复。", "键鼠精灵");
                return true;
            }

            GlobalData.Settings.InputMacroRecordShortcut = recordShortcut;
            GlobalData.Settings.InputMacroReplayShortcut = playbackShortcut;
            GlobalData.Settings.InputMacroStopShortcut = "Ctrl+F24";
            GlobalData.SaveSettings();
            WindowManager.Alert("键鼠精灵设置已保存。", "键鼠精灵");
            LogHelper.LogInfo($"[InputMemory] shortcut settings saved; record={recordShortcut} playback={playbackShortcut}");
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
