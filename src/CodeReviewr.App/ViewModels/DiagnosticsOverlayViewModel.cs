using System.Collections.ObjectModel;
using System.Diagnostics.Metrics;
using CommunityToolkit.Mvvm.ComponentModel;
using CodeReviewr.Core;
using CodeReviewr.Core.Diagnostics;

namespace CodeReviewr.App.ViewModels;

public partial class DiagnosticsOverlayViewModel : ObservableObject
{
    private readonly MeterListener _listener;

    public DiagnosticsOverlayViewModel()
    {
        _listener = new MeterListener();
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == CodeReviewrMeters.MeterName)
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            switch (instrument.Name)
            {
                case "git.invocations": GitInvocations += measurement; break;
                case "git.bytes_read": BytesRead += measurement; break;
                case "cache.hits": CacheHits += measurement; break;
                case "cache.misses": CacheMisses += measurement; break;
                case "syntax.lines_tokenised": LinesTokenised += measurement; break;
            }
            OnPropertyChanged(nameof(Summary));
        });
        _listener.SetMeasurementEventCallback<double>((instrument, measurement, _, _) =>
        {
            LastTimings.Insert(0, $"{instrument.Name}: {measurement:F1} ms");
            while (LastTimings.Count > 20) LastTimings.RemoveAt(LastTimings.Count - 1);
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

    public string Summary =>
        $"git invocations={GitInvocations}  bytes={BytesRead}  cache {CacheHits}/{CacheHits + CacheMisses}  tokens={LinesTokenised}";

    public void SetGitInfo(GitExecutableInfo? info)
    {
        GitPath = info?.Path;
        GitVersion = info?.Version.ToString();
    }
}
