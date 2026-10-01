using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using TaskMonitoring.Api.Configuration;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Hubs;
using TaskMonitoring.Api.Infrastructure;
using TaskMonitoring.Api.Security;
using TaskMonitoring.Api.Services;

var builder = WebApplication.CreateBuilder(args);

var keyPerFileDirectory = Environment.GetEnvironmentVariable("TASKMONITORING_KEY_PER_FILE_DIRECTORY")?.Trim();
if (!string.IsNullOrWhiteSpace(keyPerFileDirectory))
{
    if (!Directory.Exists(keyPerFileDirectory))
    {
        throw new InvalidOperationException($"Configured key-per-file directory does not exist: {keyPerFileDirectory}");
    }

    builder.Configuration.AddKeyPerFile(keyPerFileDirectory, optional: false);
}

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required.");
}

var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
if (string.IsNullOrWhiteSpace(jwtOptions.Issuer) || string.IsNullOrWhiteSpace(jwtOptions.Audience))
{
    throw new InvalidOperationException("Jwt:Issuer and Jwt:Audience are required.");
}
if (string.IsNullOrWhiteSpace(jwtOptions.SigningKey) || Encoding.UTF8.GetByteCount(jwtOptions.SigningKey) < 32)
{
    throw new InvalidOperationException("Jwt:SigningKey must contain at least 32 bytes and must be supplied through secure configuration.");
}
if (jwtOptions.AccessTokenMinutes is < 1 or > 60 || jwtOptions.RefreshTokenDays is < 1 or > 30)
{
    throw new InvalidOperationException("JWT token lifetimes are outside the hardened range (access: 1-60 minutes, refresh: 1-30 days).");
}

var presenceOptions = builder.Configuration.GetSection(PresenceOptions.SectionName).Get<PresenceOptions>() ?? new PresenceOptions();
if (presenceOptions.OnlineThresholdSeconds is < 30 or > 600)
{
    throw new InvalidOperationException("Presence:OnlineThresholdSeconds must be between 30 and 600 seconds.");
}

var reverseProxyOptions = builder.Configuration.GetSection(ReverseProxyOptions.SectionName).Get<ReverseProxyOptions>() ?? new ReverseProxyOptions();
SecurityRegistration.ConfigureTrustedForwardedHeaders(builder.Services, reverseProxyOptions);

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.Configure<PresenceOptions>(builder.Configuration.GetSection(PresenceOptions.SectionName));
builder.Services.AddOptions<WebsiteWorkAttentionOptions>()
    .Bind(builder.Configuration.GetSection(WebsiteWorkAttentionOptions.SectionName))
    .Validate(options => options.LongWorkingMinutes is >= 15 and <= 1440, "WebsiteWorkAttention:LongWorkingMinutes must be between 15 and 1440.")
    .Validate(options => options.PendingReviewMinutes is >= 5 and <= 1440, "WebsiteWorkAttention:PendingReviewMinutes must be between 5 and 1440.")
    .Validate(options => options.RepeatedCorrectionCount is >= 1 and <= 20, "WebsiteWorkAttention:RepeatedCorrectionCount must be between 1 and 20.")
    .ValidateOnStart();
builder.Services.AddOptions<FollowUpReminderOptions>()
    .Bind(builder.Configuration.GetSection(FollowUpReminderOptions.SectionName))
    .Validate(options => options.DueSoonMinutes is >= 5 and <= 1440, "FollowUpReminders:DueSoonMinutes must be between 5 and 1440.")
    .Validate(options => options.EscalationAfterMinutes is >= 5 and <= 10080, "FollowUpReminders:EscalationAfterMinutes must be between 5 minutes and 7 days.")
    .Validate(options =>
        options.EscalationFallbackRoles is { Length: > 0 } &&
        options.EscalationFallbackRoles.All(role => !string.IsNullOrWhiteSpace(role)),
        "FollowUpReminders:EscalationFallbackRoles must contain at least one non-empty role name.")
    .Validate(options => options.ScanIntervalSeconds is >= 30 and <= 3600, "FollowUpReminders:ScanIntervalSeconds must be between 30 and 3600.")
    .ValidateOnStart();
