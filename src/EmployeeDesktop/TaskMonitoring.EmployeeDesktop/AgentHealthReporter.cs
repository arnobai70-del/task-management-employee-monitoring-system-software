using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace TaskMonitoring.EmployeeDesktop;

internal sealed class AgentHealthReporter(EmployeeApiClient apiClient) : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly AgentHealthCollector _collector = new();

    public async Task ReportAsync(CancellationToken cancellationToken = default)
    {
        var baseUri = apiClient.ServerBaseUri ?? throw new InvalidOperationException("Configure the server before reporting agent health.");
        var token = await apiClient.GetValidAccessTokenAsync(cancellationToken);
        var health = await _collector.CaptureAsync(cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "api/me/agent-health"))
        {
            Content = JsonContent.Create(health)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public void Dispose() => _http.Dispose();
}
