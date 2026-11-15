using System;

namespace NOVR.Performance;

internal enum FrameLimiter
{
    Unknown,
    OnTarget,
    Cpu,
    Gpu,
    Waiting,
    Mixed
}

internal readonly struct FrameTimingSummary
{
    internal readonly int Frames, Missed, TimedFrames, GcFrames, Collections;
    internal readonly float AverageMs, SlowestPercentMs, WorstMs, BudgetMs;
    internal readonly float CpuMs, MainMs, RenderMs, GpuMs, WaitMs;
    internal readonly double GcBytesPerFrame;

    internal FrameTimingSummary(int frames, int missed, int timedFrames, int gcFrames, float averageMs,
        float slowestPercentMs, float worstMs, float budgetMs, float cpuMs, float mainMs, float renderMs,
        float gpuMs, float waitMs, double gcBytesPerFrame, int collections)
    {
        Frames = frames;
        Missed = missed;
        TimedFrames = timedFrames;
        GcFrames = gcFrames;
        AverageMs = averageMs;
        SlowestPercentMs = slowestPercentMs;
        WorstMs = worstMs;
        BudgetMs = budgetMs;
        CpuMs = cpuMs;
        MainMs = mainMs;
        RenderMs = renderMs;
        GpuMs = gpuMs;
        WaitMs = waitMs;
        GcBytesPerFrame = gcBytesPerFrame;
        Collections = collections;
    }

    internal float AverageFps => AverageMs > 0 ? 1000f / AverageMs : 0;
    // The usual "1% low": the frame rate of the slowest hundredth of frames.
    internal float LowFps => SlowestPercentMs > 0 ? 1000f / SlowestPercentMs : 0;
    internal bool HasBudget => BudgetMs > 0;

    // The rule Unity's own frame statistics use: a part is the limit when it
    // fills most of the frame and the others do not. It is a hint for reading
    // the numbers, never a measurement.
    internal FrameLimiter Limiter
    {
        get
        {
            if (TimedFrames == 0 || GpuMs <= 0 || MainMs <= 0)
                return FrameLimiter.Unknown;
            if (HasBudget && AverageMs <= BudgetMs * 1.05f && Missed * 100 <= Frames)
                return FrameLimiter.OnTarget;
            var full = (CpuMs > 0 ? CpuMs : AverageMs) * .8f;
            var cpu = MainMs > full || RenderMs > full;
            var gpu = GpuMs > full;
            if (gpu && !cpu)
                return FrameLimiter.Gpu;
            if (cpu && !gpu)
                return FrameLimiter.Cpu;
            if (!cpu && !gpu && WaitMs > .5f)
                return FrameLimiter.Waiting;
            return FrameLimiter.Mixed;
        }
    }
}

// A fixed window of recent frames. Adding a frame and summarising the window
// allocate nothing, so the readout does not disturb what it measures.
internal sealed class FrameTimingStats
{
    internal const int Capacity = 2048;
    // A frame that took at least half a refresh interval too long was shown late.
    internal const float MissedShare = 1.5f;

    private readonly float[] _frame = new float[Capacity], _cpu = new float[Capacity], _main = new float[Capacity],
        _render = new float[Capacity], _gpu = new float[Capacity], _wait = new float[Capacity], _sorted = new float[Capacity];
    private readonly long[] _gc = new long[Capacity];
    private readonly int[] _collections = new int[Capacity];
    private int _next;

    internal int Count
    {
        get; private set;
    }

    internal void Clear() => Count = _next = 0;

    // Times are milliseconds. Pass zero for a time the platform did not report.
    internal void Add(float frameMs, float cpuMs, float mainMs, float renderMs, float gpuMs, float waitMs, long gcBytes,
        int collections = 0)
    {
        if (!(frameMs > 0) || float.IsInfinity(frameMs))
            return;
        _frame[_next] = frameMs;
        _cpu[_next] = Clean(cpuMs);
        _main[_next] = Clean(mainMs);
        _render[_next] = Clean(renderMs);
        _gpu[_next] = Clean(gpuMs);
        _wait[_next] = Clean(waitMs);
        _gc[_next] = Math.Max(0, gcBytes);
        _collections[_next] = Math.Max(0, collections);
        _next = (_next + 1) % Capacity;
        if (Count < Capacity)
            Count++;
    }

    private static float Clean(float value) => value > 0 && !float.IsInfinity(value) ? value : 0;

    // Summarises the most recent frames, at most `frames` of them.
    internal FrameTimingSummary Summarize(float budgetMs, int frames = Capacity)
    {
        var count = Math.Min(Count, Math.Max(0, frames));
        if (count == 0)
            return default;
        if (!(budgetMs > 0) || float.IsInfinity(budgetMs))
            budgetMs = 0;
        double total = 0, cpu = 0, main = 0, render = 0, gpu = 0, wait = 0, gc = 0;
        float worst = 0;
        int missed = 0, timed = 0, gpuTimed = 0, gcFrames = 0, collections = 0;
        for (var i = 0; i < count; i++)
        {
            var index = (_next - 1 - i + Capacity * 2) % Capacity;
            var frame = _frame[index];
            _sorted[i] = frame;
            total += frame;
            worst = Math.Max(worst, frame);
            if (budgetMs > 0 && frame > budgetMs * MissedShare)
                missed++;
            if (_main[index] > 0)
            {
                timed++;
                cpu += _cpu[index];
                main += _main[index];
                render += _render[index];
                wait += _wait[index];
            }
            if (_gpu[index] > 0)
            {
                gpuTimed++;
                gpu += _gpu[index];
            }
            gc += _gc[index];
            collections += _collections[index];
            if (_gc[index] > 0)
                gcFrames++;
        }
        Array.Sort(_sorted, 0, count);
        var slowest = Math.Max(1, count / 100);
        double slow = 0;
        for (var i = 0; i < slowest; i++)
            slow += _sorted[count - 1 - i];
        return new FrameTimingSummary(count, missed, timed, gcFrames, (float)(total / count), (float)(slow / slowest),
            worst, budgetMs, Average(cpu, timed), Average(main, timed), Average(render, timed), Average(gpu, gpuTimed),
            Average(wait, timed), gc / count, collections);
    }

    private static float Average(double total, int count) => count > 0 ? (float)(total / count) : 0;
}
