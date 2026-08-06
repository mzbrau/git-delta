using System.Collections.ObjectModel;
using System.Diagnostics.Metrics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using GitDelta.Core;
using GitDelta.Core.Diagnostics;

namespace GitDelta.App.ViewModels;

public partial class DiagnosticsOverlayViewModel : ObservableObject
{
    private readonly MeterListener _listener;
    private readonly object _timingsGate = new();

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
                var line = $"{instrument.Name}: {measurement:F1} ms";
                PostToUi(() => AppendTiming(line));
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

    public string Summary =>
        $"git invocations={GitInvocations}  bytes={BytesRead}  cache {CacheHits}/{CacheHits + CacheMisses}  tokens={LinesTokenised}";

    public void SetGitInfo(GitExecutableInfo? info)
    {
        GitPath = info?.Path;
        GitVersion = info?.Version.ToString();
    }

    private void AppendTiming(string line)
    {
        lock (_timingsGate)
        {
            LastTimings.Insert(0, line);
            while (LastTimings.Count > 50)
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

            dispatcher.Post(action);
        }
        catch
        {
            // Headless tests / no Avalonia dispatcher: still apply under the timings lock.
            action();
        }
    }
}
