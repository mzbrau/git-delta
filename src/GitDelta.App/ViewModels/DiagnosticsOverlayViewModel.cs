using System.Collections.ObjectModel;
using System.Diagnostics.Metrics;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using GitDelta.Core;
using GitDelta.Core.Diagnostics;

namespace GitDelta.App.ViewModels;

public partial class DiagnosticsOverlayViewModel : ObservableObject
{
    public const string DiffRenderInstrument = "diff.render.duration_ms";
    public const string DiffScrollGestureInstrument = "diff.scroll.gesture_to_paint_ms";
    private const int MaxListedTimings = 80;

    private readonly MeterListener _listener;
    private readonly object _timingsGate = new();
    private double? _lastScrollGestureMs;
    private double? _lastDiffRenderMs;
    private bool _highFrequencyUiPosted;
    private bool _pendingScrollListLine;

    public DiagnosticsOverlayViewModel()
    {
        _listener = new MeterListener();
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == GitDeltaMeters.MeterName)
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            try
            {
                switch (instrument.Name)
                {
                    case "git.invocations":
                        Interlocked.Add(ref _gitInvocations, measurement);
                        break;
                    case "git.bytes_read":
                        Interlocked.Add(ref _bytesRead, measurement);
                        break;
                    case "cache.hits":
                        Interlocked.Add(ref _cacheHits, measurement);
                        break;
                    case "cache.misses":
                        Interlocked.Add(ref _cacheMisses, measurement);
                        break;
                    case "syntax.lines_tokenised":
                        Interlocked.Add(ref _linesTokenised, measurement);
                        break;
                    default:
                        return;
                }

                PostToUi(() =>
                {
                    OnPropertyChanged(nameof(GitInvocations));
                    OnPropertyChanged(nameof(BytesRead));
                    OnPropertyChanged(nameof(CacheHits));
                    OnPropertyChanged(nameof(CacheMisses));
                    OnPropertyChanged(nameof(LinesTokenised));
                    OnPropertyChanged(nameof(Summary));
                });
            }
            catch
            {
                // Meter callbacks must never throw into Record/Add callers (e.g. diff generation).
            }
        });
        _listener.SetMeasurementEventCallback<double>((instrument, measurement, _, _) =>
        {
            try
            {
                if (IsHighFrequencyTiming(instrument.Name))
                    QueueHighFrequencyTiming(instrument.Name, measurement);
                else
                    PostToUi(() => RecordTiming(instrument.Name, measurement));
            }
            catch
            {
                // Meter callbacks must never throw into Record/Add callers (e.g. diff generation).
            }
        });
        _listener.Start();
    }

    [ObservableProperty] private long _gitInvocations;
    [ObservableProperty] private long _bytesRead;
    [ObservableProperty] private long _cacheHits;
    [ObservableProperty] private long _cacheMisses;
    [ObservableProperty] private long _linesTokenised;
    [ObservableProperty] private string? _gitPath;
    [ObservableProperty] private string? _gitVersion;

    public ObservableCollection<string> LastTimings { get; } = [];

    /// <summary>Latest DiffViewer scroll gesture → first paint latency, if any sample arrived.</summary>
    public double? LastScrollGestureMs => _lastScrollGestureMs;

    /// <summary>Latest DiffViewer paint duration (not listed in <see cref="LastTimings"/> to avoid flooding).</summary>
    public double? LastDiffRenderMs => _lastDiffRenderMs;

    public string Summary
    {
        get
        {
            var text =
                $"git invocations={GitInvocations}  bytes={BytesRead}  cache {CacheHits}/{CacheHits + CacheMisses}  tokens={LinesTokenised}";
            if (_lastScrollGestureMs is { } scrollMs)
                text += $"  last scroll gesture={scrollMs.ToString("F1", CultureInfo.InvariantCulture)} ms";
            if (_lastDiffRenderMs is { } renderMs)
                text += $"  last paint={renderMs.ToString("F1", CultureInfo.InvariantCulture)} ms";
            return text;
        }
    }

    public void SetGitInfo(GitExecutableInfo? info)
    {
        GitPath = info?.Path;
        GitVersion = info?.Version.ToString();
    }

    /// <summary>
    /// Whether a histogram sample should appear in the scrolling timings list.
    /// High-frequency paint samples are excluded so rarer scroll/git timings stay visible.
    /// </summary>
    public static bool ShouldListTiming(string instrumentName) =>
        !string.Equals(instrumentName, DiffRenderInstrument, StringComparison.Ordinal);

    public static bool IsHighFrequencyTiming(string instrumentName) =>
        string.Equals(instrumentName, DiffRenderInstrument, StringComparison.Ordinal)
        || string.Equals(instrumentName, DiffScrollGestureInstrument, StringComparison.Ordinal);

    private void QueueHighFrequencyTiming(string instrumentName, double measurementMs)
    {
        var shouldPost = false;
        lock (_timingsGate)
        {
            if (string.Equals(instrumentName, DiffRenderInstrument, StringComparison.Ordinal))
            {
                _lastDiffRenderMs = measurementMs;
            }
            else if (string.Equals(instrumentName, DiffScrollGestureInstrument, StringComparison.Ordinal))
            {
                _lastScrollGestureMs = measurementMs;
                _pendingScrollListLine = true;
            }

            if (!_highFrequencyUiPosted)
            {
                _highFrequencyUiPosted = true;
                shouldPost = true;
            }
        }

        if (shouldPost)
            PostToUi(FlushHighFrequencyTimings);
    }

    private void FlushHighFrequencyTimings()
    {
        string? scrollLine = null;
        lock (_timingsGate)
        {
            _highFrequencyUiPosted = false;
            if (_pendingScrollListLine && _lastScrollGestureMs is { } scrollMs)
            {
                _pendingScrollListLine = false;
                scrollLine =
                    $"{DiffScrollGestureInstrument}: {scrollMs.ToString("F1", CultureInfo.InvariantCulture)} ms";
                LastTimings.Insert(0, scrollLine);
                while (LastTimings.Count > MaxListedTimings)
                    LastTimings.RemoveAt(LastTimings.Count - 1);
            }

            OnPropertyChanged(nameof(LastScrollGestureMs));
            OnPropertyChanged(nameof(LastDiffRenderMs));
            OnPropertyChanged(nameof(Summary));
        }
    }

    private void RecordTiming(string instrumentName, double measurementMs)
    {
        lock (_timingsGate)
        {
            if (!ShouldListTiming(instrumentName))
                return;

            var line = $"{instrumentName}: {measurementMs.ToString("F1", CultureInfo.InvariantCulture)} ms";
            LastTimings.Insert(0, line);
            while (LastTimings.Count > MaxListedTimings)
                LastTimings.RemoveAt(LastTimings.Count - 1);
        }
    }

    private void PostToUi(Action action)
    {
        try
        {
            var dispatcher = Dispatcher.UIThread;
            if (dispatcher.CheckAccess())
            {
                action();
                return;
            }

            // Headless unit tests have no message pump — apply immediately.
            if (Avalonia.Application.Current is null)
            {
                action();
                return;
            }

            dispatcher.Post(action);
        }
        catch
        {
            // Headless tests / no Avalonia dispatcher: still apply under the timings lock.
            action();
        }
    }
}
