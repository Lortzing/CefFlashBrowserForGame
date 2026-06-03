using CefFlashBrowser.Data;
using CefFlashBrowser.Models;
using CefFlashBrowser.Utils;
using CefFlashBrowser.Utils.InputMacros;
using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace CefFlashBrowser.Views
{
    public partial class BrowserWindow
    {
        private bool _featureButtonsAdded = false;
        private ContextMenu _speedGearMenu;
        private ContextMenu _inputMemoryMenu;
        private MenuItem _inputRecordMenuItem;
        private MenuItem _inputReplayMenuItem;
        private MenuItem _inputReplayFastMenuItem;
        private MenuItem _inputReplayLoopMenuItem;
        private MenuItem _inputStopReplayMenuItem;
        private MenuItem _inputSaveMenuItem;
        private MenuItem _inputClearMenuItem;
        private InputMemoryPanel _inputMemoryPanel;
        private string _selectedInputMacroPath;

        static BrowserWindow()
        {
            EventManager.RegisterClassHandler(
                typeof(BrowserWindow),
                LoadedEvent,
                new RoutedEventHandler(OnBrowserWindowLoadedFeatureButtons));
        }

        private static void OnBrowserWindowLoadedFeatureButtons(object sender, RoutedEventArgs e)
        {
            if (sender is BrowserWindow window)
            {
                window.AddFeatureButtons();
            }
        }

        private void AddFeatureButtons()
        {
            if (_featureButtonsAdded || findPopup == null)
            {
                return;
            }

            var toolbar = FindParent<StackPanel>(findPopup);
            if (toolbar == null)
            {
                return;
            }

            var insertIndex = toolbar.Children.IndexOf(findPopup);
            if (insertIndex < 0)
            {
                insertIndex = toolbar.Children.Count;
            }

            toolbar.Children.Insert(insertIndex, CreateSpeedGearButton());
            toolbar.Children.Insert(insertIndex + 1, CreateInputMemoryButton());
            _featureButtonsAdded = true;
        }

        private Button CreateSpeedGearButton()
        {
            var button = CreateToolbarButton("⏩", "全局变速齿轮");
            button.Click += delegate
            {
                if (_speedGearMenu == null)
                {
                    _speedGearMenu = CreateSpeedGearMenu();
                }
                OpenBottomContextMenu(button, _speedGearMenu);
            };
            return button;
        }

        private Button CreateInputMemoryButton()
        {
            var button = CreateToolbarButton("⌨", "键鼠记忆");
            button.Click += delegate
            {
                if (_inputMemoryPanel == null)
                {
                    _inputMemoryPanel = new InputMemoryPanel(this)
                    {
                        Owner = this
                    };
                    _inputMemoryPanel.Closed += delegate { _inputMemoryPanel = null; };
                }
                _inputMemoryPanel.ReloadMacros();
                _inputMemoryPanel.Show();
                _inputMemoryPanel.Activate();
            };
            return button;
        }

        private Button CreateToolbarButton(string icon, string tooltip)
        {
            return new Button
            {
                Width = 30,
                Height = 30,
                Padding = new Thickness(0),
                Margin = new Thickness(10, 0, 0, 0),
                ToolTip = tooltip,
                Content = new TextBlock
                {
                    Text = icon,
                    FontSize = 14,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center
                }
            };
        }

        private ContextMenu CreateSpeedGearMenu()
        {
            var menu = new ContextMenu
            {
                VerticalOffset = -8,
                HorizontalOffset = 8
            };

            menu.Items.Add(new MenuItem { Header = "全局倍率", IsEnabled = false });
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateSpeedGearMenuItem("0.5x", 0.5));
            menu.Items.Add(CreateSpeedGearMenuItem("1x", 1.0));
            menu.Items.Add(CreateSpeedGearMenuItem("2x", 2.0));
            menu.Items.Add(CreateSpeedGearMenuItem("5x", 5.0));
            menu.Items.Add(CreateSpeedGearMenuItem("10x", 10.0));
            menu.Items.Add(CreateSpeedGearMenuItem("20x", 20.0));
            menu.Items.Add(CreateSpeedGearMenuItem("50x", 50.0));
            menu.Items.Add(CreateSpeedGearMenuItem("100x", 100.0));

            return menu;
        }

        private MenuItem CreateSpeedGearMenuItem(string header, double factor)
        {
            var item = new MenuItem { Header = header };
            item.Click += delegate
            {
                browser.SetSpeedGearFactor(factor);
                Keyboard.Focus(browser);
            };
            return item;
        }

        private ContextMenu CreateInputMemoryMenu()
        {
            var menu = new ContextMenu
            {
                VerticalOffset = -8,
                HorizontalOffset = 8
            };

            _inputRecordMenuItem = CreateInputMemoryMenuItem("开始记录", delegate
            {
                ToggleInputMemoryRecording();
                Keyboard.Focus(browser);
            });
            menu.Items.Add(_inputRecordMenuItem);

            menu.Items.Add(new Separator());

            _inputReplayMenuItem = CreateInputMemoryMenuItem("回放一次", async delegate
            {
                await ReplayInputMemoryAsync();
                await RefreshInputMemoryMenuAsync();
            });
            menu.Items.Add(_inputReplayMenuItem);

            _inputReplayFastMenuItem = CreateInputMemoryMenuItem("2x 回放", async delegate
            {
                await ReplayInputMemoryAsync(speed: 2.0);
                await RefreshInputMemoryMenuAsync();
            });
            menu.Items.Add(_inputReplayFastMenuItem);

            _inputReplayLoopMenuItem = CreateInputMemoryMenuItem("循环 3 次", async delegate
            {
                await ReplayInputMemoryAsync(loopCount: 3, loopIntervalMs: 1000);
                await RefreshInputMemoryMenuAsync();
            });
            menu.Items.Add(_inputReplayLoopMenuItem);

            _inputStopReplayMenuItem = CreateInputMemoryMenuItem("停止回放", delegate
            {
                browser.StopInputMemoryPlayback();
                Keyboard.Focus(browser);
            });
            menu.Items.Add(_inputStopReplayMenuItem);

            menu.Items.Add(new Separator());

            _inputSaveMenuItem = CreateInputMemoryMenuItem("保存脚本...", async delegate
            {
                await SaveInputMemoryMacroAsync();
                await RefreshInputMemoryMenuAsync();
            });
            menu.Items.Add(_inputSaveMenuItem);

            menu.Items.Add(CreateInputMemoryMenuItem("加载脚本...", async delegate
            {
                await LoadInputMemoryMacroAsync();
                await RefreshInputMemoryMenuAsync();
            }));

            _inputClearMenuItem = CreateInputMemoryMenuItem("清空记录", delegate
            {
                WindowManager.Confirm("确定清空当前键鼠记录？", "键鼠记忆", result =>
                {
                    if (result == true)
                    {
                        browser.ClearInputMemory();
                        Keyboard.Focus(browser);
                        _ = RefreshInputMemoryMenuAsync();
                    }
                });
            });
            menu.Items.Add(_inputClearMenuItem);

            return menu;
        }

        private MenuItem CreateInputMemoryMenuItem(string header, RoutedEventHandler click)
        {
            var item = new MenuItem { Header = header };
            item.Click += click;
            return item;
        }

        private void ToggleInputMemoryRecording()
        {
            if (browser.IsInputMemoryRecording)
            {
                browser.StopInputMemoryRecording();
            }
            else
            {
                browser.StartInputMemoryRecording();
            }

            _ = RefreshInputMemoryMenuAsync();
        }

        private async System.Threading.Tasks.Task RefreshInputMemoryMenuAsync()
        {
            bool hasEvents;
            try
            {
                await browser.RefreshInputMemoryStatusAsync();
                hasEvents = browser.InputMemoryEventCount > 0;
            }
            catch (Exception e)
            {
                LogHelper.LogError("Failed to refresh input macro menu", e);
                return;
            }

            if (_inputRecordMenuItem != null)
            {
                _inputRecordMenuItem.Header = browser.IsInputMemoryRecording ? "停止记录" : "开始记录";
                _inputRecordMenuItem.IsEnabled = !browser.IsInputMemoryPlaying;
            }

            if (_inputReplayMenuItem != null)
                _inputReplayMenuItem.IsEnabled = hasEvents && !browser.IsInputMemoryRecording && !browser.IsInputMemoryPlaying;
            if (_inputReplayFastMenuItem != null)
                _inputReplayFastMenuItem.IsEnabled = hasEvents && !browser.IsInputMemoryRecording && !browser.IsInputMemoryPlaying;
            if (_inputReplayLoopMenuItem != null)
                _inputReplayLoopMenuItem.IsEnabled = hasEvents && !browser.IsInputMemoryRecording && !browser.IsInputMemoryPlaying;
            if (_inputStopReplayMenuItem != null)
                _inputStopReplayMenuItem.IsEnabled = browser.IsInputMemoryPlaying;
            if (_inputSaveMenuItem != null)
                _inputSaveMenuItem.IsEnabled = hasEvents && !browser.IsInputMemoryPlaying;
            if (_inputClearMenuItem != null)
                _inputClearMenuItem.IsEnabled = hasEvents || browser.IsInputMemoryRecording || browser.IsInputMemoryPlaying;
        }

        private async System.Threading.Tasks.Task SaveInputMemoryMacroAsync()
        {
            await SaveInputMemoryMacroAsync(null);
        }

        private async System.Threading.Tasks.Task SaveInputMemoryMacroAsync(Action saved)
        {
            try
            {
                var eventsJson = await browser.ExportInputMemoryEventsJsonAsync();
                var macro = InputMacroService.CreateMacro(InputMacroService.CreateDefaultName(), browser.Address, eventsJson);
                if (macro.Events.Count == 0)
                {
                    WindowManager.Alert("当前脚本为空，不能保存。", "键鼠记忆");
                    return;
                }

                WindowManager.Prompt("请输入脚本名称：", "保存键鼠脚本", macro.Name, (result, name) =>
                {
                    if (result != true)
                    {
                        return;
                    }

                    macro.Name = string.IsNullOrWhiteSpace(name) ? macro.Name : name.Trim();
                    var path = InputMacroService.Save(macro);
                    _selectedInputMacroPath = path;
                    WindowManager.Alert($"已保存：{Path.GetFileName(path)}", "键鼠记忆");
                    saved?.Invoke();
                });
            }
            catch (Exception e)
            {
                LogHelper.LogError("Failed to save input macro", e);
                WindowManager.ShowError(e.Message);
            }
        }

        private async System.Threading.Tasks.Task ReplayInputMemoryAsync(double speed = 1.0, int loopCount = 1, int loopIntervalMs = 0)
        {
            try
            {
                await browser.ReplayInputMemoryAsync(speed, loopCount, loopIntervalMs);
            }
            catch (Exception e)
            {
                LogHelper.LogError("Failed to replay input macro", e);
                WindowManager.ShowError(e.Message);
            }
        }

        private async System.Threading.Tasks.Task LoadInputMemoryMacroAsync()
        {
            try
            {
                Directory.CreateDirectory(InputMacroService.DirectoryPath);
                var dialog = new OpenFileDialog
                {
                    InitialDirectory = InputMacroService.DirectoryPath,
                    Filter = "键鼠脚本|*.json",
                    CheckFileExists = true
                };

                if (dialog.ShowDialog() != true)
                {
                    return;
                }

                var macro = await LoadInputMemoryMacroAsync(dialog.FileName);
                WindowManager.Alert($"已加载：{macro.Name}（{macro.Events.Count} 个事件）", "键鼠记忆");
            }
            catch (Exception e)
            {
                LogHelper.LogError("Failed to load input macro", e);
                WindowManager.ShowError(e.Message);
            }
        }

        private async System.Threading.Tasks.Task<InputMacro> LoadInputMemoryMacroAsync(string path)
        {
            var macro = InputMacroService.Load(path);
            await browser.ImportInputMemoryEventsJsonAsync(InputMacroService.ExportEventsJson(macro));
            _selectedInputMacroPath = path;
            return macro;
        }

        private int GetInputMacroReplayCount()
        {
            return Math.Max(1, GlobalData.Settings.InputMacroReplayCount);
        }

        private async System.Threading.Tasks.Task ReplaySelectedInputMacroAsync()
        {
            var path = _selectedInputMacroPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                path = InputMacroService.ListSavedMacros().FirstOrDefault()?.Path;
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                WindowManager.Alert("默认文件夹中没有可回放的脚本。", "键鼠记忆");
                return;
            }

            await LoadInputMemoryMacroAsync(path);
            await ReplayInputMemoryAsync(
                loopCount: GlobalData.Settings.InputMacroLoopUntilStopped ? 0 : GetInputMacroReplayCount(),
                loopIntervalMs: 0);
        }

        private sealed class InputMemoryPanel : Window
        {
            private readonly BrowserWindow owner;
            private readonly ObservableCollection<InputMacroFile> macros = new ObservableCollection<InputMacroFile>();
            private readonly ListBox listBox;
            private readonly TextBox replayCountTextBox;
            private readonly CheckBox loopUntilStoppedCheckBox;
            private readonly TextBox recordShortcutTextBox;
            private readonly TextBox replayShortcutTextBox;
            private readonly TextBox stopShortcutTextBox;

            public InputMemoryPanel(BrowserWindow owner)
            {
                this.owner = owner;
                Title = "键鼠精灵";
                Width = 620;
                Height = 520;
                MinWidth = 560;
                MinHeight = 460;
                WindowStartupLocation = WindowStartupLocation.CenterOwner;

                var root = new DockPanel { Margin = new Thickness(12) };
                Content = root;

                var footer = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 10, 0, 0)
                };
                DockPanel.SetDock(footer, Dock.Bottom);
                root.Children.Add(footer);

                var closeButton = CreatePanelButton("关闭");
                closeButton.Click += delegate { Close(); };
                footer.Children.Add(closeButton);

                var main = new Grid();
                main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                root.Children.Add(main);

                listBox = new ListBox
                {
                    ItemsSource = macros,
                    MinHeight = 220
                };
                listBox.MouseDoubleClick += async delegate { await LoadSelectedAsync(); };
                main.Children.Add(listBox);

                var controls = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
                Grid.SetRow(controls, 1);
                main.Children.Add(controls);

                var listButtons = new WrapPanel();
                controls.Children.Add(listButtons);
                AddButton(listButtons, "刷新", delegate { ReloadMacros(); });
                AddButton(listButtons, "载入", async delegate { await LoadSelectedAsync(); });
                AddButton(listButtons, "重命名", delegate { RenameSelected(); });
                AddButton(listButtons, "上移", delegate { MoveSelected(-1); });
                AddButton(listButtons, "下移", delegate { MoveSelected(1); });
                AddButton(listButtons, "保存当前记录", async delegate { await owner.SaveInputMemoryMacroAsync(ReloadMacros); });

                var playbackPanel = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
                controls.Children.Add(playbackPanel);
                playbackPanel.Children.Add(new TextBlock
                {
                    Text = "回放次数",
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0)
                });
                replayCountTextBox = new TextBox
                {
                    Width = 56,
                    Text = owner.GetInputMacroReplayCount().ToString(),
                    Margin = new Thickness(0, 0, 12, 0)
                };
                playbackPanel.Children.Add(replayCountTextBox);
                loopUntilStoppedCheckBox = new CheckBox
                {
                    Content = "持续循环直到停止",
                    IsChecked = GlobalData.Settings.InputMacroLoopUntilStopped,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 12, 0)
                };
                replayCountTextBox.IsEnabled = loopUntilStoppedCheckBox.IsChecked != true;
                loopUntilStoppedCheckBox.Checked += delegate { replayCountTextBox.IsEnabled = false; };
                loopUntilStoppedCheckBox.Unchecked += delegate { replayCountTextBox.IsEnabled = true; };
                playbackPanel.Children.Add(loopUntilStoppedCheckBox);
                AddButton(playbackPanel, "回放", async delegate { await PlaySelectedAsync(); });
                AddButton(playbackPanel, "停止回放", delegate { owner.browser.StopInputMemoryPlayback(); });

                var shortcutGrid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
                shortcutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                shortcutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
                shortcutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                shortcutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
                shortcutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                shortcutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
                shortcutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                controls.Children.Add(shortcutGrid);

                recordShortcutTextBox = AddShortcutBox(shortcutGrid, "录制", GlobalData.Settings.InputMacroRecordShortcut, 0);
                replayShortcutTextBox = AddShortcutBox(shortcutGrid, "回放", GlobalData.Settings.InputMacroReplayShortcut, 2);
                stopShortcutTextBox = AddShortcutBox(shortcutGrid, "停止", GlobalData.Settings.InputMacroStopShortcut, 4);
                var saveShortcutButton = CreatePanelButton("保存设置");
                saveShortcutButton.Click += delegate { SaveSettingsFromControls(showMessage: true); };
                Grid.SetColumn(saveShortcutButton, 6);
                shortcutGrid.Children.Add(saveShortcutButton);

                ReloadMacros();
            }

            public void ReloadMacros()
            {
                macros.Clear();
                foreach (var macro in InputMacroService.ListSavedMacros())
                {
                    macros.Add(macro);
                }

                if (!string.IsNullOrWhiteSpace(owner._selectedInputMacroPath))
                {
                    listBox.SelectedItem = macros.FirstOrDefault(item =>
                        string.Equals(item.Path, owner._selectedInputMacroPath, StringComparison.OrdinalIgnoreCase));
                }

                if (listBox.SelectedItem == null && macros.Count > 0)
                {
                    listBox.SelectedIndex = 0;
                }
            }

            private async System.Threading.Tasks.Task LoadSelectedAsync()
            {
                var selected = GetSelected();
                if (selected == null)
                {
                    return;
                }

                try
                {
                    await owner.LoadInputMemoryMacroAsync(selected.Path);
                    WindowManager.Alert($"已载入：{selected.Macro.Name}（{selected.EventCount} 个事件，{selected.DurationText}）", "键鼠精灵");
                }
                catch (Exception e)
                {
                    LogHelper.LogError("Failed to load selected input macro", e);
                    WindowManager.ShowError(e.Message);
                    ReloadMacros();
                }
            }

            private async System.Threading.Tasks.Task PlaySelectedAsync()
            {
                var selected = GetSelected();
                if (selected == null)
                {
                    WindowManager.Alert("请先选择一个脚本。", "键鼠精灵");
                    return;
                }

                if (!SaveSettingsFromControls(showMessage: false))
                {
                    return;
                }

                try
                {
                    await owner.LoadInputMemoryMacroAsync(selected.Path);
                    await owner.ReplayInputMemoryAsync(
                        loopCount: GlobalData.Settings.InputMacroLoopUntilStopped ? 0 : owner.GetInputMacroReplayCount(),
                        loopIntervalMs: 0);
                }
                catch (Exception e)
                {
                    LogHelper.LogError("Failed to replay selected input macro", e);
                    WindowManager.ShowError(e.Message);
                    ReloadMacros();
                }
            }

            private void RenameSelected()
            {
                var selected = GetSelected();
                if (selected == null)
                {
                    return;
                }

                WindowManager.Prompt("请输入新的脚本名称：", "重命名脚本", selected.Macro.Name, (result, name) =>
                {
                    if (result != true)
                    {
                        return;
                    }

                    try
                    {
                        var renamed = InputMacroService.Rename(selected, name);
                        owner._selectedInputMacroPath = renamed.Path;
                        ReloadMacros();
                    }
                    catch (Exception e)
                    {
                        LogHelper.LogError("Failed to rename input macro", e);
                        WindowManager.ShowError(e.Message);
                    }
                });
            }

            private void MoveSelected(int offset)
            {
                var index = listBox.SelectedIndex;
                var next = index + offset;
                if (index < 0 || next < 0 || next >= macros.Count)
                {
                    return;
                }

                macros.Move(index, next);
                listBox.SelectedIndex = next;
                InputMacroService.SaveOrder(macros);
            }

            private InputMacroFile GetSelected()
            {
                return listBox.SelectedItem as InputMacroFile;
            }

            private bool SaveSettingsFromControls(bool showMessage)
            {
                var loopUntilStopped = loopUntilStoppedCheckBox.IsChecked == true;
                var replayCount = owner.GetInputMacroReplayCount();
                if (!loopUntilStopped && !TryParseReplayCount(out replayCount))
                {
                    WindowManager.Alert("回放次数必须是大于 0 的整数。", "键鼠精灵");
                    return false;
                }

                if (!TryParseShortcut(recordShortcutTextBox.Text, out _)
                    || !TryParseShortcut(replayShortcutTextBox.Text, out _)
                    || !TryParseShortcut(stopShortcutTextBox.Text, out _))
                {
                    WindowManager.Alert("快捷键格式无效，例如 Ctrl+F8、Ctrl+Shift+R、F10。", "键鼠精灵");
                    return false;
                }

                var shortcuts = new[]
                {
                    NormalizeShortcutText(recordShortcutTextBox.Text),
                    NormalizeShortcutText(replayShortcutTextBox.Text),
                    NormalizeShortcutText(stopShortcutTextBox.Text)
                };
                if (shortcuts.Distinct(StringComparer.OrdinalIgnoreCase).Count() != shortcuts.Length)
                {
                    WindowManager.Alert("录制、回放、停止快捷键不能重复。", "键鼠精灵");
                    return false;
                }

                GlobalData.Settings.InputMacroReplayCount = replayCount;
                GlobalData.Settings.InputMacroLoopUntilStopped = loopUntilStopped;
                GlobalData.Settings.InputMacroRecordShortcut = shortcuts[0];
                GlobalData.Settings.InputMacroReplayShortcut = shortcuts[1];
                GlobalData.Settings.InputMacroStopShortcut = shortcuts[2];
                GlobalData.SaveSettings();

                if (showMessage)
                {
                    WindowManager.Alert("键鼠精灵设置已保存。", "键鼠精灵");
                }

                return true;
            }

            private bool TryParseReplayCount(out int replayCount)
            {
                return int.TryParse(replayCountTextBox.Text, out replayCount) && replayCount > 0;
            }

            private static TextBox AddShortcutBox(Grid grid, string label, string value, int column)
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
                    Margin = new Thickness(0, 0, 12, 0)
                };
                Grid.SetColumn(textBox, column + 1);
                grid.Children.Add(textBox);
                return textBox;
            }

            private static Button CreatePanelButton(string text)
            {
                return new Button
                {
                    Content = text,
                    MinWidth = 78,
                    Height = 28,
                    Margin = new Thickness(0, 0, 8, 6),
                    Padding = new Thickness(8, 0, 8, 0)
                };
            }

            private static void AddButton(Panel panel, string text, RoutedEventHandler handler)
            {
                var button = CreatePanelButton(text);
                button.Click += handler;
                panel.Children.Add(button);
            }
        }

        private static T FindParent<T>(DependencyObject element) where T : DependencyObject
        {
            while (element != null)
            {
                element = LogicalTreeHelper.GetParent(element);
                if (element is T result)
                {
                    return result;
                }
            }
            return null;
        }
    }
}
