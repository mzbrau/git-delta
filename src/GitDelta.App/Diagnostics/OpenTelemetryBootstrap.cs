using GitDelta.Core.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace GitDelta.App.Diagnostics;

/// <summary>
/// Starts OTLP trace/metric export when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set
/// (Aspire injects this). Does not use Generic Host.
/// </summary>
public static class OpenTelemetryBootstrap
{
    // Spans only for severe jank (~3 frames at 60Hz); histogram covers the full distribution.
    private const double SlowPaintMs = 50;

    public static IDisposable? StartIfConfigured()
    {
        var endpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint))
            return null;

        var serviceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") ?? "GitDelta";
        var resource = ResourceBuilder.CreateDefault().AddService(serviceName);

        var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(resource)
            .AddSource(GitDeltaActivity.SourceName)
            .AddOtlpExporter()
            .Build();

        var meterProvider = Sdk.CreateMeterProviderBuilder()
            .SetResourceBuilder(resource)
            .AddMeter(GitDeltaMeters.MeterName)
            .AddOtlpExporter()
            .Build();

        return new Providers(tracerProvider, meterProvider);
    }

    /// <summary>Records paint duration; emits a span only for severe jank (≥ <see cref="SlowPaintMs"/> ms).</summary>
    public static void RecordDiffRender(double elapsedMs, int visibleRows)
    {
        GitDeltaMeters.DiffRenderMs.Record(elapsedMs);
        if (elapsedMs < SlowPaintMs)
            return;

        using var activity = GitDeltaActivity.Source.StartActivity("diff.render.slow");
        activity?.SetTag("diff.visible_rows", visibleRows);
        activity?.SetTag("diff.render_ms", elapsedMs);
    }

    /// <summary>
    /// Records scroll gesture → first paint latency; emits a span only for severe jank
    /// (≥ <see cref="SlowPaintMs"/> ms).
    /// </summary>
    public static void RecordDiffScroll(
        double gestureToPaintMs,
        int cacheMissRows,
        double maxWidthScanMs,
        bool paintWarmPending,
        bool firstAfterBind,
        int scrollDir)
    {
        GitDeltaMeters.DiffScrollGestureToPaintMs.Record(gestureToPaintMs);
        if (gestureToPaintMs < SlowPaintMs)
            return;

        using var activity = GitDeltaActivity.Source.StartActivity("diff.scroll.first.slow");
        activity?.SetTag("diff.cache_miss_rows", cacheMissRows);
        activity?.SetTag("diff.max_width_scan_ms", maxWidthScanMs);
        activity?.SetTag("diff.paint_warm_pending", paintWarmPending);
        activity?.SetTag("diff.first_after_bind", firstAfterBind);
        activity?.SetTag("diff.scroll_dir", scrollDir);
        activity?.SetTag("diff.gesture_to_paint_ms", gestureToPaintMs);
    }

    /// <summary>Records file-list column resize layout; emits a span only for severe jank (≥ <see cref="SlowPaintMs"/> ms).</summary>
    public static void RecordFileListResize(double elapsedMs, int visibleApprox)
    {
        GitDeltaMeters.UiFileListResizeMs.Record(elapsedMs);
        if (elapsedMs < SlowPaintMs)
            return;

        using var activity = GitDeltaActivity.Source.StartActivity("ui.filelist.resize.slow");
        activity?.SetTag("filelist.visible_approx", visibleApprox);
        activity?.SetTag("filelist.resize_ms", elapsedMs);
    }

    private sealed class Providers(TracerProvider tracer, MeterProvider meter) : IDisposable
    {
        public void Dispose()
        {
            tracer.Dispose();
            meter.Dispose();
        }
    }
}
