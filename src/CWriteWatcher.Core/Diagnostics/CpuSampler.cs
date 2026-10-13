using System.Diagnostics;

namespace CWriteWatcher.Core.Diagnostics;

/// <summary>
/// CPU / 内存采样器。
/// </summary>
/// <remarks>
/// 口径（v0.2 §6.2）：<b>占整机 CPU 百分比</b> = 进程单核占用 ÷ 逻辑核心数。
/// 与任务管理器一致，避免验收时口径扯皮。配置项与验收标准均使用此口径。
/// </remarks>
public sealed class CpuSampler
{
    private readonly Process _self = Process.GetCurrentProcess();
    private readonly int _cores = Math.Max(1, Environment.ProcessorCount);

    private TimeSpan _lastCpu;
    private long _lastTimestamp;
    private bool _primed;

    public int LogicalCores => _cores;

    /// <summary>采样一次，返回占整机 CPU 百分比（0–100）。首次调用返回 0。</summary>
    public double SamplePercentOfMachine()
    {
        try
        {
            _self.Refresh();
            var cpu = _self.TotalProcessorTime;
            var now = Stopwatch.GetTimestamp();

            if (!_primed)
            {
                _lastCpu = cpu;
                _lastTimestamp = now;
                _primed = true;
                return 0;
            }

            var cpuDeltaMs = (cpu - _lastCpu).TotalMilliseconds;
            var wallDeltaMs = (now - _lastTimestamp) * 1000.0 / Stopwatch.Frequency;

            _lastCpu = cpu;
            _lastTimestamp = now;

            if (wallDeltaMs <= 0) return 0;

            var percentOfOneCore = cpuDeltaMs / wallDeltaMs * 100.0;
            return Math.Clamp(percentOfOneCore / _cores, 0, 100);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>当前工作集（MB）。</summary>
    public double WorkingSetMb
    {
        get
        {
            try
            {
                _self.Refresh();
                return _self.WorkingSet64 / (1024.0 * 1024.0);
            }
            catch
            {
                return 0;
            }
        }
    }

    /// <summary>私有工作集（MB），更能反映真实占用。</summary>
    public double PrivateMemoryMb
    {
        get
        {
            try
            {
                _self.Refresh();
                return _self.PrivateMemorySize64 / (1024.0 * 1024.0);
            }
            catch
            {
                return 0;
            }
        }
    }
}
