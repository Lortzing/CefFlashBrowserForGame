using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
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
                    const int physicalSize = 30;
                    var stroke = release
                        ? Brushes.DeepSkyBlue
                        : string.Equals(source, "record", StringComparison.OrdinalIgnoreCase)
                            ? Brushes.LimeGreen
                            : Brushes.Red;

                    var window = new Window
                    {
                        Width = physicalSize,
                        Height = physicalSize,
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
                                    Width = physicalSize - 4,
                                    Height = physicalSize - 4,
                                    HorizontalAlignment = HorizontalAlignment.Center,
                                    VerticalAlignment = VerticalAlignment.Center
                                }
                            }
                        }
                    };

                    window.SourceInitialized += delegate
                    {
                        var hwnd = new WindowInteropHelper(window).Handle;
                        var x = screenX - physicalSize / 2;
                        var y = screenY - physicalSize / 2;
                        InputMarkerNativeMethods.SetWindowPos(
                            hwnd,
                            InputMarkerNativeMethods.HWND_TOPMOST,
                            x,
                            y,
                            physicalSize,
                            physicalSize,
                            InputMarkerNativeMethods.SWP_NOACTIVATE | InputMarkerNativeMethods.SWP_SHOWWINDOW);
                        FeatureDiagnostics.Log("InputMemory", $"{source} marker shown; screenX={screenX}; screenY={screenY}; hwnd={hwnd}");
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

        private static class InputMarkerNativeMethods
        {
            public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
            public const uint SWP_NOACTIVATE = 0x0010;
            public const uint SWP_SHOWWINDOW = 0x0040;

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        }
    }
}
