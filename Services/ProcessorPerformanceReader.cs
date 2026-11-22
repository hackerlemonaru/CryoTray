using System;
using System.Runtime.InteropServices;

namespace CryoTray.Services
{
    internal sealed class ProcessorPerformanceReader : IDisposable
    {
        private const uint PdhFmtDouble = 0x00000200;
        private const string CounterPath =
            @"\Processor Information(_Total)\% Processor Performance";
        private static readonly TimeSpan ReinitRetryInterval = TimeSpan.FromSeconds(30);

        private IntPtr _query;
        private IntPtr _counter;
        private DateTime _lastInitAttemptUtc = DateTime.MinValue;
        private bool _disposed;

        public ProcessorPerformanceReader()
        {
            TryInitialize();
        }

        private bool TryInitialize()
        {
            if (_disposed)
            {
                return false;
            }

            _lastInitAttemptUtc = DateTime.UtcNow;

            if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0)
            {
                _query = IntPtr.Zero;
                return false;
            }

            if (PdhAddEnglishCounter(_query, CounterPath, IntPtr.Zero, out _counter) != 0)
            {
                PdhCloseQuery(_query);
                _query = IntPtr.Zero;
                _counter = IntPtr.Zero;
                return false;
            }

            // Prime the counter so the first poll can return a value.
            PdhCollectQueryData(_query);
            return true;
        }

        public bool TryRead(out double percent)
        {
            percent = 0;
            if (_disposed)
            {
                return false;
            }

            if (_query == IntPtr.Zero || _counter == IntPtr.Zero)
            {
                if (DateTime.UtcNow - _lastInitAttemptUtc < ReinitRetryInterval || !TryInitialize())
                {
                    return false;
                }
            }

            if (PdhCollectQueryData(_query) != 0 ||
                PdhGetFormattedCounterValue(_counter, PdhFmtDouble, out _, out var value) != 0)
            {
                return false;
            }

            if (value.Status > 1 ||
                double.IsNaN(value.DoubleValue) ||
                double.IsInfinity(value.DoubleValue) ||
                value.DoubleValue < 0)
            {
                return false;
            }

            percent = value.DoubleValue;
            return true;
        }

        public void Dispose()
        {
            _disposed = true;
            if (_query != IntPtr.Zero)
            {
                PdhCloseQuery(_query);
                _query = IntPtr.Zero;
                _counter = IntPtr.Zero;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PdhFmtCounterValue
        {
            public uint Status;
            public double DoubleValue;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint PdhAddEnglishCounter(
            IntPtr query,
            string fullCounterPath,
            IntPtr userData,
            out IntPtr counter);

        [DllImport("pdh.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint PdhGetFormattedCounterValue(
            IntPtr counter,
            uint format,
            out uint type,
            out PdhFmtCounterValue value);

        [DllImport("pdh.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint PdhCloseQuery(IntPtr query);
    }
}
