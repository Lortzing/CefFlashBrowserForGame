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
        private const string InputMemoryPlaybackButtonTag = "InputMemoryPlaybackButton";
        private const string InputMemoryReplayCountBoxTag = "InputMemoryReplayCountBox";
        private const string InputMemoryReplayCountModeTag = "InputMemoryReplayCountMode";
        private const string InputMemoryReplayLoopModeTag = "InputMemoryReplayLoopMode";
        private const string InputMemoryRecordShortcutBoxTag = "InputMemoryRecordShortcutBox";
        private const string InputMemoryPlaybackShortcutBoxTag = "InputMemoryPlaybackShortcutBox";
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
                FixInputMemoryTopButtonRow(panel, wrapPanels[0]);
            }

            if (wrapPanels.Count > 1)
            {
                ReplaceInputMemoryPlaybackPanel(panel, wrapPanels[1]);
            }

            var shortcutGrid = FindVisualChildren<Grid>(panel)
                .FirstOrDefault(grid => FindVisualChildren<TextBox>(grid).Count() >= 3);
            if (shortcutGrid != null)
            {
                ReplaceInputMemoryShortcutGrid(panel, shortcutGrid);
            }

            RefreshInputMemoryPanelRecordingButton();
            LogHelper.LogInfo("[InputMemory] input macro panel fixups applied");
        }

        private void FixInputMemoryTopButtonRow(Window panel, WrapPanel topPanel)
        {
            RemoveInputMemoryPanelButton(topPanel, "保存当前记录");
            RemoveInputMemoryPanelButton(topPanel, "停止并自动保存");
            RemoveInputMemoryPanelButton(topPanel, "开始/停止录制");

            if (!topPanel.Children.OfType<Button>().Any(button => string.Equals(button.Tag as string, InputMemoryRecordButtonTag, StringComparison.Ordinal)))
            {
                var recordButton = CreateInputMemoryPanelButton(GetInputMemoryRecordButtonText(), 112);
                recordButton.Tag = InputMemoryRecordButtonTag;
                recordButton.Click += async delegate
                {
                    if (browser.IsInputMemoryRecording)
                    {
                        await StopAndAutoSaveInputMacroAsync("键鼠精灵窗口停止录制并保存");
                    }
                    else
                    {
                        browser.StartInputMemoryRecording();
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
        }

        private void ReplaceInputMemoryPlaybackPanel(Window panel, WrapPanel playbackPanel)
        {
            playbackPanel.Children.Clear();

            var playbackButton = CreateInputMemoryPanelButton(GetInputMemoryPlaybackButtonText(), 96);
            playbackButton.Tag = InputMemoryPlaybackButtonTag;
            playbackButton.Click += async delegate
            {
                if (browser.IsInputMemoryPlaying)
                {
                    browser.StopInputMemoryPlayback();
                    SetInputMacroHint("键鼠精灵：已停止播放");
                    RefreshInputMemoryPanelRecordingButton();
                    return;
                }

                if (!SaveInputMemoryPlaybackModeFromPanel(panel, showMessage: true))
                    return;

                var selected = GetSelectedInputMacroFileFromPanel(panel);
                if (selected != null)
                    _selectedInputMacroPath = selected.Path;

                await ReplaySelectedInputMacroAsync();
                RefreshInputMemoryPanelRecordingButton();
            };
            playbackPanel.Children.Add(playbackButton);

            var countMode = new RadioButton
            {
                Content = "次数播放",
                GroupName = "InputMemoryPlaybackMode",
                Tag = InputMemoryReplayCountModeTag,
                IsChecked = !GlobalData.Settings.InputMacroLoopUntilStopped,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 6, 6)
            };
            playbackPanel.Children.Add(countMode);

            var replayCountTextBox = new TextBox
            {
                Width = 56,
                Text = GetInputMacroReplayCount().ToString(),
                Tag = InputMemoryReplayCountBoxTag,
                IsEnabled = countMode.IsChecked == true,
                Margin = new Thickness(0, 0, 12, 6)
            };
            playbackPanel.Children.Add(replayCountTextBox);

            var loopMode = new RadioButton
            {
                Content = "持续播放",
                GroupName = "InputMemoryPlaybackMode",
                Tag = InputMemoryReplayLoopModeTag,
                IsChecked = GlobalData.Settings.InputMacroLoopUntilStopped,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 6)
            };
            playbackPanel.Children.Add(loopMode);

            countMode.Checked += delegate { replayCountTextBox.IsEnabled = true; };
            loopMode.Checked += delegate { replayCountTextBox.IsEnabled = false; };
        }

        private void ReplaceInputMemoryShortcutGrid(Window panel, Grid shortcutGrid)
        {
            shortcutGrid.Children.Clear();
            shortcutGrid.ColumnDefinitions.Clear();
            shortcutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            shortcutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            shortcutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            shortcutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            shortcutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var recordBox = AddTaggedShortcutBox(shortcutGrid, "录制", GlobalData.Settings.InputMacroRecordShortcut, 0, InputMemoryRecordShortcutBoxTag);
            var playbackShortcut = string.IsNullOrWhiteSpace(GlobalData.Settings.InputMacroReplayShortcut)
                ? GlobalData.Settings.InputMacroStopShortcut
                : GlobalData.Settings.InputMacroReplayShortcut;
            AddTaggedShortcutBox(shortcutGrid, "播放", playbackShortcut, 2, InputMemoryPlaybackShortcutBoxTag);

            var saveShortcutButton = CreateInputMemoryPanelButton("保存设置", 78);
            saveShortcutButton.Click += delegate { TrySaveInputMemoryShortcutSettingsFromPanel(panel); };
            Grid.SetColumn(saveShortcutButton, 4);
            shortcutGrid.Children.Add(saveShortcutButton);

            if (string.IsNullOrWhiteSpace(recordBox.Text))
                recordBox.Text = "Ctrl+F8";
        }

        private static TextBox AddTaggedShortcutBox(Grid grid, string label, string value, int column, string tag)
        {
            var textBlock = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            Grid.SetColumn(textBlock, column);
            grid.Children.Add(textBlock);

            var textBox = new TextBox
            {
                Text = value,
                Tag = tag,
                Margin = new Thickness(0, 0, 12, 0)
            };
            Grid.SetColumn(textBox, column + 1);
            grid.Children.Add(textBox);
            return textBox;
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

            var playbackButton = FindVisualChildren<Button>(_inputMemoryPanel)
                .FirstOrDefault(item => string.Equals(item.Tag as string, InputMemoryPlaybackButtonTag, StringComparison.Ordinal));
            if (playbackButton != null)
            {
                playbackButton.Content = GetInputMemoryPlaybackButtonText();
                playbackButton.ToolTip = browser.IsInputMemoryPlaying ? "停止当前播放" : "按右侧模式播放选中脚本";
            }
        }

        private string GetInputMemoryRecordButtonText()
        {
            return browser != null && browser.IsInputMemoryRecording ? "停止录制并保存" : "开始录制";
        }

        private string GetInputMemoryPlaybackButtonText()
        {
            return browser != null && browser.IsInputMemoryPlaying ? "停止播放" : "开始播放";
        }

        private bool SaveInputMemoryPlaybackModeFromPanel(Window panel, bool showMessage)
        {
            var countMode = FindVisualChildren<RadioButton>(panel)
                .FirstOrDefault(item => string.Equals(item.Tag as string, InputMemoryReplayCountModeTag, StringComparison.Ordinal));
            var countBox = FindVisualChildren<TextBox>(panel)
                .FirstOrDefault(item => string.Equals(item.Tag as string, InputMemoryReplayCountBoxTag, StringComparison.Ordinal));

            var loopUntilStopped = countMode?.IsChecked != true;
            var replayCount = GetInputMacroReplayCount();
            if (!loopUntilStopped)
            {
                if (countBox == null || !int.TryParse(countBox.Text, out replayCount) || replayCount <= 0)
                {
                    WindowManager.Alert("回放次数必须是大于 0 的整数。", "键鼠精灵");
                    return false;
                }
            }

            GlobalData.Settings.InputMacroReplayCount = replayCount;
            GlobalData.Settings.InputMacroLoopUntilStopped = loopUntilStopped;
            GlobalData.SaveSettings();
            if (showMessage)
                LogHelper.LogInfo($"[InputMemory] playback mode saved; loop={loopUntilStopped}; count={replayCount}");
            return true;
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

        private InputMacroFile GetSelectedInputMacroFileFromPanel(Window panel)
        {
            return FindVisualChildren<ListBox>(panel).FirstOrDefault()?.SelectedItem as InputMacroFile;
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

        private static bool TrySaveInputMemoryShortcutSettingsFromPanel(Window panel)
        {
            var recordBox = FindVisualChildren<TextBox>(panel)
                .FirstOrDefault(item => string.Equals(item.Tag as string, InputMemoryRecordShortcutBoxTag, StringComparison.Ordinal));
            var playbackBox = FindVisualChildren<TextBox>(panel)
                .FirstOrDefault(item => string.Equals(item.Tag as string, InputMemoryPlaybackShortcutBoxTag, StringComparison.Ordinal));

            if (recordBox == null || playbackBox == null)
                return false;

            if (!TryParseInputMacroHotkey(recordBox.Text, out _, out _)
                || !TryParseInputMacroHotkey(playbackBox.Text, out _, out _))
            {
                WindowManager.Alert("快捷键格式无效。可用 Ctrl/Shift/Alt + 字母、数字、F1-F24、-、=、[、]、\\、;、'、,、.、/、`、Space、Esc。", "键鼠精灵");
                return true;
            }

            var recordShortcut = NormalizeInputMacroShortcut(recordBox.Text);
            var playbackShortcut = NormalizeInputMacroShortcut(playbackBox.Text);
            if (string.Equals(recordShortcut, playbackShortcut, StringComparison.OrdinalIgnoreCase))
            {
                WindowManager.Alert("录制和播放快捷键不能重复。", "键鼠精灵");
                return true;
            }

            GlobalData.Settings.InputMacroRecordShortcut = recordShortcut;
            GlobalData.Settings.InputMacroReplayShortcut = playbackShortcut;
            GlobalData.Settings.InputMacroStopShortcut = playbackShortcut;
            SaveInputMemoryPlaybackModeFromPanel(panel, showMessage: false);
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
