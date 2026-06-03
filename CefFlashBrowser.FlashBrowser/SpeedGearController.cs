using System;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;

namespace CefFlashBrowser.FlashBrowser
{
    public static class SpeedGearController
    {
        public const double DefaultFactor = 1.0;
        public const double MinFactor = 0.5;
        public const double MaxFactor = 100.0;

        private const string MappingName = "Local\\CefFlashBrowser.SpeedGear";
        private const long MappingSize = 32;

        private static readonly object SyncRoot = new object();
        private static MemoryMappedFile _mapping;
        private static MemoryMappedViewAccessor _accessor;
        private static long _generation;
        private static double _factor = DefaultFactor;

        public static void EnsureInitialized()
        {
            lock (SyncRoot)
            {
                if (_accessor != null)
                {
                    return;
                }

                _mapping = MemoryMappedFile.CreateOrOpen(
                    MappingName,
                    MappingSize,
                    MemoryMappedFileAccess.ReadWrite);

                _accessor = _mapping.CreateViewAccessor(
                    0,
                    MappingSize,
                    MemoryMappedFileAccess.ReadWrite);

                WriteSharedStateLocked(_factor);
            }
        }

        public static double SetFactor(double factor)
        {
            factor = NormalizeFactor(factor);

            lock (SyncRoot)
            {
                EnsureInitialized();
                _factor = factor;
                WriteSharedStateLocked(factor);
            }

            Debug.WriteLine($"[SpeedGear] factor = {factor:0.###}x");
            return factor;
        }

        public static double NormalizeFactor(double factor)
        {
            if (double.IsNaN(factor) || double.IsInfinity(factor))
            {
                return DefaultFactor;
            }

            if (factor < MinFactor)
            {
                return MinFactor;
            }

            if (factor > MaxFactor)
            {
                return MaxFactor;
            }

            return factor;
        }

        private static void WriteSharedStateLocked(double factor)
        {
            _generation++;

            // Native struct layout: int64 generation at offset 0, double speed at offset 8.
            _accessor.Write(8, factor);
            _accessor.Write(0, _generation);
            _accessor.Flush();
        }
    }
}
