var builder = DistributedApplication.CreateBuilder(args);

// Desktop app: Aspire injects OTEL_EXPORTER_OTLP_ENDPOINT so GitDelta exports
// traces/metrics to the Aspire dashboard (see OpenTelemetryBootstrap).
builder.AddProject<Projects.GitDelta_App>("gitdelta");

builder.Build().Run();
