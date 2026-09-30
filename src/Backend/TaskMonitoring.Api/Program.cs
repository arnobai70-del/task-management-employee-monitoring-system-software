using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
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
if (string.IsNullOrWhiteSpace(jwtOptions.SigningKey) || Encoding.UTF8.GetByteCount(jwtOptions.SigningKey) < 32)
{
    throw new InvalidOperationException("Jwt:SigningKey must contain at least 32 bytes and must be supplied through secure configuration.");
}

if (jwtOptions.AccessTokenMinutes is < 1 or > 1440 || jwtOptions.RefreshTokenDays is < 1 or > 90)
{
    throw new InvalidOperationException("JWT token lifetimes are outside the allowed range.");
}

var presenceOptions = builder.Configuration.GetSection(PresenceOptions.SectionName).Get<PresenceOptions>() ?? new PresenceOptions();
if (presenceOptions.OnlineThresholdSeconds is < 30 or > 600)
{
    throw new InvalidOperationException("Presence:OnlineThresholdSeconds must be between 30 and 600 seconds.");
}

var trustForwardedHeaders = builder.Configuration.GetValue<bool>("ReverseProxy:TrustForwardedHeaders");
if (trustForwardedHeaders)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        options.ForwardLimit = 1;
    });
}

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(PresenceOptions.SectionName));
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.Configure<PresenceOptions>(builder.Configuration.GetSection(PresenceOptions.SectionName));
builder.Services.AddScoped<TaskNotificationInterceptor>();
builder.Services.AddScoped<SurveyNotificationInterceptor>();
builder.Services.AddDbContext<AppDbContext>((serviceProvider, options) =>
    options.UseNpgsql(connectionString).AddInterceptors(
        serviceProvider.GetRequiredService<TaskNotificationInterceptor>(),
        serviceProvider.GetRequiredService<SurveyNotificationInterceptor>()));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IEmployeeCoreService, EmployeeCoreService>();
builder.Services.AddScoped<IAttendanceCoreService, AttendanceCoreService>();
builder.Services.AddScoped<IProjectTaskCoreService, ProjectTaskCoreService>();
builder.Services.AddScoped<IWebsiteWorkService, WebsiteWorkService>();
builder.Services.AddScoped<ISurveyCoreService, SurveyCoreService>();
builder.Services.AddScoped<IExternalSurveyService, ExternalSurveyService>();
builder.Services.AddScoped<IReportingDashboardService, ReportingDashboardService>();
builder.Services.AddScoped<IAccessAssignmentService, AccessAssignmentService>();
builder.Services.AddScoped<IEmployeeWorkspaceService, EmployeeWorkspaceService>();
builder.Services.AddScoped<IRealtimeWorkspaceService, RealtimeWorkspaceService>();
builder.Services.AddScoped<IMonitoringTelemetryService, MonitoringTelemetryService>();
builder.Services.AddScoped<IAuditLogService, AuditLogService>();
builder.Services.AddSingleton<IRealtimeEventPublisher, SignalRRealtimeEventPublisher>();
builder.Services.AddSingleton<IWebsiteWorkRealtimePublisher, SignalRWebsiteWorkRealtimePublisher>();
builder.Services.AddHostedService<MonitoringRetentionHostedService>();
builder.Services.AddScoped<DatabaseInitializer>();
builder.Services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidAudience = jwtOptions.Audience,
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

builder.Services.AddAuthorization(options =>
{
    foreach (var permission in PermissionCatalog.All)
    {
        options.AddPolicy(permission, policy => policy.Requirements.Add(new PermissionRequirement(permission)));
    }
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
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
if (trustForwardedHeaders)
{
    app.UseForwardedHeaders();
}
app.UseHttpsRedirection();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<RealtimeHub>("/hubs/realtime");
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = registration => registration.Tags.Contains("ready") });
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = registration => registration.Tags.Contains("ready") });
app.MapOpenApi();

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
