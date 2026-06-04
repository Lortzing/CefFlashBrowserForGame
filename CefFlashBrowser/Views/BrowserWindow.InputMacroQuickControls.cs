using CefFlashBrowser.Utils;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CefFlashBrowser.Views
{
    public partial class BrowserWindow
    {
        private static readonly bool InputMacroQuickControlsRegistered = RegisterInputMacroQuickControls();
        private bool _inputMacroQuickControlsAdded;
        private Button _inputMacroRecordQuickButton;

        private static bool RegisterInputMacroQuickControls()
        {
            EventManager.RegisterClassHandler(
                typeof(BrowserWindow),
                LoadedEvent,
                new RoutedEventHandler(OnBrowserWindowLoadedInputMacroQuickControls));
            return true;
        }

        private static void OnBrowserWindowLoadedInputMacroQuickControls(object sender, RoutedEventArgs e)
        {
            if (sender is BrowserWindow window)
            {
                window.AddInputMacroQuickControls();
            }
        }

        private void AddInputMacroQuickControls()
        {
            if (_inputMacroQuickControlsAdded || findPopup == null)
            {
                return;
            }

            var toolbar = FindParent<StackPanel>(findPopup);
            if (toolbar == null)
            {
                FeatureDiagnostics.Log("InputMemory", "quick controls not added: toolbar not found");
                return;
            }

            var insertIndex = toolbar.Children.IndexOf(findPopup);
            if (insertIndex < 0)
            {
                insertIndex = toolbar.Children.Count;
            }

            _inputMacroRecordQuickButton = CreateInputMacroQuickButton("录", "键鼠精灵：开始/停止录制");
            _inputMacroRecordQuickButton.Click += delegate
            {
                FeatureDiagnostics.Log("InputMemory", $"quick record clicked; before recording={browser.IsInputMemoryRecording} count={browser.InputMemoryEventCount}");
                ToggleInputMemoryRecording();
                FocusBrowserForInputMacro();
                UpdateInputMacroQuickRecordButton();
                FeatureDiagnostics.Log("InputMemory", $"quick record completed; after recording={browser.IsInputMemoryRecording} count={browser.InputMemoryEventCount}");
            };

            var saveButton = CreateInputMacroQuickButton("存", "键鼠精灵：保存当前录制");
            saveButton.Click += async delegate
            {
                FeatureDiagnostics.Log("InputMemory", $"quick save clicked; recording={browser.IsInputMemoryRecording} count={browser.InputMemoryEventCount}");
                if (browser.IsInputMemoryRecording)
                {
                    browser.StopInputMemoryRecording();
                    UpdateInputMacroQuickRecordButton();
                }
                await SaveInputMemoryMacroAsync();
                FocusBrowserForInputMacro();
            };

            var replayButton = CreateInputMacroQuickButton("放", "键鼠精灵：回放当前录制");
            replayButton.Click += async delegate
            {
                FeatureDiagnostics.Log("InputMemory", $"quick replay clicked; recording={browser.IsInputMemoryRecording} count={browser.InputMemoryEventCount}");
                if (browser.IsInputMemoryRecording)
                {
                    browser.StopInputMemoryRecording();
                    UpdateInputMacroQuickRecordButton();
                }
                await ReplayInputMemoryAsync();
                FocusBrowserForInputMacro();
            };

            var stopButton = CreateInputMacroQuickButton("停", "键鼠精灵：停止回放");
            stopButton.Click += delegate
            {
                FeatureDiagnostics.Log("InputMemory", $"quick stop clicked; playing={browser.IsInputMemoryPlaying} count={browser.InputMemoryEventCount}");
                browser.StopInputMemoryPlayback();
                FocusBrowserForInputMacro();
            };

            toolbar.Children.Insert(insertIndex, _inputMacroRecordQuickButton);
            toolbar.Children.Insert(insertIndex + 1, saveButton);
            toolbar.Children.Insert(insertIndex + 2, replayButton);
            toolbar.Children.Insert(insertIndex + 3, stopButton);
            _inputMacroQuickControlsAdded = true;
            UpdateInputMacroQuickRecordButton();
            FeatureDiagnostics.Log("InputMemory", "quick controls added");
        }

        private Button CreateInputMacroQuickButton(string text, string tooltip)
        {
            return new Button
            {
                Width = 30,
                Height = 30,
                Padding = new Thickness(0),
                Margin = new Thickness(6, 0, 0, 0),
                ToolTip = tooltip,
                Content = new TextBlock
                {
                    Text = text,
                    FontSize = 13,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center
                }
            };
        }

        private void UpdateInputMacroQuickRecordButton()
        {
            if (_inputMacroRecordQuickButton == null)
                return;

            _inputMacroRecordQuickButton.Content = new TextBlock
            {
                Text = browser.IsInputMemoryRecording ? "止录" : "录",
                FontSize = browser.IsInputMemoryRecording ? 11 : 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            };
        }

        private void FocusBrowserForInputMacro()
        {
            try
            {
                browser.Focus();
                Keyboard.Focus(browser);
                browser.GetBrowser()?.GetHost()?.SetFocus(true);
            }
            catch (Exception e)
            {
                FeatureDiagnostics.Log("InputMemory", "failed to focus browser for macro input", e);
            }
        }
    }
}
