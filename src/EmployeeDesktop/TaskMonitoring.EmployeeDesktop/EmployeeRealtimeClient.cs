using Microsoft.AspNetCore.SignalR.Client;

namespace TaskMonitoring.EmployeeDesktop;

public sealed class EmployeeRealtimeClient(EmployeeApiClient apiClient) : IAsyncDisposable
{
    private readonly AgentHealthReporter _agentHealthReporter = new(apiClient);
    private HubConnection? _connection;
    private CancellationTokenSource? _agentHealthCancellation;
    private Task? _agentHealthTask;

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
            .WithAutomaticReconnect(PersistentRealtimeRetryPolicy.Instance)
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
            StartAgentHealthLoop();
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
        await StopAgentHealthLoopAsync();
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

    private void StartAgentHealthLoop()
    {
        if (_agentHealthTask is not null)
        {
            return;
        }

        _agentHealthCancellation = new CancellationTokenSource();
        _agentHealthTask = RunAgentHealthLoopAsync(_agentHealthCancellation.Token);
    }

    private async Task RunAgentHealthLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _agentHealthReporter.ReportAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Presence heartbeat remains authoritative for online/offline state.
                // Detailed operational health is best-effort and retries on the next interval.
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task StopAgentHealthLoopAsync()
    {
        var cancellation = _agentHealthCancellation;
        var task = _agentHealthTask;
        _agentHealthCancellation = null;
        _agentHealthTask = null;
        if (cancellation is null)
        {
            return;
        }

        await cancellation.CancelAsync();
        if (task is not null)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
        }
        cancellation.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _agentHealthReporter.Dispose();
    }

    private sealed class PersistentRealtimeRetryPolicy : IRetryPolicy
    {
        public static readonly PersistentRealtimeRetryPolicy Instance = new();

        private static readonly TimeSpan[] Delays =
        [
            TimeSpan.Zero,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30)
        ];

        public TimeSpan? NextRetryDelay(RetryContext retryContext)
        {
            var index = (int)Math.Min(retryContext.PreviousRetryCount, Delays.LongLength - 1);
            return Delays[index];
        }
    }
}
