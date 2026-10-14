using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Security;
using TaskMonitoring.Api.Services;

namespace Backend.Tests;

/// <summary>
/// G-56 through the real HTTP pipeline and PostgreSQL: every monitoring write endpoint used to answer 500
/// because the request records put their validation attributes on properties instead of constructor parameters.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class HttpMonitoringTests(HttpMonitoringTests.ApiFixture api) : IClassFixture<HttpMonitoringTests.ApiFixture>
{
    private const string Disclosure = "Approved work applications and business domains are recorded while you are signed in.";

    [Fact]
    public async Task Administrators_can_update_the_policy_and_approved_rules_and_bad_input_is_a_400()
    {
        var ct = TestContext.Current.CancellationToken;
        var admin = await api.ClientForAsync(ApiFixture.AdminEmail, ct);

        await AssertStatusAsync(HttpStatusCode.OK, await admin.PutAsJsonAsync("/api/monitoring/policy",
            new { isEnabled = true, sampleIntervalSeconds = 60, retentionDays = 30, disclosureText = Disclosure }, ct));
        await AssertStatusAsync(HttpStatusCode.BadRequest, await admin.PutAsJsonAsync("/api/monitoring/policy",
            new { isEnabled = true, sampleIntervalSeconds = 10, retentionDays = 30, disclosureText = Disclosure }, ct));
        await AssertStatusAsync(HttpStatusCode.BadRequest, await admin.PutAsJsonAsync("/api/monitoring/policy",
            new { isEnabled = true, sampleIntervalSeconds = 60, retentionDays = 30, disclosureText = "Too short." }, ct));

        var application = await admin.PostAsJsonAsync("/api/monitoring/applications",
            new { processName = "code", displayName = "Visual Studio Code", captureWindowTitle = true, isActive = true }, ct);
        await AssertSuccessAsync(application);
        using (var json = JsonDocument.Parse(await application.Content.ReadAsStringAsync(ct)))
        {
            var id = json.RootElement.GetProperty("id").GetGuid();
            await AssertSuccessAsync(await admin.PutAsJsonAsync($"/api/monitoring/applications/{id}",
                new { processName = "code", displayName = "VS Code", captureWindowTitle = true, isActive = true }, ct));
        }
        await AssertStatusAsync(HttpStatusCode.BadRequest, await admin.PostAsJsonAsync("/api/monitoring/applications",
            new { processName = "", displayName = "Nameless", captureWindowTitle = false, isActive = true }, ct));

        await AssertSuccessAsync(await admin.PostAsJsonAsync("/api/monitoring/domains",
            new { domain = "jira.company.example", displayName = "Jira", includeSubdomains = false, isActive = true }, ct));
        await AssertStatusAsync(HttpStatusCode.BadRequest, await admin.PostAsJsonAsync("/api/monitoring/domains",
            new { domain = new string('a', 254), displayName = "Too long", includeSubdomains = false, isActive = true }, ct));

        var employee = await api.ClientForAsync(ApiFixture.EmployeeEmail, ct);
        await AssertStatusAsync(HttpStatusCode.Forbidden, await employee.PutAsJsonAsync("/api/monitoring/policy",
            new { isEnabled = false, sampleIntervalSeconds = 60, retentionDays = 30, disclosureText = Disclosure }, ct));
    }

    [Fact]
    public async Task The_desktop_can_record_approved_activity_and_bad_input_is_a_400()
    {
        var ct = TestContext.Current.CancellationToken;
        var employee = await api.ClientForAsync(ApiFixture.EmployeeEmail, ct);

        var accepted = await employee.PostAsJsonAsync("/api/me/monitoring/application-activity",
            new { processName = ApiFixture.ApprovedProcess, windowTitle = "Quarterly plan" }, ct);
        await AssertSuccessAsync(accepted);
        using (var json = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync(ct)))
        {
            Assert.True(json.RootElement.GetProperty("accepted").GetBoolean());
        }

        await AssertSuccessAsync(await employee.PostAsJsonAsync("/api/me/monitoring/business-domain-activity",
            new { domain = ApiFixture.ApprovedDomain }, ct));
        await AssertStatusAsync(HttpStatusCode.BadRequest, await employee.PostAsJsonAsync("/api/me/monitoring/application-activity",
            new { processName = ApiFixture.ApprovedProcess, windowTitle = new string('t', 301) }, ct));
        await AssertStatusAsync(HttpStatusCode.BadRequest, await employee.PostAsJsonAsync("/api/me/monitoring/business-domain-activity",
            new { domain = "" }, ct));

        await using var scope = api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(2, await db.MonitoringActivitySegments.CountAsync(x => x.EmployeeId == api.EmployeeId, ct));
    }

    private static async Task AssertStatusAsync(HttpStatusCode expected, HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.PathAndQuery} returned {(int)response.StatusCode}, expected {(int)expected}: {text}");
    }

    private static async Task AssertSuccessAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.PathAndQuery} returned {(int)response.StatusCode}: {text}");
    }

    public sealed class ApiFixture : IAsyncLifetime
    {
        public const string Password = "Monitoring-Test-Password-2026!";
        public const string AdminEmail = "admin@monitoring.test";
        public const string EmployeeEmail = "employee@monitoring.test";
        public const string ApprovedProcess = "approvedtool";
        public const string ApprovedDomain = "wiki.company.example";

        private string _connectionString = string.Empty;
        private readonly Dictionary<string, string?> _previousEnvironment = new();
        private readonly Dictionary<string, HttpClient> _clients = new();

        public WebApplicationFactory<Program> Factory { get; private set; } = null!;
        public Guid EmployeeId { get; private set; }

        public async ValueTask InitializeAsync()
        {
            var baseConnection = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
            Assert.False(string.IsNullOrWhiteSpace(baseConnection), "TEST_POSTGRES_CONNECTION is required for monitoring HTTP tests.");
            _connectionString = new NpgsqlConnectionStringBuilder(baseConnection)
            {
                Database = $"tm_monitoring_http_{Guid.NewGuid():N}"
            }.ConnectionString;

            SetEnvironment("ConnectionStrings__DefaultConnection", _connectionString);
            SetEnvironment("Jwt__SigningKey", "http-monitoring-test-signing-key-0123456789abcdef");
            SetEnvironment("Database__AutoMigrate", "true");

            Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
            _ = Factory.Server;

            await using var scope = Factory.Services.CreateAsyncScope();
            await SeedAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>());
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var client in _clients.Values)
            {
                client.Dispose();
            }

            await Factory.DisposeAsync();
            foreach (var (key, value) in _previousEnvironment)
            {
                Environment.SetEnvironmentVariable(key, value);
            }

            NpgsqlConnection.ClearAllPools();
            await using var cleanup = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_connectionString).Options);
            await cleanup.Database.EnsureDeletedAsync();
        }

        // One login per account: the auth endpoints are rate limited to 10 requests per minute.
        public async Task<HttpClient> ClientForAsync(string email, CancellationToken ct)
        {
            if (_clients.TryGetValue(email, out var cached))
            {
                return cached;
            }

            var client = Factory.CreateClient();
            var response = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password }, ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.RootElement.GetProperty("accessToken").GetString());
            _clients[email] = client;
            return client;
        }

        private void SetEnvironment(string key, string value)
        {
            _previousEnvironment.TryAdd(key, Environment.GetEnvironmentVariable(key));
            Environment.SetEnvironmentVariable(key, value);
        }

        private async Task SeedAsync(AppDbContext db)
        {
            var hasher = new PasswordHasher<User>();
            var employeeRole = await db.Roles.SingleAsync(x => x.Name == "Employee");
            var adminRole = new Role { Name = "MonitoringAdmin" };
            foreach (var permission in db.Permissions.Where(x => x.Code == PermissionCatalog.MonitoringRead || x.Code == PermissionCatalog.MonitoringManage).ToList())
            {
                adminRole.RolePermissions.Add(new RolePermission { RoleId = adminRole.Id, PermissionId = permission.Id });
            }
            db.Roles.Add(adminRole);

            User AddUser(string email, Role role)
            {
                var user = new User { Email = email, NormalizedEmail = AuthService.NormalizeEmail(email), IsActive = true };
                user.PasswordHash = hasher.HashPassword(user, Password);
                user.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
                db.Users.Add(user);
                return user;
            }

            AddUser(AdminEmail, adminRole);
            var employeeUser = AddUser(EmployeeEmail, employeeRole);
            var employee = new Employee
            {
                UserId = employeeUser.Id,
                User = employeeUser,
                EmployeeCode = "MON-1",
                NormalizedEmployeeCode = "MON-1",
                FullName = "Monitoring Employee",
                NormalizedFullName = "MONITORING EMPLOYEE",
                JobTitle = "Worker",
                IsActive = true
            };
            db.Employees.Add(employee);
            db.ApprovedApplications.Add(new ApprovedApplication
            {
                ProcessName = ApprovedProcess,
                NormalizedProcessName = ApprovedProcess,
                DisplayName = "Approved tool",
                CaptureWindowTitle = true,
                IsActive = true
            });
            db.ApprovedBusinessDomains.Add(new ApprovedBusinessDomain
            {
                Domain = ApprovedDomain,
                NormalizedDomain = ApprovedDomain,
                DisplayName = "Company wiki",
                IsActive = true
            });
            await db.SaveChangesAsync();
            EmployeeId = employee.Id;
        }
    }
}
