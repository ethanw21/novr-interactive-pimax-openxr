using NOVR.Performance;

var count = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); count++; }
void Near(float a, float b, string message) => Check(Math.Abs(a - b) < .01f, $"{message} ({a} vs {b})");

// Frame timing statistics.
{
    var stats = new FrameTimingStats();
    Check(stats.Summarize(11.1f).Frames == 0 && FrameTimingReport.Page(stats.Summarize(11.1f), "heap growth") == FrameTimingReport.Collecting,
        "An empty window reports that it is still collecting");
    for (var i = 0; i < 99; i++)
        stats.Add(10, 10, 6, 3, 4, 1, 1024);
    stats.Add(30, 30, 26, 3, 4, 1, 0, 2);
    var s = stats.Summarize(1000f / 90);
    Check(s.Frames == 100 && s.TimedFrames == 100, "Every accepted frame is counted");
    Near(s.AverageMs, 10.2f, "Average frame time is the mean interval");
    Near(s.AverageFps, 1000f / 10.2f, "Average frame rate follows the mean interval, not the mean of rates");
    Near(s.LowFps, 1000f / 30, "The 1% low is the rate of the slowest hundredth of frames");
    Near(s.WorstMs, 30, "The worst frame is reported");
    Check(s.Missed == 1, "A frame at least half an interval late is a late frame; one on time is not");
    Near(s.MainMs, 6.2f, "Main thread time is averaged");
    Near(s.GpuMs, 4, "GPU time is averaged");
    Check(Math.Abs(s.GcBytesPerFrame - 1013.76) < .01 && s.GcFrames == 99 && s.Collections == 2,
        "Allocation is averaged per frame and collections are summed");

    Check(stats.Summarize(0).Missed == 0 && !stats.Summarize(0).HasBudget && !stats.Summarize(float.NaN).HasBudget,
        "Without a refresh rate no frame is called late");
    Near(stats.Summarize(11.1f, 1).AverageMs, 30, "A shorter window covers only the most recent frames");
    Near(stats.Summarize(11.1f, 50).AverageMs, (49 * 10 + 30) / 50f, "A window of fifty covers the last fifty frames");

    stats.Add(0, 1, 1, 1, 1, 1, 1);
    stats.Add(float.NaN, 1, 1, 1, 1, 1, 1);
    stats.Add(float.PositiveInfinity, 1, 1, 1, 1, 1, 1);
    stats.Add(-5, 1, 1, 1, 1, 1, 1);
    Check(stats.Count == 100, "Frames without a usable interval are ignored");

    stats.Clear();
    for (var i = 0; i < FrameTimingStats.Capacity + 500; i++)
        stats.Add(i < 500 ? 100 : 5, 0, 0, 0, 0, 0, -50);
    s = stats.Summarize(11.1f);
    Check(s.Frames == FrameTimingStats.Capacity, "The window is bounded");
    Near(s.WorstMs, 5, "Old frames leave the window");
    Check(s.TimedFrames == 0 && s.GcBytesPerFrame == 0 && s.Limiter == FrameLimiter.Unknown,
        "Missing engine timings and negative allocation deltas are not counted as measurements");
    Check(FrameTimingReport.Page(s, "heap growth").Contains("not reported by this setup"),
        "The readout says when the engine reports no CPU and GPU times");
}

