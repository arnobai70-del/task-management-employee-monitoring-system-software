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
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Security;
using TaskMonitoring.Api.Services;

namespace Backend.Tests;

/// <summary>
/// G-28 through the real HTTP pipeline and PostgreSQL: only attendance.manage can correct a session,
/// and concurrent auto-close attempts close a forgotten session exactly once.
/// </summary>
public sealed class HttpAttendanceCorrectionTests(HttpAttendanceCorrectionTests.ApiFixture api) : IClassFixture<HttpAttendanceCorrectionTests.ApiFixture>
{
    [Fact]
    public async Task Only_attendance_managers_can_correct_a_session()
    {
        var ct = TestContext.Current.CancellationToken;
        var body = new
        {
            startedAtUtc = api.ClosedSessionScheduledStart,
            endedAtUtc = api.ClosedSessionScheduledStart.AddHours(7),
            reason = "Supervisor confirmed early leave"
        };
        var path = $"/api/attendance/{api.ClosedSessionId}/correction";

        using var anonymous = api.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(path, body, ct)).StatusCode);
        var employee = await api.ClientForAsync(ApiFixture.EmployeeEmail, ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.PostAsJsonAsync(path, body, ct)).StatusCode);
        var reader = await api.ClientForAsync(ApiFixture.ReaderEmail, ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(path, body, ct)).StatusCode);

        var manager = await api.ClientForAsync(ApiFixture.ManagerEmail, ct);
        var missingReason = await manager.PostAsJsonAsync(path, new { body.startedAtUtc, body.endedAtUtc, reason = "" }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, missingReason.StatusCode);
        var response = await manager.PostAsJsonAsync(path, body, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Correction returned {(int)response.StatusCode}: {text}");
        using var json = JsonDocument.Parse(text);
        Assert.Equal("AdminCorrection", json.RootElement.GetProperty("closureSource").GetString());
        Assert.Equal(60, json.RootElement.GetProperty("earlyLeaveMinutes").GetInt32());

        await using var scope = api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var audit = await db.AuditLogs.AsNoTracking().SingleAsync(x => x.Action == AttendanceCoreService.CorrectedAction, ct);
        Assert.Equal(api.ManagerUserId, audit.ActorUserId);
    }

    [Fact]
    public async Task Concurrent_checkouts_and_sweeps_close_a_forgotten_session_exactly_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var actor = new RequestActor(api.EmployeeUserId, "127.0.0.1", "tests");

        var attempts = Enumerable.Range(0, 8).Select(async index =>
        {
            await using var scope = api.Factory.Services.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<IAttendanceCoreService>();
            if (index % 2 == 0)
            {
                await service.AutoCloseStaleSessionsAsync(ct);
            }
            else
            {
                await service.CheckOutAsync(actor, ct);
            }
        });
        await Task.WhenAll(attempts);

        await using var verify = api.Factory.Services.CreateAsyncScope();
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.WorkSessions.AsNoTracking().SingleAsync(x => x.Id == api.StaleSessionId, ct);
        Assert.Equal(WorkSessionClosureSource.AutoClosed, session.ClosureSource);
        Assert.Equal(session.ScheduledEndUtc, session.EndedAtUtc);
        Assert.Equal(1, await db.AuditLogs.CountAsync(
            x => x.Action == AttendanceCoreService.AutoClosedAction && x.TargetId == api.StaleSessionId.ToString(), ct));
    }

    public sealed class ApiFixture : IAsyncLifetime
    {
        public const string Password = "Correction-Test-Password-2026!";
        public const string EmployeeEmail = "employee@correction.test";
        public const string ReaderEmail = "reader@correction.test";
        public const string ManagerEmail = "manager@correction.test";

        private string _connectionString = string.Empty;
        private readonly Dictionary<string, string?> _previousEnvironment = new();
        private readonly Dictionary<string, HttpClient> _clients = new();

        public WebApplicationFactory<Program> Factory { get; private set; } = null!;
        public Guid EmployeeUserId { get; private set; }
        public Guid ManagerUserId { get; private set; }
        public Guid ClosedSessionId { get; private set; }
        public DateTime ClosedSessionScheduledStart { get; private set; }
        public Guid StaleSessionId { get; private set; }

        public async ValueTask InitializeAsync()
        {
            var baseConnection = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
            Assert.False(string.IsNullOrWhiteSpace(baseConnection), "TEST_POSTGRES_CONNECTION is required for attendance correction tests.");
            _connectionString = new NpgsqlConnectionStringBuilder(baseConnection)
            {
                Database = $"tm_attendance_correction_{Guid.NewGuid():N}"
            }.ConnectionString;

            SetEnvironment("ConnectionStrings__DefaultConnection", _connectionString);
            SetEnvironment("Jwt__SigningKey", "http-attendance-correction-test-signing-key-0123456789");
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
            Role AddRole(string name, params string[] codes)
            {
                var role = new Role { Name = name };
                foreach (var permission in db.Permissions.Where(x => codes.Contains(x.Code)).ToList())
                {
                    role.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = permission.Id });
                }

                db.Roles.Add(role);
                return role;
            }

            User AddUser(string email, Role role)
            {
                var user = new User { Email = email, NormalizedEmail = AuthService.NormalizeEmail(email), IsActive = true };
                user.PasswordHash = hasher.HashPassword(user, Password);
                user.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
                db.Users.Add(user);
                return user;
            }

            var readerRole = AddRole("CorrectionReader", PermissionCatalog.AttendanceRead);
            var managerRole = AddRole("CorrectionManager", PermissionCatalog.AttendanceRead, PermissionCatalog.AttendanceManage);
            var employeeUser = AddUser(EmployeeEmail, employeeRole);
            AddUser(ReaderEmail, readerRole);
            var managerUser = AddUser(ManagerEmail, managerRole);

            var employee = new Employee
            {
                UserId = employeeUser.Id,
                User = employeeUser,
                EmployeeCode = "COR-1",
                NormalizedEmployeeCode = "COR-1",
                FullName = "Correction Employee",
                NormalizedFullName = "CORRECTION EMPLOYEE",
                JobTitle = "Worker",
                IsActive = true
            };
            db.Employees.Add(employee);
            var shift = new Shift
            {
                Code = "COR-DAY",
                NormalizedCode = "COR-DAY",
                Name = "Correction Day",
                NormalizedName = "CORRECTION DAY",
                StartTime = new TimeOnly(9, 0),
                EndTime = new TimeOnly(17, 0),
                TimeZoneId = "Asia/Dhaka",
                GraceMinutes = 0,
                IsActive = true
            };
            db.Shifts.Add(shift);
            var assignment = new EmployeeShiftAssignment
            {
                EmployeeId = employee.Id,
                Employee = employee,
                ShiftId = shift.Id,
                Shift = shift,
                EffectiveFrom = new DateOnly(2026, 1, 1)
            };
            db.EmployeeShiftAssignments.Add(assignment);

            WorkSession AddSession(DateOnly workDate, DateTime scheduledStart, bool closed)
            {
                var session = new WorkSession
                {
                    EmployeeId = employee.Id,
                    Employee = employee,
                    ShiftId = shift.Id,
                    Shift = shift,
                    ShiftAssignmentId = assignment.Id,
                    ShiftAssignment = assignment,
                    WorkDate = workDate,
                    ScheduledStartUtc = scheduledStart,
                    ScheduledEndUtc = scheduledStart.AddHours(8),
                    StartedAtUtc = scheduledStart,
                    EndedAtUtc = closed ? scheduledStart.AddHours(8) : null,
                    EarlyLeaveMinutes = closed ? 0 : null,
                    ClosureSource = closed ? WorkSessionClosureSource.Employee : null
                };
                db.WorkSessions.Add(session);
                return session;
            }

            // Fixed past dates keep the sessions independent of when the suite runs.
            var closedStart = new DateTime(2026, 3, 2, 3, 0, 0, DateTimeKind.Utc);
            var closedSession = AddSession(new DateOnly(2026, 3, 2), closedStart, closed: true);
            var staleSession = AddSession(new DateOnly(2026, 3, 3), closedStart.AddDays(1), closed: false);
            await db.SaveChangesAsync();

            EmployeeUserId = employeeUser.Id;
            ManagerUserId = managerUser.Id;
            ClosedSessionId = closedSession.Id;
            ClosedSessionScheduledStart = closedStart;
            StaleSessionId = staleSession.Id;
        }
    }
}
