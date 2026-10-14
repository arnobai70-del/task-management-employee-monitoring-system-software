using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TaskMonitoring.Api.Data;

namespace TaskMonitoring.Api.Infrastructure;

public sealed class DatabaseReadinessHealthCheck(IServiceScopeFactory scopeFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (!await db.Database.CanConnectAsync(cancellationToken))
            {
                return HealthCheckResult.Unhealthy("PostgreSQL connection failed.");
            }

            // Production runs the migrator separately with AutoMigrate off, so an API image newer than the schema must not report ready.
            if (db.Database.IsRelational())
            {
                var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
                if (pending.Count > 0)
                {
                    return HealthCheckResult.Unhealthy(
                        $"{pending.Count} database migration(s) not applied; latest pending {pending[^1]}.");
                }
            }

            return HealthCheckResult.Healthy("PostgreSQL connection succeeded and schema is current.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL readiness check failed.", ex);
        }
    }
}
