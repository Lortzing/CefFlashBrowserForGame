using CefFlashBrowser.Utils;
using CefFlashBrowser.Utils.InputMacros;
using Microsoft.Win32;
using System;
using System.IO;
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
                if (_inputMemoryMenu == null)
                {
                    _inputMemoryMenu = CreateInputMemoryMenu();
                }
                _ = RefreshInputMemoryMenuAsync();
                OpenBottomContextMenu(button, _inputMemoryMenu);
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
                    WindowManager.Alert($"已保存：{Path.GetFileName(path)}", "键鼠记忆");
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

                var macro = InputMacroService.Load(dialog.FileName);
                await browser.ImportInputMemoryEventsJsonAsync(InputMacroService.ExportEventsJson(macro));
                WindowManager.Alert($"已加载：{macro.Name}（{macro.Events.Count} 个事件）", "键鼠记忆");
            }
            catch (Exception e)
            {
                LogHelper.LogError("Failed to load input macro", e);
                WindowManager.ShowError(e.Message);
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
