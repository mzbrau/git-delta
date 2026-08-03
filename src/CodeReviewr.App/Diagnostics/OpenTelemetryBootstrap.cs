using CodeReviewr.Core.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace CodeReviewr.App.Diagnostics;

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

        var serviceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") ?? "CodeReviewr";
        var resource = ResourceBuilder.CreateDefault().AddService(serviceName);

        var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(resource)
            .AddSource(CodeReviewrActivity.SourceName)
            .AddOtlpExporter()
            .Build();

        var meterProvider = Sdk.CreateMeterProviderBuilder()
            .SetResourceBuilder(resource)
            .AddMeter(CodeReviewrMeters.MeterName)
            .AddOtlpExporter()
            .Build();

        return new Providers(tracerProvider, meterProvider);
    }

    /// <summary>Records paint duration; emits a span only for severe jank (≥ <see cref="SlowPaintMs"/> ms).</summary>
    public static void RecordDiffRender(double elapsedMs, int visibleRows)
    {
        CodeReviewrMeters.DiffRenderMs.Record(elapsedMs);
        if (elapsedMs < SlowPaintMs)
            return;

        using var activity = CodeReviewrActivity.Source.StartActivity("diff.render.slow");
        activity?.SetTag("diff.visible_rows", visibleRows);
        activity?.SetTag("diff.render_ms", elapsedMs);
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
