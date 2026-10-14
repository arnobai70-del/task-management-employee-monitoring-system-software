using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using TaskMonitoring.Api.Infrastructure;

namespace Backend.Tests;

public sealed class ApiTelemetryMiddlewareTests
{
    [Fact]
    public async Task Middleware_replaces_untrusted_trace_identifier_and_returns_request_id()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/test";
        context.TraceIdentifier = "client-controlled-value";
        context.RequestAborted = cancellationToken;

        var middleware = new ApiTelemetryMiddleware(
            next: httpContext =>
            {
                httpContext.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            },
            logger: NullLogger<ApiTelemetryMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        var requestId = context.Response.Headers["X-Request-ID"].ToString();
        Assert.NotEmpty(requestId);
        Assert.NotEqual("client-controlled-value", requestId);
        Assert.Equal(requestId, context.TraceIdentifier);
        Assert.Matches("^[0-9a-f]{32}$", requestId);
    }

    [Fact]
    public async Task Middleware_records_completed_request_with_low_cardinality_status_class()
    {
        var measurements = new ConcurrentBag<(long Value, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == ApiTelemetry.MeterName && instrument.Name == "taskmonitoring.api.requests")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            if (instrument.Name != "taskmonitoring.api.requests")
            {
                return;
            }

            var copiedTags = tags.ToArray().ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            measurements.Add((measurement, copiedTags));
        });
        listener.Start();

        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/test";

        var middleware = new ApiTelemetryMiddleware(
            next: httpContext =>
            {
                httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return Task.CompletedTask;
            },
            logger: NullLogger<ApiTelemetryMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        var recorded = Assert.Single(measurements);
        Assert.Equal(1, recorded.Value);
        Assert.Equal(HttpMethods.Post, recorded.Tags["method"]);
        Assert.Equal("5xx", recorded.Tags["status_class"]);
        Assert.DoesNotContain("path", recorded.Tags.Keys);
        Assert.DoesNotContain("request_id", recorded.Tags.Keys);
    }
}
