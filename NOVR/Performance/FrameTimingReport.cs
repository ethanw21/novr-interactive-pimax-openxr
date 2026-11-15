using System.Globalization;

namespace NOVR.Performance;

// The readout's wording, kept apart from Unity so it can be checked.
internal static class FrameTimingReport
{
    private static readonly CultureInfo Plain = CultureInfo.InvariantCulture;

    internal const string Collecting = "Collecting frames…";

    internal static string Page(FrameTimingSummary s, string memorySource)
    {
        if (s.Frames == 0)
            return Collecting;
        var text = string.Format(Plain, "{0:0.0} FPS average · {1:0.0} FPS in the slowest 1%\n", s.AverageFps, s.LowFps) +
            string.Format(Plain, "Frame {0:0.0} ms · worst {1:0.0} ms", s.AverageMs, s.WorstMs) +
            (s.HasBudget
                ? string.Format(Plain, " · headset allows {0:0.0} ms\nLate frames: {1} of {2} ({3:0.0}%)\n",
                    s.BudgetMs, s.Missed, s.Frames, s.Missed * 100f / s.Frames)
                : "\nHeadset refresh rate not reported\n");
        text += s.TimedFrames == 0
            ? "CPU and GPU times are not reported by this setup\n"
            : string.Format(Plain, "CPU main thread {0:0.0} ms ({1:0.0} ms waiting) · render thread {2:0.0} ms\n",
                  s.MainMs, s.WaitMs, s.RenderMs) +
              (s.GpuMs > 0 ? string.Format(Plain, "GPU {0:0.0} ms", s.GpuMs) : "GPU time not reported") +
              LimiterText(s.Limiter) + "\n";
        return text + string.Format(Plain, "Memory churn {0} per frame · {1} collection{2} ({3})",
            Bytes(s.GcBytesPerFrame), s.Collections, s.Collections == 1 ? "" : "s", memorySource);
    }

    internal static string LogLine(FrameTimingSummary s, string memorySource) =>
        string.Format(Plain,
            "[NOVR] Frame timing: frames={0} avgFps={1:0.0} lowFps={2:0.0} avgMs={3:0.00} worstMs={4:0.0} " +
            "budgetMs={5:0.00} late={6} cpuMs={7:0.00} mainMs={8:0.00} waitMs={9:0.00} renderMs={10:0.00} gpuMs={11:0.00} " +
            "limit={12} gcBytesPerFrame={13:0} gcFrames={14} collections={15} gcSource={16}",
            s.Frames, s.AverageFps, s.LowFps, s.AverageMs, s.WorstMs, s.BudgetMs, s.HasBudget ? s.Missed : -1, s.CpuMs,
            s.MainMs, s.WaitMs, s.RenderMs, s.GpuMs, s.Limiter, s.GcBytesPerFrame, s.GcFrames, s.Collections, memorySource.Replace(' ', '-'));

    private static string LimiterText(FrameLimiter limiter) => limiter switch
    {
        FrameLimiter.OnTarget => " · keeping up with the headset",
        FrameLimiter.Cpu => " · looks limited by the CPU",
        FrameLimiter.Gpu => " · looks limited by the GPU",
        FrameLimiter.Waiting => " · waiting on a frame limit or the headset",
        FrameLimiter.Mixed => " · no single limit stands out",
        _ => ""
    };

    internal static string Bytes(double bytes) => bytes >= 1048576
        ? string.Format(Plain, "{0:0.0} MB", bytes / 1048576)
        : bytes >= 1024 ? string.Format(Plain, "{0:0.0} KB", bytes / 1024) : string.Format(Plain, "{0:0} B", bytes);
}
