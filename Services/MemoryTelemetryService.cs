using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CryoTray.Models;

namespace CryoTray.Services
{
    /// <summary>Reads physical, usable, and kernel page-list RAM telemetry via Win32 and ntdll APIs.</summary>
    public static class MemoryTelemetryService
    {
        private static readonly ulong PageSize = (ulong)Math.Max(4096, Environment.SystemPageSize);
        private static readonly Lazy<ulong> CachedInstalledBytes = new(ReadInstalledPhysicalBytes);
        private static readonly int[] CandidateMemoryListSizes = BuildCandidateSizes();
        private static int _pageListPrivilegeAttempted;
        private static volatile int _detectedMemoryListSize;

        private static int[] BuildCandidateSizes()
        {
            int ptr = IntPtr.Size;
            return new[]
            {
                22 * ptr,
                21 * ptr,
                23 * ptr,
                24 * ptr,
                25 * ptr,
                26 * ptr,
                27 * ptr,
                28 * ptr
            };
        }

        private static ulong ReadInstalledPhysicalBytes()
        {
            try
            {
                if (NativeMethods.GetPhysicallyInstalledSystemMemory(out ulong kb) && kb > 0)
                {
                    return kb * 1024UL;
                }
            }
            catch
            {
            }

            return 0UL;
        }

        /// <summary>Reads physical installed, usable, and NT memory-list breakdown without WMI.</summary>
        public static bool TryReadBreakdown(out MemoryBreakdown breakdown)
        {
            breakdown = new MemoryBreakdown();

            var status = new NativeMethods.MEMORYSTATUSEX
            {
                dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>()
            };

            if (!NativeMethods.GlobalMemoryStatusEx(ref status) || status.ullTotalPhys == 0)
            {
                return false;
            }

            ulong totalUsable = status.ullTotalPhys;
            ulong available = Math.Min(status.ullAvailPhys, totalUsable);
            ulong inUse = totalUsable - available;
            ulong installed = Math.Max(CachedInstalledBytes.Value, totalUsable);
            ulong hwReserved = installed > totalUsable ? installed - totalUsable : 0UL;

            breakdown.InstalledBytes = installed;
            breakdown.TotalUsableBytes = totalUsable;
            breakdown.AvailableBytes = available;
            breakdown.InUseBytes = inUse;
            breakdown.HardwareReservedBytes = hwReserved;
            breakdown.MemoryLoadPercent = status.dwMemoryLoad;

            if (TryQuerySystemMemoryList(out ulong freePages, out ulong modifiedPages, out ulong standbyPages))
            {
                breakdown.FreeBytes = freePages * PageSize;
                breakdown.ModifiedBytes = modifiedPages * PageSize;
                breakdown.StandbyBytes = standbyPages * PageSize;
                breakdown.HasPageListBreakdown = true;
            }

            return true;
        }

        private static bool TryQuerySystemMemoryList(
            out ulong freePages,
            out ulong modifiedPages,
            out ulong standbyPages)
        {
            freePages = 0;
            modifiedPages = 0;
            standbyPages = 0;

            int minRequiredBytes = 13 * IntPtr.Size;
            int preferred = _detectedMemoryListSize;
            if (preferred >= minRequiredBytes &&
                TryQueryMemoryListWithSize(preferred, out freePages, out modifiedPages, out standbyPages, out _))
            {
                return true;
            }

            int lastReturnLength = 0;
            foreach (int candidateSize in CandidateMemoryListSizes)
            {
                if (candidateSize == preferred)
                {
                    continue;
                }

                if (TryQueryMemoryListWithSize(
                    candidateSize,
                    out freePages,
                    out modifiedPages,
                    out standbyPages,
                    out int returnLength))
                {
                    _detectedMemoryListSize = candidateSize;
                    return true;
                }

                if (returnLength >= minRequiredBytes && returnLength <= 4096)
                {
                    lastReturnLength = returnLength;
                }
            }

            if (lastReturnLength >= minRequiredBytes &&
                Array.IndexOf(CandidateMemoryListSizes, lastReturnLength) < 0 &&
                TryQueryMemoryListWithSize(lastReturnLength, out freePages, out modifiedPages, out standbyPages, out _))
            {
                _detectedMemoryListSize = lastReturnLength;
                return true;
            }

            return false;
        }

