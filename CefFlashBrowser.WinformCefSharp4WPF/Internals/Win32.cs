using System;
using System.Runtime.InteropServices;

namespace CefFlashBrowser.WinformCefSharp4WPF.Internals
{
    internal static class Win32
    {
        [DllImport("user32.dll")]
        public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll")]
        public static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);
    }
}
