using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace CefFlashBrowser.FlashBrowser
{
    public partial class ChromiumFlashBrowser
    {
        private void ShowInputMemoryScreenMarker(int screenX, int screenY, bool release, string source)
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    const int size = 30;
                    var stroke = release
                        ? Brushes.DeepSkyBlue
                        : string.Equals(source, "record", StringComparison.OrdinalIgnoreCase)
                            ? Brushes.LimeGreen
                            : Brushes.Red;

                    var window = new Window
                    {
                        Width = size,
                        Height = size,
                        Left = screenX - size / 2.0,
                        Top = screenY - size / 2.0,
                        WindowStyle = WindowStyle.None,
                        AllowsTransparency = true,
                        Background = Brushes.Transparent,
                        Topmost = true,
                        ShowInTaskbar = false,
                        ShowActivated = false,
                        IsHitTestVisible = false,
                        Content = new Grid
                        {
                            Children =
                            {
                                new Ellipse
                                {
                                    Stroke = stroke,
                                    StrokeThickness = 3,
                                    Fill = Brushes.Transparent,
                                    Width = size - 4,
                                    Height = size - 4,
                                    HorizontalAlignment = HorizontalAlignment.Center,
                                    VerticalAlignment = VerticalAlignment.Center
                                }
                            }
                        }
                    };

                    window.Show();
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
                    timer.Tick += delegate
                    {
                        timer.Stop();
                        window.Close();
                    };
                    timer.Start();
                }));
            }
            catch (Exception e)
            {
                FeatureDiagnostics.Log("InputMemory", $"failed to show {source} marker; x={screenX}; y={screenY}; error={e.Message}");
            }
        }
    }
}