// Reading the numbers.
{
    FrameTimingSummary Sample(float frame, float main, float render, float gpu, float wait, float budget = 11.11f)
    {
        var stats = new FrameTimingStats();
        for (var i = 0; i < 200; i++)
            stats.Add(frame, frame, main, render, gpu, wait, 0);
        return stats.Summarize(budget);
    }
    Check(Sample(11.1f, 5, 3, 6, 4).Limiter == FrameLimiter.OnTarget, "Holding the refresh rate is on target whatever the parts say");
    Check(Sample(22.2f, 20, 6, 8, 0).Limiter == FrameLimiter.Cpu, "A main thread filling the frame points at the CPU");
    Check(Sample(22.2f, 8, 19, 8, 0).Limiter == FrameLimiter.Cpu, "A render thread filling the frame points at the CPU");
    Check(Sample(22.2f, 8, 6, 21, 0).Limiter == FrameLimiter.Gpu, "A GPU filling the frame points at the GPU");
    Check(Sample(22.2f, 8, 6, 9, 9).Limiter == FrameLimiter.Waiting, "A frame nothing fills, with time spent waiting, points at a limit");
    Check(Sample(22.2f, 20, 6, 21, 0).Limiter == FrameLimiter.Mixed, "CPU and GPU both full name no single limit");
    Check(Sample(22.2f, 20, 6, 0, 0).Limiter == FrameLimiter.Unknown, "No GPU time means no verdict");
    Check(Sample(22.2f, 20, 6, 8, 0, 0).Limiter == FrameLimiter.Cpu, "A verdict does not need the refresh rate");

    var page = FrameTimingReport.Page(Sample(22.2f, 20, 6, 8, .4f), "main thread");
    Check(page.Split('\n').Length == 6, "The readout fits its six lines");
    Check(page.Contains("45.0 FPS average") && page.Contains("headset allows 11.1 ms") && page.Contains("Late frames: 200 of 200 (100.0%)") &&
          page.Contains("CPU main thread 20.0 ms (0.4 ms waiting) · render thread 6.0 ms") && page.Contains("GPU 8.0 ms · looks limited by the CPU") &&
          page.Contains("0 collections (main thread)"),
        "The readout states each measurement in plain units: " + page);
    var previous = System.Globalization.CultureInfo.CurrentCulture;
    System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
    var line = FrameTimingReport.LogLine(Sample(22.2f, 20, 6, 8, .4f), "heap growth, approximate");
    System.Globalization.CultureInfo.CurrentCulture = previous;
    Check(line.StartsWith("[NOVR] Frame timing: frames=200 avgFps=45.0 lowFps=45.0 avgMs=22.20 ") && line.Contains(" late=200 ") &&
          line.Contains(" mainMs=20.00 ") && line.Contains(" limit=Cpu ") && line.EndsWith("gcSource=heap-growth,-approximate"),
        "The log line is one culture-independent record of key=value fields: " + line);
    Check(FrameTimingReport.LogLine(Sample(22.2f, 20, 6, 8, 0, 0), "x").Contains(" late=-1 "), "The log marks late frames as unknown without a refresh rate");
    Check(FrameTimingReport.Bytes(512) == "512 B" && FrameTimingReport.Bytes(1536) == "1.5 KB" && FrameTimingReport.Bytes(3 * 1048576) == "3.0 MB",
        "Allocation is shown in the nearest unit");
}

// Frame pacing.
{
    var state = default(FramePacingState);
    var write = FramePacingPolicy.Decide(ref state, true, false, 60, 1);
    Check(!write.SetTargetFrameRate && !write.SetVSyncCount && !state.Holding, "Nothing is touched without an active headset");

    write = FramePacingPolicy.Decide(ref state, true, true, 60, 1);
    Check(write is { Started: true, SetTargetFrameRate: true, TargetFrameRate: -1, SetVSyncCount: true, VSyncCount: 0 } &&
          state is { Holding: true, GameTargetFrameRate: 60, GameVSyncCount: 1 },
        "With a headset active the game's limit and vertical sync are held off and remembered");
    Check(FramePacingPolicy.Describe(state, true) == "Frame limit: game asked for 60 FPS and vertical sync; held off in VR",
        "The status names what the game asked for");

    write = FramePacingPolicy.Decide(ref state, true, true, -1, 0);
    Check(!write.Started && !write.SetTargetFrameRate && !write.SetVSyncCount && state.GameTargetFrameRate == 60,
        "Held-off values are not rewritten every frame and do not replace what the game asked for");

    write = FramePacingPolicy.Decide(ref state, true, true, 144, 0);
    Check(write is { SetTargetFrameRate: true, TargetFrameRate: -1, SetVSyncCount: false } && state is { GameTargetFrameRate: 144, GameVSyncCount: 1 },
        "A new limit from the game, such as entering a mission, is remembered and held off again");

    write = FramePacingPolicy.Decide(ref state, true, false, -1, 0);
    Check(write is { Stopped: true, SetTargetFrameRate: true, TargetFrameRate: 144, SetVSyncCount: true, VSyncCount: 1 } && !state.Holding,
        "When the headset stops the game's latest values are handed back");
    Check(FramePacingPolicy.Decide(ref state, true, false, 144, 1) is { Stopped: false, SetTargetFrameRate: false, SetVSyncCount: false },
        "Handing back happens once");

    FramePacingPolicy.Decide(ref state, true, true, 60, 0);
    write = FramePacingPolicy.Decide(ref state, false, true, 30, 0);
    Check(write is { Stopped: true, SetTargetFrameRate: false, SetVSyncCount: false },
        "Turning the guard off never overwrites a value the game set in the meantime");

    state = default;
    FramePacingPolicy.Decide(ref state, true, true, -1, 0);
    Check(FramePacingPolicy.Describe(state, true) == "Frame limit: the game asked for none" &&
          FramePacingPolicy.Decide(ref state, false, true, -1, 0) is { Stopped: true, SetTargetFrameRate: false, SetVSyncCount: false },
        "A game without limits is left exactly as it was");
    Check(FramePacingPolicy.Describe(default, false) == "Frame limit: left to the game", "The status says when the guard is off");
    state = default;
    FramePacingPolicy.Decide(ref state, true, true, 0, 2);
    Check(state.Holding && FramePacingPolicy.Describe(state, true) == "Frame limit: game asked for vertical sync; held off in VR",
        "Vertical sync alone is held off; a zero frame rate is the platform default, not a limit");
}

