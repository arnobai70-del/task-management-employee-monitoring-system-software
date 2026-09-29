using TaskMonitoring.Employee.Shared;

namespace EmployeeClient.Tests;

public sealed class ServerEndpointPolicyTests
{
    [Fact]
    public void Https_endpoint_is_allowed_and_normalized()
    {
        var uri = ServerEndpointPolicy.ParseAndValidate("https://monitor.example.com/api");

        Assert.Equal("https", uri.Scheme);
        Assert.Equal("https://monitor.example.com/api/", uri.AbsoluteUri);
    }

    [Fact]
    public void Loopback_http_is_allowed_for_local_development()
    {
        var uri = ServerEndpointPolicy.ParseAndValidate("http://localhost:5000");

        Assert.True(uri.IsLoopback);
        Assert.Equal("http://localhost:5000/", uri.AbsoluteUri);
    }

    [Fact]
    public void Non_loopback_http_is_rejected()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ServerEndpointPolicy.ParseAndValidate("http://monitor.example.com"));

        Assert.Contains("require HTTPS", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Relative_or_empty_endpoint_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => ServerEndpointPolicy.ParseAndValidate("/api"));
        Assert.Throws<InvalidOperationException>(() => ServerEndpointPolicy.ParseAndValidate(" "));
    }
}
