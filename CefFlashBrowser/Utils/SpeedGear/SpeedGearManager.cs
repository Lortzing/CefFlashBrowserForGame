using CefFlashBrowser.FlashBrowser;

namespace CefFlashBrowser.Utils.SpeedGear
{
    internal static class SpeedGearManager
    {
        public static void SetSpeedFactor(double speedFactor)
        {
            SpeedGearController.SetFactor(speedFactor);
        }
    }
}