builder.Services.AddOptions<OperationsOptions>()
    .Bind(builder.Configuration.GetSection(OperationsOptions.SectionName))
    .Validate(options => options.DetailedAgentStaleMinutes is >= 1 and <= 60, "Operations:DetailedAgentStaleMinutes must be between 1 and 60.")
    .Validate(options => options.BackupStaleHours is >= 1 and <= 168, "Operations:BackupStaleHours must be between 1 and 168.")
    .Validate(options => options.AgentOfflineMinutes is >= 1 and <= 1440, "Operations:AgentOfflineMinutes must be between 1 and 1440.")
    .Validate(options => options.DatabaseLatencyWarningMilliseconds is >= 100 and <= 60000, "Operations:DatabaseLatencyWarningMilliseconds must be between 100 and 60000.")
    .Validate(options => options.IncidentScanIntervalSeconds is >= 30 and <= 3600, "Operations:IncidentScanIntervalSeconds must be between 30 and 3600.")
    .Validate(options => options.IncidentReopenCooldownMinutes is >= 1 and <= 1440, "Operations:IncidentReopenCooldownMinutes must be between 1 and 1440.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.StableReleaseManifestPath), "Operations:StableReleaseManifestPath is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.BackupStatusPath), "Operations:BackupStatusPath is required.")
    .ValidateOnStart();
builder.Services.AddOptions<AgentUpdateOptions>()
    .Bind(builder.Configuration.GetSection(AgentUpdateOptions.SectionName))
    .Validate(options => string.IsNullOrWhiteSpace(options.EnrollmentKey) || Encoding.UTF8.GetByteCount(options.EnrollmentKey) >= 32,
        "AgentUpdates:EnrollmentKey must contain at least 32 bytes when configured.")
    .Validate(options => options.DeviceTokenBytes is >= 24 and <= 64,
        "AgentUpdates:DeviceTokenBytes must be between 24 and 64.")
    .Validate(options => options.MaxRolloutWindowHours is >= 1 and <= 720,
        "AgentUpdates:MaxRolloutWindowHours must be between 1 and 720.")
    .ValidateOnStart();
builder.Services.AddOptions<SecurityObservabilityOptions>()
    .Bind(builder.Configuration.GetSection(SecurityObservabilityOptions.SectionName))
    .Validate(options => options.DefaultWindowHours is >= 1 and <= 168, "SecurityObservability:DefaultWindowHours must be between 1 and 168.")
    .Validate(options => options.MaxWindowHours is >= 1 and <= 720, "SecurityObservability:MaxWindowHours must be between 1 and 720.")
    .Validate(options => options.DefaultWindowHours <= options.MaxWindowHours, "SecurityObservability:DefaultWindowHours cannot exceed MaxWindowHours.")
    .Validate(options => options.CorrelationWindowMinutes is >= 1 and <= 1440, "SecurityObservability:CorrelationWindowMinutes must be between 1 and 1440.")
    .Validate(options => options.FailedLoginThreshold is >= 2 and <= 1000, "SecurityObservability:FailedLoginThreshold must be between 2 and 1000.")
    .Validate(options => options.RateLimitThreshold is >= 2 and <= 1000, "SecurityObservability:RateLimitThreshold must be between 2 and 1000.")
    .Validate(options => options.MinimumAuditRetentionDays is >= 1 and <= 3650, "SecurityObservability:MinimumAuditRetentionDays must be between 1 and 3650.")
    .Validate(options => options.ExportMaxRecords is >= 100 and <= 100000, "SecurityObservability:ExportMaxRecords must be between 100 and 100000.")
    .Validate(options => options.AlertScanIntervalSeconds is >= 30 and <= 3600, "SecurityObservability:AlertScanIntervalSeconds must be between 30 and 3600.")
    .Validate(options => options.AlertEscalationAfterMinutes is >= 1 and <= 1440, "SecurityObservability:AlertEscalationAfterMinutes must be between 1 and 1440.")
    .Validate(options => options.AlertReopenCooldownMinutes is >= 1 and <= 1440, "SecurityObservability:AlertReopenCooldownMinutes must be between 1 and 1440.")
    .ValidateOnStart();
