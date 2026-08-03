var builder = DistributedApplication.CreateBuilder(args);

// Desktop app: Aspire injects OTEL_EXPORTER_OTLP_ENDPOINT so CodeReviewr exports
// traces/metrics to the Aspire dashboard (see OpenTelemetryBootstrap).
builder.AddProject<Projects.CodeReviewr_App>("codereviewr");

builder.Build().Run();