        private static bool TryQueryMemoryListWithSize(
            int bufferSize,
            out ulong freePages,
            out ulong modifiedPages,
            out ulong standbyPages,
            out int returnLength)
        {
            freePages = 0;
            modifiedPages = 0;
            standbyPages = 0;
            returnLength = 0;

            IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                int status = NativeMethods.NtQuerySystemInformation(
                    NativeMethods.SystemMemoryListInformation,
                    buffer,
                    bufferSize,
                    out returnLength);

                if ((status == NativeMethods.STATUS_PRIVILEGE_NOT_HELD || status == NativeMethods.STATUS_ACCESS_DENIED) &&
                    Interlocked.Exchange(ref _pageListPrivilegeAttempted, 1) == 0)
                {
                    _ = NativeMethods.RtlAdjustPrivilege(
                        NativeMethods.SeProfileSingleProcessPrivilege,
                        true,
                        false,
                        out _);

                    status = NativeMethods.NtQuerySystemInformation(
                        NativeMethods.SystemMemoryListInformation,
                        buffer,
                        bufferSize,
                        out returnLength);
                }

                if (status != 0)
                {
                    return false;
                }

                byte[] managed = new byte[bufferSize];
                Marshal.Copy(buffer, managed, 0, bufferSize);
                return ParseSystemMemoryListBuffer(managed, IntPtr.Size, out freePages, out modifiedPages, out standbyPages);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        internal static bool ParseSystemMemoryListBuffer(
            ReadOnlySpan<byte> buffer,
            int pointerSize,
            out ulong freePages,
            out ulong modifiedPages,
            out ulong standbyPages)
        {
            freePages = 0;
            modifiedPages = 0;
            standbyPages = 0;

            if ((pointerSize != 4 && pointerSize != 8) || buffer.Length < 13 * pointerSize)
            {
                return false;
            }

            ulong zero = ReadPointerField(buffer, 0, pointerSize);
            ulong free = ReadPointerField(buffer, 1, pointerSize);
            ulong modified = ReadPointerField(buffer, 2, pointerSize);
            ulong modifiedNoWrite = ReadPointerField(buffer, 3, pointerSize);

            ulong standby = 0;
            for (int i = 5; i <= 12; i++)
            {
                standby += ReadPointerField(buffer, i, pointerSize);
            }

            freePages = zero + free;
            modifiedPages = modified + modifiedNoWrite;
            standbyPages = standby;
            return true;
        }

        private static ulong ReadPointerField(ReadOnlySpan<byte> buffer, int index, int pointerSize)
        {
            ReadOnlySpan<byte> slice = buffer.Slice(index * pointerSize, pointerSize);
            return pointerSize == 8
                ? BitConverter.ToUInt64(slice)
                : BitConverter.ToUInt32(slice);
        }

        /// <summary>Formats byte counts as GB (>= 1024 MB, 2 decimals) or MB (&lt; 1024 MB).</summary>
        public static string FormatBytes(ulong bytes)
        {
            const ulong OneGiB = 1024UL * 1024UL * 1024UL;
            const double OneMiB = 1024.0 * 1024.0;

            if (bytes >= OneGiB)
            {
                double gb = bytes / (double)OneGiB;
                return $"{gb.ToString("0.00", CultureInfo.InvariantCulture)} GB";
            }

            double mb = bytes / OneMiB;
            string format = mb > 0.0 && mb < 10.0 ? "0.00" : "0.0";
            return $"{mb.ToString(format, CultureInfo.InvariantCulture)} MB";
        }

        /// <summary>Computes overall CPU utilization % from two GetSystemTimes snapshots (kernel includes idle).</summary>
        internal static double ComputeCpuUsagePercent(
            ulong idle1, ulong kernel1, ulong user1,
            ulong idle2, ulong kernel2, ulong user2)
        {
            ulong idleDelta = idle2 >= idle1 ? idle2 - idle1 : 0UL;
            ulong kernelDelta = kernel2 >= kernel1 ? kernel2 - kernel1 : 0UL;
            ulong userDelta = user2 >= user1 ? user2 - user1 : 0UL;
            ulong totalDelta = kernelDelta + userDelta;
            if (totalDelta == 0)
            {
                return 0.0;
            }

            ulong busyDelta = totalDelta > idleDelta ? totalDelta - idleDelta : 0UL;
            return Math.Clamp((busyDelta / (double)totalDelta) * 100.0, 0.0, 100.0);
        }

        /// <summary>Measures system-wide CPU utilization over the given window using GetSystemTimes.</summary>
        public static async Task<double?> MeasureCpuUsageAsync(TimeSpan sampleWindow, CancellationToken cancellationToken = default)
        {
            if (!NativeMethods.GetSystemTimes(out ulong idle1, out ulong kernel1, out ulong user1))
            {
                return null;
            }

            await Task.Delay(sampleWindow, cancellationToken).ConfigureAwait(false);

            if (!NativeMethods.GetSystemTimes(out ulong idle2, out ulong kernel2, out ulong user2))
            {
                return null;
            }

            return ComputeCpuUsagePercent(idle1, kernel1, user1, idle2, kernel2, user2);
        }
    }
}
