using Microsoft.AspNetCore.SignalR.Client;

namespace TaskMonitoring.EmployeeDesktop;

public sealed class EmployeeRealtimeClient(EmployeeApiClient apiClient) : IAsyncDisposable
{
    private HubConnection? _connection;

    public event EventHandler<EmployeeNotificationResponse>? NotificationReceived;
    public event EventHandler<bool>? ConnectionChanged;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_connection is not null)
        {
            return;
        }

        var baseUri = apiClient.ServerBaseUri ?? throw new InvalidOperationException("Configure the server before starting realtime features.");
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(baseUri, "hubs/realtime"), options =>
            {
                options.AccessTokenProvider = async () => await apiClient.GetValidAccessTokenAsync();
            })
            .WithAutomaticReconnect(new[]
            {
                TimeSpan.Zero,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(30)
            })
            .Build();

        connection.On<EmployeeNotificationResponse>("notificationCreated", notification =>
            NotificationReceived?.Invoke(this, notification));
        connection.Reconnecting += _ =>
        {
            ConnectionChanged?.Invoke(this, false);
            return Task.CompletedTask;
        };
        connection.Reconnected += _ =>
        {
            ConnectionChanged?.Invoke(this, true);
            return Task.CompletedTask;
        };
        connection.Closed += _ =>
        {
            ConnectionChanged?.Invoke(this, false);
            return Task.CompletedTask;
        };

        _connection = connection;
        try
        {
            await connection.StartAsync(cancellationToken);
            ConnectionChanged?.Invoke(this, true);
        }
        catch
        {
            _connection = null;
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var connection = _connection;
        _connection = null;
        if (connection is null)
        {
            return;
        }

        try
        {
            await connection.StopAsync(cancellationToken);
        }
        finally
        {
            await connection.DisposeAsync();
            ConnectionChanged?.Invoke(this, false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
