using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Security;
using TaskMonitoring.Api.Services;

namespace Backend.Tests;

/// <summary>
/// G-27 through the real HTTP pipeline and PostgreSQL: a lead without employees.scope.all sees only their own
/// reporting line (direct and indirect reports) in employee-scoped views; a lead with it sees everyone; and the
/// data migration grants the permission to roles that already existed, so upgrading changes nothing.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class HttpTeamScopeTests(HttpTeamScopeTests.ApiFixture api) : IClassFixture<HttpTeamScopeTests.ApiFixture>
{
    private static readonly string Today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
    private static readonly string WeekAgo = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-6)).ToString("yyyy-MM-dd");

    private static string[] ListPaths => [
        "/api/employees?page=1&pageSize=100",
        $"/api/attendance?from={WeekAgo}&to={Today}&page=1&pageSize=100",
        "/api/presence?page=1&pageSize=100",
        "/api/monitoring/activity?page=1&pageSize=100",
        "/api/website-work?page=1&pageSize=100",
        "/api/website-work/progress",
        $"/api/reports/website-work/productivity?from={WeekAgo}&to={Today}"
    ];

    [Fact]
    public async Task Scoped_lead_sees_only_own_reporting_line()
    {
        var ct = TestContext.Current.CancellationToken;
        using var lead = await api.ClientForAsync(ApiFixture.ScopedLeadEmail, ct);

        // Collect every problem first so a failure names all leaking views, not just the first.
        var problems = new List<string>();
        foreach (var path in ListPaths)
        {
            var response = await lead.GetAsync(path, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode != HttpStatusCode.OK) problems.Add($"{path} returned {(int)response.StatusCode}");
            if (body.Contains("SCOPE-OUT", StringComparison.Ordinal)) problems.Add($"{path} shows the outsider");
            // The indirect report (reports to the direct report) is in scope.
            if (!body.Contains("SCOPE-R2", StringComparison.Ordinal)) problems.Add($"{path} is missing the indirect report");
        }

        foreach (var (path, expected) in new[]
                 {
                     ($"/api/employees/{api.OutsiderId}", HttpStatusCode.NotFound),
                     ($"/api/employees/{api.IndirectReportId}", HttpStatusCode.OK),
                     ($"/api/reports/website-work/productivity/{api.OutsiderId}/timeline", HttpStatusCode.NotFound),
                     ($"/api/reports/website-work/productivity/{api.IndirectReportId}/timeline", HttpStatusCode.OK)
                 })
        {
            var status = (await lead.GetAsync(path, ct)).StatusCode;
            if (status != expected) problems.Add($"{path} returned {(int)status}, expected {(int)expected}");
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));

        var employees = await lead.GetStringAsync("/api/employees?page=1&pageSize=100", ct);
        foreach (var code in new[] { "SCOPE-LEAD", "SCOPE-R1", "SCOPE-R2" })
        {
            Assert.Contains(code, employees, StringComparison.Ordinal);
        }

    }

    [Fact]
    public async Task Lead_with_scope_all_sees_everyone()
    {
        var ct = TestContext.Current.CancellationToken;
        using var lead = await api.ClientForAsync(ApiFixture.AllLeadEmail, ct);

        foreach (var path in ListPaths)
        {
            var body = await lead.GetStringAsync(path, ct);
            Assert.True(body.Contains("SCOPE-OUT", StringComparison.Ordinal), $"{path} should include the outsider for a scope.all lead");
        }

        Assert.Equal(HttpStatusCode.OK, (await lead.GetAsync($"/api/employees/{api.OutsiderId}", ct)).StatusCode);
    }

    [Fact]
    public async Task Upgrade_grants_scope_all_to_roles_that_could_already_read_employee_data()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION"))
        {
            Database = $"tm_scope_upgrade_{Guid.NewGuid():N}"
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options;
        try
        {
            await using (var db = new AppDbContext(options))
            {
                await db.GetService<IMigrator>().MigrateAsync("20261013233309_AttendanceClosureAndCorrection", ct);
                var read = new Permission { Code = PermissionCatalog.AttendanceRead, Description = "read" };
                var submit = new Permission { Code = PermissionCatalog.SurveySubmit, Description = "submit" };
                var reader = new Role { Name = "ExistingReader" };
                var surveyor = new Role { Name = "ExistingSurveyor" };
                reader.RolePermissions.Add(new RolePermission { RoleId = reader.Id, PermissionId = read.Id });
                surveyor.RolePermissions.Add(new RolePermission { RoleId = surveyor.Id, PermissionId = submit.Id });
                db.AddRange(read, submit, reader, surveyor);
                await db.SaveChangesAsync(ct);
            }

            await using (var db = new AppDbContext(options))
            {
                await db.Database.MigrateAsync(ct);
                var granted = await db.Roles.Where(r => r.RolePermissions.Any(rp => rp.Permission.Code == PermissionCatalog.EmployeesScopeAll))
                    .Select(r => r.Name).ToListAsync(ct);
                Assert.Equal(["ExistingReader"], granted);
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var cleanup = new AppDbContext(options);
            await cleanup.Database.EnsureDeletedAsync(ct);
        }
    }

    public sealed class ApiFixture : IAsyncLifetime
    {
        public const string Password = "Scope-Test-Password-2026!";
        public const string ScopedLeadEmail = "scoped-lead@scope.test";
        public const string AllLeadEmail = "all-lead@scope.test";

        private string _connectionString = string.Empty;
        private readonly Dictionary<string, string?> _previousEnvironment = new();

        public WebApplicationFactory<Program> Factory { get; private set; } = null!;
        public Guid OutsiderId { get; private set; }
        public Guid IndirectReportId { get; private set; }

        public async ValueTask InitializeAsync()
        {
            var baseConnection = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
            Assert.False(string.IsNullOrWhiteSpace(baseConnection), "TEST_POSTGRES_CONNECTION is required for team scope tests.");
            _connectionString = new NpgsqlConnectionStringBuilder(baseConnection)
            {
                Database = $"tm_team_scope_{Guid.NewGuid():N}"
            }.ConnectionString;

            SetEnvironment("ConnectionStrings__DefaultConnection", _connectionString);
            SetEnvironment("Jwt__SigningKey", "http-team-scope-test-signing-key-0123456789abcdef");
            SetEnvironment("Database__AutoMigrate", "true");

            Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
            _ = Factory.Server;

            await using var scope = Factory.Services.CreateAsyncScope();
            await SeedAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>());
        }

        public async ValueTask DisposeAsync()
        {
            await Factory.DisposeAsync();
            foreach (var (key, value) in _previousEnvironment)
            {
                Environment.SetEnvironmentVariable(key, value);
            }

            NpgsqlConnection.ClearAllPools();
            await using var cleanup = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_connectionString).Options);
            await cleanup.Database.EnsureDeletedAsync();
        }

        public async Task<HttpClient> ClientForAsync(string email, CancellationToken cancellationToken)
        {
            var client = Factory.CreateClient();
            var response = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password }, cancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.RootElement.GetProperty("accessToken").GetString());
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
            var readCodes = new[]
            {
                PermissionCatalog.EmployeesRead, PermissionCatalog.AttendanceRead, PermissionCatalog.PresenceRead,
                PermissionCatalog.MonitoringRead, PermissionCatalog.TasksRead, PermissionCatalog.ReportsRead
            };
            var readPermissions = await db.Permissions.Where(x => readCodes.Contains(x.Code)).ToListAsync();
            var scopeAll = await db.Permissions.SingleAsync(x => x.Code == PermissionCatalog.EmployeesScopeAll);

            Role AddRole(string name, bool withScopeAll)
            {
                var role = new Role { Name = name };
                foreach (var permission in withScopeAll ? readPermissions.Append(scopeAll) : readPermissions)
                {
                    role.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = permission.Id });
                }
                db.Roles.Add(role);
                return role;
            }

            var scopedRole = AddRole("ScopeTestLead", withScopeAll: false);
            var allRole = AddRole("ScopeTestAllLead", withScopeAll: true);
            var employeeRole = await db.Roles.SingleAsync(x => x.Name == "Employee");

            Employee AddEmployee(string email, Role role, string code, Employee? supervisor)
            {
                var user = new User { Email = email, NormalizedEmail = AuthService.NormalizeEmail(email), IsActive = true };
                user.PasswordHash = hasher.HashPassword(user, Password);
                user.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
                db.Users.Add(user);
                var employee = new Employee
                {
                    UserId = user.Id,
                    User = user,
                    EmployeeCode = code,
                    NormalizedEmployeeCode = code,
                    FullName = $"Scope {code}",
                    NormalizedFullName = $"SCOPE {code}",
                    JobTitle = "Worker",
                    IsActive = true,
                    SupervisorEmployeeId = supervisor?.Id,
                    Supervisor = supervisor
                };
                db.Employees.Add(employee);
                return employee;
            }

            var lead = AddEmployee(ScopedLeadEmail, scopedRole, "SCOPE-LEAD", null);
            var report1 = AddEmployee("r1@scope.test", employeeRole, "SCOPE-R1", lead);
            var report2 = AddEmployee("r2@scope.test", employeeRole, "SCOPE-R2", report1);
            var outsider = AddEmployee("out@scope.test", employeeRole, "SCOPE-OUT", null);
            AddEmployee(AllLeadEmail, allRole, "SCOPE-ALL", null);

            var shift = new Shift
            {
                Code = "SCOPE-DAY", NormalizedCode = "SCOPE-DAY", Name = "Scope Day", NormalizedName = "SCOPE DAY",
                StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(17, 0), TimeZoneId = "UTC", GraceMinutes = 0, IsActive = true
            };
            db.Shifts.Add(shift);
            var now = DateTime.UtcNow;
            var project = new Project
            {
                Code = "SCOPE-WEB", NormalizedCode = "SCOPE-WEB", Name = "Scope Website Work", NormalizedName = "SCOPE WEBSITE WORK",
                Status = ProjectStatus.Active
            };
            db.Projects.Add(project);

            foreach (var employee in new[] { report2, outsider })
            {
                var assignment = new EmployeeShiftAssignment
                {
                    EmployeeId = employee.Id, Employee = employee, ShiftId = shift.Id, Shift = shift, EffectiveFrom = new DateOnly(2026, 1, 1)
                };
                db.EmployeeShiftAssignments.Add(assignment);
                var start = now.Date.AddDays(-1).AddHours(9);
                db.WorkSessions.Add(new WorkSession
                {
                    EmployeeId = employee.Id, Employee = employee, ShiftId = shift.Id, Shift = shift,
                    ShiftAssignmentId = assignment.Id, ShiftAssignment = assignment,
                    WorkDate = DateOnly.FromDateTime(start), ScheduledStartUtc = start, ScheduledEndUtc = start.AddHours(8),
                    StartedAtUtc = start, EndedAtUtc = start.AddHours(8), EarlyLeaveMinutes = 0, ClosureSource = WorkSessionClosureSource.Employee
                });

                var task = new ProjectTask
                {
                    ProjectId = project.Id, Project = project, Title = $"{employee.EmployeeCode} website target",
                    NormalizedTitle = $"{employee.EmployeeCode} WEBSITE TARGET", Status = ProjectTaskStatus.InProgress,
                    Priority = ProjectTaskPriority.Normal, AssigneeEmployeeId = employee.Id, AssigneeEmployee = employee,
                    CreatedAtUtc = now.AddMinutes(-30), UpdatedAtUtc = now.AddMinutes(-20)
                };
                task.Activities.Add(new TaskActivity
                {
                    ProjectTaskId = task.Id, ProjectTask = task, ActorUserId = employee.UserId, Action = WebsiteWorkService.ConfiguredAction,
                    DetailsJson = "{\"url\":\"https://example.com/work\"}", CreatedAtUtc = now.AddMinutes(-30)
                });
                task.Activities.Add(new TaskActivity
                {
                    ProjectTaskId = task.Id, ProjectTask = task, ActorUserId = employee.UserId, Action = WebsiteWorkService.StartedAction,
                    DetailsJson = "{\"host\":\"example.com\"}", CreatedAtUtc = now.AddMinutes(-20)
                });
                db.ProjectTasks.Add(task);
                db.MonitoringActivitySegments.Add(new MonitoringActivitySegment
                {
                    EmployeeId = employee.Id, Employee = employee, Kind = MonitoringActivityKind.Application,
                    ProcessName = "excel.exe", ApplicationName = "Fictional Spreadsheet",
                    StartedAtUtc = now.AddHours(-2), LastObservedAtUtc = now.AddHours(-1)
                });
            }

            await db.SaveChangesAsync();
            OutsiderId = outsider.Id;
            IndirectReportId = report2.Id;
        }
    }
}
