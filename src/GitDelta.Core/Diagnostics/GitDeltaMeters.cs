using System.Diagnostics.Metrics;

namespace GitDelta.Core.Diagnostics;

public static class GitDeltaMeters
{
    public const string MeterName = "GitDelta";

    public static readonly Meter Meter = new(MeterName, "1.0.0");

    public static readonly Histogram<double> RepositoryOpenMs =
        Meter.CreateHistogram<double>("repository.open.duration_ms", "ms", "Repository open duration");

    public static readonly Histogram<double> StatusRefreshMs =
        Meter.CreateHistogram<double>("status.refresh.duration_ms", "ms", "Status refresh duration");

    public static readonly Histogram<double> DiffGenerationMs =
        Meter.CreateHistogram<double>("diff.generation.duration_ms", "ms", "Diff generation duration");

    public static readonly Histogram<double> DiffPresentMs =
        Meter.CreateHistogram<double>("diff.present.duration_ms", "ms", "Diff present (enrich + project + bind) duration");

    public static readonly Histogram<double> DiffProjectMs =
        Meter.CreateHistogram<double>("diff.project.duration_ms", "ms", "Diff row projection duration");

    public static readonly Histogram<double> DiffRenderMs =
        Meter.CreateHistogram<double>("diff.render.duration_ms", "ms", "DiffViewer paint duration");

    public static readonly Histogram<double> StageMs =
        Meter.CreateHistogram<double>("stage.duration_ms", "ms", "Stage operation duration");

    public static readonly Histogram<double> CommitMs =
        Meter.CreateHistogram<double>("commit.duration_ms", "ms", "Commit duration");

    public static readonly Histogram<double> PushMs =
        Meter.CreateHistogram<double>("push.duration_ms", "ms", "Push duration");

    public static readonly Histogram<double> PullMs =
        Meter.CreateHistogram<double>("pull.duration_ms", "ms", "Pull duration");

    public static readonly Histogram<double> WcRefreshMs =
        Meter.CreateHistogram<double>("wc.refresh.duration_ms", "ms", "Working-copy refresh end-to-end duration");

    public static readonly Histogram<double> WcFileListsRebuildMs =
        Meter.CreateHistogram<double>("wc.filelists.rebuild.duration_ms", "ms", "File list rebuild (filter + entries) duration");

    public static readonly Histogram<double> WcFileListsLayoutMs =
        Meter.CreateHistogram<double>("wc.filelists.layout.duration_ms", "ms", "File list UI layout after rebuild duration");

    public static readonly Histogram<double> WcCacheIndicatorsMs =
        Meter.CreateHistogram<double>("wc.cache.indicators.duration_ms", "ms", "UpdateFileCacheIndicators duration");

    public static readonly Histogram<double> WcPrefetchMs =
        Meter.CreateHistogram<double>("wc.prefetch.duration_ms", "ms", "Working-copy diff prefetch enqueue duration");

    public static readonly Histogram<double> WcPrefetchEnqueueMs =
        Meter.CreateHistogram<double>("wc.prefetch.enqueue.duration_ms", "ms", "Working-copy diff prefetch enqueue loop duration");

    public static readonly Histogram<double> WcStageMs =
        Meter.CreateHistogram<double>("wc.stage.duration_ms", "ms", "Stage/unstage end-to-end (optimistic + git + refresh)");

    public static readonly Histogram<double> WcStageOptimisticMs =
        Meter.CreateHistogram<double>("wc.stage.optimistic.duration_ms", "ms", "Optimistic file-list rebuild during stage/unstage");

    public static readonly Histogram<double> WcStageInvalidateMs =
        Meter.CreateHistogram<double>("wc.stage.invalidate.duration_ms", "ms", "Warm-store soft-invalidate during stage/unstage");

    public static readonly Histogram<double> WcFileListsFilterMs =
        Meter.CreateHistogram<double>("wc.filelists.filter.duration_ms", "ms", "ApplyFileFilter + entry rebuild duration");

    public static readonly Histogram<double> WcBranchesLoadMs =
        Meter.CreateHistogram<double>("wc.branches.load.duration_ms", "ms", "Branch list load duration");

    public static readonly Histogram<double> WcStashesLoadMs =
        Meter.CreateHistogram<double>("wc.stashes.load.duration_ms", "ms", "Stash list load duration");

    public static readonly Histogram<double> UiFileListResizeMs =
        Meter.CreateHistogram<double>("ui.filelist.resize.duration_ms", "ms", "File-list column resize layout duration");

    public static readonly Counter<long> GitInvocations =
        Meter.CreateCounter<long>("git.invocations", description: "git process invocations");

    public static readonly Counter<long> GitBytesRead =
        Meter.CreateCounter<long>("git.bytes_read", "bytes", "Bytes read from git");

    public static readonly Counter<long> CacheHits =
        Meter.CreateCounter<long>("cache.hits", description: "Content-addressed cache hits");

    public static readonly Counter<long> CacheMisses =
        Meter.CreateCounter<long>("cache.misses", description: "Content-addressed cache misses");

    public static readonly Counter<long> LinesTokenised =
        Meter.CreateCounter<long>("syntax.lines_tokenised", description: "Lines tokenised for syntax highlighting");
}

/// <summary>Test helper that listens to GitDelta meters for work assertions.</summary>
public sealed class WorkAssertionListener : IDisposable
{
    private readonly MeterListener _listener;
    private long _gitInvocations;
    private long _bytesRead;
    private long _cacheHits;
    private long _cacheMisses;
    private long _linesTokenised;
    private int _uiSyncContextTouches;

    public WorkAssertionListener()
    {
        _listener = new MeterListener();
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == GitDeltaMeters.MeterName)
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            switch (instrument.Name)
            {
                case "git.invocations": Interlocked.Add(ref _gitInvocations, measurement); break;
                case "git.bytes_read": Interlocked.Add(ref _bytesRead, measurement); break;
                case "cache.hits": Interlocked.Add(ref _cacheHits, measurement); break;
                case "cache.misses": Interlocked.Add(ref _cacheMisses, measurement); break;
                case "syntax.lines_tokenised": Interlocked.Add(ref _linesTokenised, measurement); break;
            }
        });
        _listener.Start();
    }

    public long GitInvocations => Interlocked.Read(ref _gitInvocations);
    public long BytesRead => Interlocked.Read(ref _bytesRead);
    public long CacheHits => Interlocked.Read(ref _cacheHits);
    public long CacheMisses => Interlocked.Read(ref _cacheMisses);
    public long LinesTokenised => Interlocked.Read(ref _linesTokenised);
    public int UiSyncContextTouches => _uiSyncContextTouches;

    public void Reset()
    {
        Interlocked.Exchange(ref _gitInvocations, 0);
        Interlocked.Exchange(ref _bytesRead, 0);
        Interlocked.Exchange(ref _cacheHits, 0);
        Interlocked.Exchange(ref _cacheMisses, 0);
        Interlocked.Exchange(ref _linesTokenised, 0);
        _uiSyncContextTouches = 0;
    }

    public void RecordUiSyncContextTouch() => Interlocked.Increment(ref _uiSyncContextTouches);

    public void Dispose() => _listener.Dispose();
}