// Shadow sharing.
{
    const int frame = 100, camera = 7;
    var first = SharedShadowPolicy.Record(true, false, 0, frame, camera, 3, 4096, 4096);
    Check(first.Reusable, "A first-eye draw can be shared");
    bool Reuse(in ShadowDraw drawn, bool enabled = true, bool renderer = true, bool texture = true, bool xr = true,
        bool singlePass = false, int eye = 1, int atFrame = frame, int forCamera = camera) =>
        SharedShadowPolicy.CanReuse(enabled, drawn, renderer, texture, xr, singlePass, eye, atFrame, forCamera);
    Check(Reuse(first), "The second eye of the same camera and frame reuses the first eye's shadow map");
    Check(!Reuse(first, enabled: false), "Off by setting means never reused");
    Check(!Reuse(first, eye: 0) && !Reuse(first, eye: 2), "Only the second eye reuses");
    Check(!Reuse(first, atFrame: frame + 1), "A shadow map from an earlier frame is never reused");
    Check(!Reuse(first, forCamera: 8), "Another camera never reuses this camera's shadow map");
    Check(!Reuse(first, renderer: false) && !Reuse(first, texture: false), "A different renderer or a replaced texture is never reused");
    Check(!Reuse(first, xr: false) && !Reuse(first, singlePass: true), "Only two-pass headset rendering shares");
    Check(!Reuse(default), "Nothing drawn yet means nothing to reuse");

    // Another camera drawing into the map between the eyes takes it over.
    var overlay = SharedShadowPolicy.Record(true, false, 0, frame, 8, 3, 4096, 4096);
    Check(!Reuse(overlay) && Reuse(overlay, forCamera: 8), "A later draw by another camera replaces what the map holds");
    Check(!SharedShadowPolicy.Record(true, false, 1, frame, camera, 3, 4096, 4096).Reusable &&
          !SharedShadowPolicy.Record(false, false, 0, frame, camera, 3, 4096, 4096).Reusable &&
          !SharedShadowPolicy.Record(true, true, 0, frame, camera, 3, 4096, 4096).Reusable,
        "A second-eye, desktop or single-pass draw is never shared onward");

    Check(SharedShadowPolicy.SameShadowMap(first, true, 3, 4096, 4096), "Matching shadow settings allow reuse");
    Check(!SharedShadowPolicy.SameShadowMap(first, false, 3, 4096, 4096), "A sun that casts no shadow for this eye is left to the game");
    Check(!SharedShadowPolicy.SameShadowMap(first, true, 4, 4096, 4096) && !SharedShadowPolicy.SameShadowMap(first, true, 3, 2048, 4096) &&
          !SharedShadowPolicy.SameShadowMap(first, true, 3, 4096, 2048) && !SharedShadowPolicy.SameShadowMap(default, true, 0, 0, 0),
        "Changed cascades or resolution are left to the game");

    Check(SharedShadowPolicy.CanSkipCasterCulling(true, false, 2000), "A reused eye need not find shadow casters");
    Check(!SharedShadowPolicy.CanSkipCasterCulling(false, false, 2000) && !SharedShadowPolicy.CanSkipCasterCulling(true, true, 2000) &&
          !SharedShadowPolicy.CanSkipCasterCulling(true, false, 0),
        "Shadow casters are still found when the eye draws its own map or other lights cast shadows");
}

Console.WriteLine($"{count} performance checks passed.");
