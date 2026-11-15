using System;
using System.Runtime.CompilerServices;
using NOVR.VrUi;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.Scripting;
using UnityEngine.XR;

namespace NOVR.Performance;

// Feeds FrameTimingStats from the engine's own frame timings while the Frame
// timing page is open or logging is on, and does nothing otherwise.
internal sealed class FrameTimingRuntime : MonoBehaviour
{
    private const float PageSeconds = .5f, LogSeconds = 10f;

    internal static string Status { get; private set; } = FrameTimingReport.Collecting;

    private readonly FrameTimingStats _stats = new();
    private readonly FrameTiming[] _timings = new FrameTiming[1];
    private ProfilerRecorder _gcRecorder;
    private bool _active, _failed, _logging, _recorderCounts, _threadCounts, _threadUnsupported;
    private long _lastThreadBytes, _lastHeapBytes;
    private int _lastCollections, _framesSinceLog, _shadowsDrawn, _shadowsReused;
    private float _nextPage, _nextLog;

    private void Update()
    {
        var logging = ModConfiguration.Current?.Performance.LogFrameTiming.Value == true;
        var page = WristMenu.IsFrameTimingPage;
        if (_failed || !XRSettings.enabled || (!logging && !page))
        {
            if (_active)
                Stop();
            return;
        }
        try
        {
            if (!_active)
            {
                Begin();
                return;
            }
            Sample();
        }
        catch (Exception error)
        {
            // A readout must never cost the game its frame loop.
            _failed = true;
            Stop();
            Status = "Frame timing is not available on this setup.";
            Debug.LogWarning("[NOVR] Frame timing stopped: " + error);
            return;
        }
        var now = Time.unscaledTime;
        if (logging != _logging)
        {
            _logging = logging;
            _framesSinceLog = 0;
            _nextLog = now + LogSeconds;
            _shadowsDrawn = SharedShadows.Drawn;
            _shadowsReused = SharedShadows.Reused;
            if (logging)
                LogSetup();
        }
        else if (logging && now >= _nextLog)
        {
            _nextLog = now + LogSeconds;
            Debug.Log(FrameTimingReport.LogLine(_stats.Summarize(BudgetMs(), _framesSinceLog), MemorySource) +
                $" shadowMapsDrawn={SharedShadows.Drawn - _shadowsDrawn} shadowMapsReused={SharedShadows.Reused - _shadowsReused}");
            _shadowsDrawn = SharedShadows.Drawn;
            _shadowsReused = SharedShadows.Reused;
            _framesSinceLog = 0;
        }
        if (page && now >= _nextPage)
        {
            _nextPage = now + PageSeconds;
            Status = FrameTimingReport.Page(_stats.Summarize(BudgetMs()), MemorySource) + "\n" + FramePacingGuard.Status +
                "\n" + SharedShadows.Describe();
        }
    }

    private void OnDisable()
    {
        if (_active)
            Stop();
    }

    private void Begin()
    {
        _active = true;
        _logging = false;
        _stats.Clear();
        _recorderCounts = _threadCounts = false;
        _nextPage = 0;
        Status = FrameTimingReport.Collecting;
        try
        {
            _gcRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
        }
        catch (Exception)
        {
            _gcRecorder = default;
        }
        _lastThreadBytes = ThreadBytes();
        _lastHeapBytes = GC.GetTotalMemory(false);
        _lastCollections = GC.CollectionCount(0);
    }

    private void Stop()
    {
        _active = _logging = false;
        if (_gcRecorder.Valid)
            _gcRecorder.Dispose();
        _gcRecorder = default;
        _stats.Clear();
        Status = FrameTimingReport.Collecting;
    }

    private void Sample()
    {
        float cpu = 0, main = 0, render = 0, gpu = 0, wait = 0;
        FrameTimingManager.CaptureFrameTimings();
        if (FrameTimingManager.GetLatestTimings(1, _timings) > 0)
        {
            cpu = (float)_timings[0].cpuFrameTime;
            main = (float)_timings[0].cpuMainThreadFrameTime;
            render = (float)_timings[0].cpuRenderThreadFrameTime;
            gpu = (float)_timings[0].gpuFrameTime;
            wait = (float)_timings[0].cpuMainThreadPresentWaitTime;
        }
        var collections = GC.CollectionCount(0);
        _stats.Add(Time.unscaledDeltaTime * 1000f, cpu, main, render, gpu, wait, Allocated(), collections - _lastCollections);
        _lastCollections = collections;
        if (_framesSinceLog < FrameTimingStats.Capacity)
            _framesSinceLog++;
    }

    // Three ways to see managed allocation, best first. Release players do
    // not always expose the first two, so each is used only once it has been
    // seen to count, and the readout names the one in use.
    private long Allocated()
    {
        var recorder = _gcRecorder.Valid ? _gcRecorder.LastValue : 0;
        if (recorder > 0)
            _recorderCounts = true;
        var thread = ThreadBytes();
        var threadDelta = thread - _lastThreadBytes;
        _lastThreadBytes = thread;
        if (threadDelta > 0)
            _threadCounts = true;
        var heap = GC.GetTotalMemory(false);
        var heapDelta = heap - _lastHeapBytes;
        _lastHeapBytes = heap;
        return _recorderCounts ? recorder : Math.Max(0, _threadCounts ? threadDelta : heapDelta);
    }

    private long ThreadBytes()
    {
        if (_threadUnsupported)
            return 0;
        try
        {
            return ReadThreadBytes();
        }
        catch (Exception)
        {
            _threadUnsupported = true;
            return 0;
        }
    }

    // Kept apart so a runtime without this counter fails inside the guard
    // above, not while compiling the caller.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long ReadThreadBytes() => GC.GetAllocatedBytesForCurrentThread();

    private string MemorySource => _recorderCounts ? "profiler counter"
        : _threadCounts ? "main thread" : "heap growth, approximate";

    private static float BudgetMs()
    {
        var rate = XRDevice.refreshRate;
        return rate > 1 ? 1000f / rate : 0;
    }

    // One line describing what the numbers that follow were measured under.
    private static void LogSetup()
    {
        try
        {
            var urp = UniversalRenderPipeline.asset;
            var scene = urp == null ? "urp=none" :
                $"msaa={urp.msaaSampleCount} urpRenderScale={urp.renderScale:0.##} shadows={urp.supportsMainLightShadows} " +
                $"shadowDistance={urp.shadowDistance:0} cascades={urp.shadowCascadeCount} shadowMap={urp.mainLightShadowmapResolution}";
            Debug.Log($"[NOVR] Frame timing started: refreshHz={XRDevice.refreshRate:0.#} " +
                $"eyeTexture={XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight} " +
                $"eyeScale={XRSettings.eyeTextureResolutionScale:0.##} stereo={XRSettings.stereoRenderingMode} {scene} " +
                $"targetFrameRate={Application.targetFrameRate} vSyncCount={QualitySettings.vSyncCount} " +
                $"quality={QualitySettings.names[QualitySettings.GetQualityLevel()]} cores={SystemInfo.processorCount} " +
                $"jobWorkers={JobsUtility.JobWorkerCount} incrementalGc={GarbageCollector.isIncremental} " +
                $"frameTimings={FrameTimingManager.IsFeatureEnabled()}.");
        }
        catch (Exception e)
        {
            Debug.LogWarning("[NOVR] Frame timing setup line unavailable: " + e.GetType().Name);
        }
    }
}
