using System;
using System.Collections.Generic;
using System.Linq;
using CryoTray.Models;

namespace CryoTray.Services
{
    public class TelemetryRingBuffer
    {
        private readonly TelemetrySample[] _buffer;
        private int _head = 0;
        private int _count = 0;
        private readonly object _lock = new();

        public int Capacity { get; }

        public TelemetryRingBuffer(int capacity = 3600)
        {
            Capacity = capacity;
            _buffer = new TelemetrySample[capacity];
        }

        public void AddSample(TelemetrySample sample)
        {
            lock (_lock)
            {
                _buffer[_head] = sample;
                _head = (_head + 1) % Capacity;
                if (_count < Capacity)
                {
                    _count++;
                }
            }
        }

        public List<TelemetrySample> GetRecentSamples(int count)
        {
            lock (_lock)
            {
                int take = Math.Min(count, _count);
                var result = new List<TelemetrySample>(take);

                if (take == 0) return result;

                int start = (_head - take + Capacity) % Capacity;
                for (int i = 0; i < take; i++)
                {
                    int index = (start + i) % Capacity;
                    result.Add(_buffer[index]);
                }

                return result;
            }
        }

        public (double MinCpu, double AvgCpu, double MaxCpu) GetCpuStats(bool coolingOnly = false)
        {
            lock (_lock)
            {
                if (_count == 0) return (0, 0, 0);

                double min = double.MaxValue;
                double max = double.MinValue;
                double sum = 0;
                int matched = 0;

                for (int i = 0; i < _count; i++)
                {
                    TelemetrySample sample = _buffer[i];
                    if (coolingOnly && !sample.CoolingModeActive)
                    {
                        continue;
                    }

                    double t = sample.CpuTemp;
                    if (!double.IsFinite(t) || t <= 0)
                    {
                        continue;
                    }

                    if (t < min) min = t;
                    if (t > max) max = t;
                    sum += t;
                    matched++;
                }

                return matched == 0 ? (0, 0, 0) : (min, sum / matched, max);
            }
        }

        public (double MinGpu, double AvgGpu, double MaxGpu) GetGpuStats(
            bool coolingOnly = false,
            string? gpuIdentifier = null)
        {
            lock (_lock)
            {
                if (_count == 0) return (0, 0, 0);

                double min = double.MaxValue;
                double max = double.MinValue;
                double sum = 0;
                int matched = 0;

                for (int i = 0; i < _count; i++)
                {
                    TelemetrySample sample = _buffer[i];
                    if (coolingOnly && !sample.CoolingModeActive)
                    {
                        continue;
                    }

                    if (!sample.TryGetGpuTemp(gpuIdentifier, out double t))
                    {
                        continue;
                    }

                    if (t < min) min = t;
                    if (t > max) max = t;
                    sum += t;
                    matched++;
                }

                return matched == 0 ? (0, 0, 0) : (min, sum / matched, max);
            }
        }
    }
}
