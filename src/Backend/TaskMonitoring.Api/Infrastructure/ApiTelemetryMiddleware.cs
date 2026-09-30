using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace TaskMonitoring.Api.Infrastructure;

public static class ApiTelemetry
{
    public const string MeterName = "TaskMonitoring.Api";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> RequestCounter = Meter.CreateCounter<long>(
        "taskmonitoring.api.requests",
        description: "Completed HTTP requests handled by the TaskMonitoring API.");
    private static readonly Histogram<double> RequestDuration = Meter.CreateHistogram<double>(
        "taskmonitoring.api.request.duration",
        unit: "s",
        description: "TaskMonitoring API HTTP request duration in seconds.");
    private static readonly UpDownCounter<long> ActiveRequests = Meter.CreateUpDownCounter<long>(
        "taskmonitoring.api.active_requests",
        description: "HTTP requests currently executing in the TaskMonitoring API.");
    private static readonly ObservableGauge<long> Up = Meter.CreateObservableGauge(
        "taskmonitoring.api.up",
        () => 1L,
        description: "Reports 1 while the TaskMonitoring API telemetry pipeline is running.");

    internal static void RequestStarted(string method)
    {
        ActiveRequests.Add(1, new KeyValuePair<string, object?>("method", method));
    }

    internal static void RequestCompleted(string method, int statusCode, double elapsedSeconds)
    {
        var tags = new TagList
        {
            { "method", method },
            { "status_class", StatusClass(statusCode) }
        };

        RequestCounter.Add(1, tags);
        RequestDuration.Record(elapsedSeconds, tags);
        ActiveRequests.Add(-1, new KeyValuePair<string, object?>("method", method));
    }

    internal static string StatusClass(int statusCode) => statusCode switch
    {
        >= 100 and <= 199 => "1xx",
        >= 200 and <= 299 => "2xx",
        >= 300 and <= 399 => "3xx",
        >= 400 and <= 499 => "4xx",
        >= 500 and <= 599 => "5xx",
        _ => "other"
    };
}

public sealed class ApiTelemetryMiddleware(RequestDelegate next, ILogger<ApiTelemetryMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var requestId = Activity.Current?.TraceId.ToString();
        if (string.IsNullOrWhiteSpace(requestId))
        {
            requestId = Guid.NewGuid().ToString("N");
        }

        context.TraceIdentifier = requestId;
        context.Response.Headers["X-Request-ID"] = requestId;

        var scopeValues = new[]
        {
            new KeyValuePair<string, object?>("request_id", requestId)
        };

        var startedAt = Stopwatch.GetTimestamp();
        ApiTelemetry.RequestStarted(context.Request.Method);

        using (logger.BeginScope(scopeValues))
        {
            try
            {
                await next(context);
            }
            finally
            {
                var elapsed = Stopwatch.GetElapsedTime(startedAt);
                ApiTelemetry.RequestCompleted(context.Request.Method, context.Response.StatusCode, elapsed.TotalSeconds);

                if (!context.Request.Path.StartsWithSegments("/health"))
                {
                    logger.LogInformation(
                        "HTTP request completed {Method} {Path} with {StatusCode} in {ElapsedMilliseconds} ms",
                        context.Request.Method,
                        context.Request.Path.Value ?? "/",
                        context.Response.StatusCode,
                        elapsed.TotalMilliseconds);
                }
            }
        }
    }
}