builder.Services.AddScoped<TaskNotificationInterceptor>();
builder.Services.AddScoped<SurveyNotificationInterceptor>();
builder.Services.AddScoped<WebsiteWorkFollowUpRealtimeInterceptor>();
builder.Services.AddScoped<AdminNotificationRealtimeInterceptor>();
builder.Services.AddDbContext<AppDbContext>((serviceProvider, options) =>
    options.UseNpgsql(connectionString).AddInterceptors(
        serviceProvider.GetRequiredService<TaskNotificationInterceptor>(),
        serviceProvider.GetRequiredService<SurveyNotificationInterceptor>(),
        serviceProvider.GetRequiredService<WebsiteWorkFollowUpRealtimeInterceptor>(),
        serviceProvider.GetRequiredService<AdminNotificationRealtimeInterceptor>()));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IAgentHealthRegistry, AgentHealthRegistry>();
builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IEmployeeCoreService, EmployeeCoreService>();
builder.Services.AddScoped<IAttendanceCoreService, AttendanceCoreService>();
builder.Services.AddScoped<IProjectTaskCoreService, ProjectTaskCoreService>();
builder.Services.AddScoped<IWebsiteWorkService, WebsiteWorkService>();
builder.Services.AddScoped<IWebsiteWorkProgressService, WebsiteWorkProgressService>();
builder.Services.AddScoped<IWebsiteWorkReviewService, WebsiteWorkReviewService>();
builder.Services.AddScoped<IWebsiteWorkProductivityReportService, WebsiteWorkProductivityReportService>();
builder.Services.AddScoped<IWebsiteWorkTimelineService, WebsiteWorkTimelineService>();
builder.Services.AddScoped<IWebsiteWorkAttentionService, WebsiteWorkAttentionService>();
builder.Services.AddScoped<IWebsiteWorkAttentionActionService, WebsiteWorkAttentionActionService>();
builder.Services.AddScoped<IFollowUpSlaAnalyticsService, FollowUpSlaAnalyticsService>();
builder.Services.AddScoped<IAdminNotificationService, AdminNotificationService>();
builder.Services.AddScoped<IFollowUpReminderService, FollowUpReminderService>();
builder.Services.AddScoped<ISurveyCoreService, SurveyCoreService>();
builder.Services.AddScoped<IExternalSurveyService, ExternalSurveyService>();
builder.Services.AddScoped<IReportingDashboardService, ReportingDashboardService>();
builder.Services.AddScoped<IAccessAssignmentService, AccessAssignmentService>();
builder.Services.AddScoped<IEmployeeWorkspaceService, EmployeeWorkspaceService>();
builder.Services.AddScoped<IRealtimeWorkspaceService, RealtimeWorkspaceService>();
builder.Services.AddScoped<IMonitoringTelemetryService, MonitoringTelemetryService>();
builder.Services.AddScoped<IOperationsHealthService, OperationsHealthService>();
builder.Services.AddScoped<IOperationsIncidentService, OperationsIncidentService>();
builder.Services.AddScoped<IAgentUpdateService, AgentUpdateService>();
builder.Services.AddScoped<IAgentUpdateIncidentBridge, AgentUpdateIncidentBridge>();
builder.Services.AddScoped<IAuditLogService, AuditLogService>();
builder.Services.AddScoped<ISecurityObservabilityService, SecurityObservabilityService>();
builder.Services.AddScoped<ISecurityAlertService, SecurityAlertService>();
builder.Services.AddSingleton<IRealtimeEventPublisher, SignalRRealtimeEventPublisher>();
builder.Services.AddSingleton<IWebsiteWorkRealtimePublisher, SignalRWebsiteWorkRealtimePublisher>();
builder.Services.AddSingleton<IAdminNotificationRealtimePublisher, SignalRAdminNotificationRealtimePublisher>();
builder.Services.AddSingleton<IOperationsIncidentRealtimePublisher, SignalROperationsIncidentRealtimePublisher>();
builder.Services.AddSingleton<ISecurityAlertRealtimePublisher, SignalRSecurityAlertRealtimePublisher>();
builder.Services.AddHostedService<MonitoringRetentionHostedService>();
builder.Services.AddHostedService<FollowUpReminderHostedService>();
builder.Services.AddHostedService<OperationsIncidentHostedService>();
builder.Services.AddHostedService<AgentUpdateIncidentHostedService>();
builder.Services.AddHostedService<SecurityAlertHostedService>();
builder.Services.AddScoped<DatabaseInitializer>();
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, PermissionAuthorizationHandler>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.SaveToken = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidAudience = jwtOptions.Audience,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey)),
            ClockSkew = TimeSpan.FromSeconds(30),
            RoleClaimType = System.Security.Claims.ClaimTypes.Role,
            NameClaimType = System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Email
        };
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrWhiteSpace(accessToken) && context.HttpContext.Request.Path.StartsWithSegments("/hubs/realtime"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddTaskMonitoringAuthorization();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        try
        {
            var httpContext = context.HttpContext;
            var dbContext = httpContext.RequestServices.GetRequiredService<AppDbContext>();
            dbContext.AuditLogs.Add(new AuditLog
            {
                Action = SecurityObservabilityService.RateLimitRejectedAction,
                TargetType = "Endpoint",
                TargetId = $"{httpContext.Request.Method} {httpContext.Request.Path}",
                MetadataJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    method = httpContext.Request.Method,
                    path = httpContext.Request.Path.Value
                }),
                IpAddress = httpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent = httpContext.Request.Headers.UserAgent.ToString(),
                CreatedAtUtc = DateTime.UtcNow
            });
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            var logger = context.HttpContext.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("RateLimitAudit");
            logger.LogWarning(ex, "Could not persist rate-limit rejection audit event.");
        }
    };
    options.AddPolicy("auth", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    options.AddPolicy("agent-enrollment", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(5),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    options.AddPolicy("agent-device", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Request.RouteValues["deviceId"]?.ToString() ??
            httpContext.Connection.RemoteIpAddress?.ToString() ??
            "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddControllers().AddJsonOptions(options =>
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));
builder.Services.AddSignalR().AddJsonProtocol(options =>
    options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseReadinessHealthCheck>("database", tags: ["ready"]);

var app = builder.Build();

if (args.Any(argument => string.Equals(argument, "--migrate-only", StringComparison.OrdinalIgnoreCase)))
{
    await using var migrationScope = app.Services.CreateAsyncScope();
    var migrationInitializer = migrationScope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
    await migrationInitializer.MigrateAsync();
    await migrationInitializer.SeedFoundationAsync();
    return;
}

app.UseExceptionHandler();
if (reverseProxyOptions.TrustForwardedHeaders)
{
    app.UseForwardedHeaders();
}
app.UseHttpsRedirection();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<RealtimeHub>("/hubs/realtime");
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = registration => registration.Tags.Contains("ready") }).AllowAnonymous();
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = registration => registration.Tags.Contains("ready") }).AllowAnonymous();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

await using (var scope = app.Services.CreateAsyncScope())
{
    var initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
    if (app.Configuration.GetValue<bool>("Database:AutoMigrate"))
    {
        await initializer.MigrateAsync();
    }

    await initializer.SeedFoundationAsync();
}

app.Run();

public partial class Program;