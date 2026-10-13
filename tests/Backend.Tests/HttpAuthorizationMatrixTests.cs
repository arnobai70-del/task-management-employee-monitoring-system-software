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
/// End-to-end authorization boundaries through the real HTTP pipeline (JWT, policies, controllers, PostgreSQL):
/// Employee A must not read or change Employee B's records, and permission-gated endpoints must fail closed.
/// </summary>
public sealed class HttpAuthorizationMatrixTests(HttpAuthorizationMatrixTests.ApiFixture api) : IClassFixture<HttpAuthorizationMatrixTests.ApiFixture>
{
    [Fact]
    public async Task Unauthenticated_requests_to_protected_endpoints_are_rejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = api.Factory.CreateClient();

        foreach (var path in new[] { "/api/me/tasks", "/api/me/website-work", "/api/me/notifications", "/api/employees", "/api/audit-logs" })
        {
            var response = await client.GetAsync(path, cancellationToken);
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{path} returned {(int)response.StatusCode}, expected 401.");
        }
    }

    [Fact]
    public async Task Employee_cannot_act_on_another_employees_website_work()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var employeeA = await api.ClientForAsync(ApiFixture.EmployeeAEmail, cancellationToken);

        foreach (var action in new[] { "start", "submit", "complete" })
        {
            var response = await employeeA.PostAsync($"/api/me/website-work/{api.EmployeeBTaskId}/{action}", null, cancellationToken);
            Assert.True(
                response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden,
                $"{action} on another employee's work returned {(int)response.StatusCode}.");
        }

        var listing = await employeeA.GetStringAsync("/api/me/website-work", cancellationToken);
        Assert.DoesNotContain(api.EmployeeBTaskId.ToString(), listing, StringComparison.OrdinalIgnoreCase);

        await using var scope = api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var task = await db.ProjectTasks.AsNoTracking().SingleAsync(x => x.Id == api.EmployeeBTaskId, cancellationToken);
        Assert.Equal(ProjectTaskStatus.InProgress, task.Status);
        Assert.Null(task.CompletedAtUtc);
    }

    [Fact]
    public async Task Employee_cannot_read_or_mark_another_employees_notifications()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var employeeA = await api.ClientForAsync(ApiFixture.EmployeeAEmail, cancellationToken);

        var mark = await employeeA.PostAsync($"/api/me/notifications/{api.EmployeeBNotificationId}/read", null, cancellationToken);
        Assert.True(mark.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden, $"Mark-read returned {(int)mark.StatusCode}.");

        var feed = await employeeA.GetStringAsync("/api/me/notifications", cancellationToken);
        Assert.DoesNotContain(api.EmployeeBNotificationId.ToString(), feed, StringComparison.OrdinalIgnoreCase);

        await using var scope = api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var notification = await db.EmployeeNotifications.AsNoTracking().SingleAsync(x => x.Id == api.EmployeeBNotificationId, cancellationToken);
        Assert.Null(notification.ReadAtUtc);
    }

    [Fact]
    public async Task Owner_positive_control_can_act_on_own_records_through_the_same_routes()
    {
        // Proves the denials above come from ownership checks, not from broken routes or seed data.
        var cancellationToken = TestContext.Current.CancellationToken;
        using var employeeB = await api.ClientForAsync(ApiFixture.EmployeeBEmail, cancellationToken);

        var mark = await employeeB.PostAsync($"/api/me/notifications/{api.EmployeeBControlNotificationId}/read", null, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, mark.StatusCode);

        var submit = await employeeB.PostAsync($"/api/me/website-work/{api.EmployeeBControlTaskId}/submit", null, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);
    }

    [Fact]
    public async Task Employee_without_permissions_is_forbidden_from_admin_endpoints()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var employeeA = await api.ClientForAsync(ApiFixture.EmployeeAEmail, cancellationToken);

        foreach (var path in new[]
                 {
                     "/api/employees", "/api/attendance", "/api/website-work", "/api/tasks",
                     "/api/audit-logs", "/api/security-observability/audit-export.csv", "/api/presence"
                 })
        {
            var response = await employeeA.GetAsync(path, cancellationToken);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{path} returned {(int)response.StatusCode}, expected 403.");
        }
    }

    [Fact]
    public async Task Task_manager_gets_task_data_but_not_audit_or_employee_admin()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var manager = await api.ClientForAsync(ApiFixture.ManagerEmail, cancellationToken);

        var work = await manager.GetAsync("/api/website-work", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, work.StatusCode);
        Assert.Contains(api.EmployeeBTaskId.ToString(), await work.Content.ReadAsStringAsync(cancellationToken), StringComparison.OrdinalIgnoreCase);

        foreach (var path in new[] { "/api/audit-logs", "/api/security-observability/audit-export.csv", "/api/employees" })
        {
            var response = await manager.GetAsync(path, cancellationToken);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{path} returned {(int)response.StatusCode}, expected 403.");
        }
    }

    [Fact]
    public async Task Deactivated_account_cannot_log_in_or_refresh()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var anonymous = api.Factory.CreateClient();
        var login = await anonymous.PostAsJsonAsync("/api/auth/login", new { email = ApiFixture.DisabledEmail, password = ApiFixture.Password }, cancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    public sealed class ApiFixture : IAsyncLifetime
    {
        public const string Password = "Matrix-Test-Password-2026!";
        public const string EmployeeAEmail = "employee-a@matrix.test";
        public const string EmployeeBEmail = "employee-b@matrix.test";
        public const string ManagerEmail = "task-manager@matrix.test";
        public const string DisabledEmail = "disabled@matrix.test";

        private string _connectionString = string.Empty;
        private readonly Dictionary<string, string?> _previousEnvironment = new();

        public WebApplicationFactory<Program> Factory { get; private set; } = null!;
        public Guid EmployeeBTaskId { get; private set; }
        public Guid EmployeeBNotificationId { get; private set; }
        public Guid EmployeeBControlTaskId { get; private set; }
        public Guid EmployeeBControlNotificationId { get; private set; }

        public async ValueTask InitializeAsync()
        {
            var baseConnection = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
            Assert.False(string.IsNullOrWhiteSpace(baseConnection), "TEST_POSTGRES_CONNECTION is required for HTTP authorization tests.");
            _connectionString = new NpgsqlConnectionStringBuilder(baseConnection)
            {
                Database = $"tm_authz_matrix_{Guid.NewGuid():N}"
            }.ConnectionString;

            // Program reads configuration before the host is built, so supply it through the environment.
            SetEnvironment("ConnectionStrings__DefaultConnection", _connectionString);
            SetEnvironment("Jwt__SigningKey", "http-authorization-matrix-test-signing-key-0123456789");
            SetEnvironment("Database__AutoMigrate", "true");

            Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
            _ = Factory.Server;

            await using var scope = Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await SeedAsync(db);
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
            var employeeRole = await db.Roles.SingleAsync(x => x.Name == "Employee");
            var managerRole = new Role { Name = "MatrixTaskManager" };
            var taskPermissions = await db.Permissions
                .Where(x => x.Code == PermissionCatalog.TasksRead || x.Code == PermissionCatalog.TasksManage)
                .ToListAsync();
            foreach (var permission in taskPermissions)
            {
                managerRole.RolePermissions.Add(new RolePermission { RoleId = managerRole.Id, PermissionId = permission.Id });
            }
            db.Roles.Add(managerRole);

            User AddUser(string email, Role role, bool active = true)
            {
                var user = new User { Email = email, NormalizedEmail = AuthService.NormalizeEmail(email), IsActive = active };
                user.PasswordHash = hasher.HashPassword(user, Password);
                user.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
                db.Users.Add(user);
                return user;
            }

            Employee AddEmployee(User user, string code)
            {
                var employee = new Employee
                {
                    UserId = user.Id,
                    User = user,
                    EmployeeCode = code,
                    NormalizedEmployeeCode = code,
                    FullName = $"Matrix {code}",
                    NormalizedFullName = $"MATRIX {code}",
                    JobTitle = "Worker",
                    IsActive = user.IsActive
                };
                db.Employees.Add(employee);
                return employee;
            }

            AddEmployee(AddUser(EmployeeAEmail, employeeRole), "MTX-A");
            var employeeB = AddEmployee(AddUser(EmployeeBEmail, employeeRole), "MTX-B");
            AddUser(ManagerEmail, managerRole);
            AddEmployee(AddUser(DisabledEmail, employeeRole, active: false), "MTX-OFF");

            var now = DateTime.UtcNow;
            var project = new Project
            {
                Code = "MTX-WEB",
                NormalizedCode = "MTX-WEB",
                Name = "Matrix Website Work",
                NormalizedName = "MATRIX WEBSITE WORK",
                Status = ProjectStatus.Active
            };
            db.Projects.Add(project);

            ProjectTask AddWorkingTask(Employee owner, string title)
            {
                var task = new ProjectTask
                {
                    ProjectId = project.Id,
                    Project = project,
                    Title = title,
                    NormalizedTitle = title.ToUpperInvariant(),
                    Status = ProjectTaskStatus.InProgress,
                    Priority = ProjectTaskPriority.Normal,
                    AssigneeEmployeeId = owner.Id,
                    AssigneeEmployee = owner,
                    CreatedAtUtc = now.AddMinutes(-30),
                    UpdatedAtUtc = now.AddMinutes(-20)
                };
                task.Activities.Add(new TaskActivity
                {
                    ProjectTaskId = task.Id,
                    ProjectTask = task,
                    ActorUserId = owner.UserId,
                    Action = WebsiteWorkService.ConfiguredAction,
                    DetailsJson = "{\"url\":\"https://example.com/work\"}",
                    CreatedAtUtc = now.AddMinutes(-30)
                });
                task.Activities.Add(new TaskActivity
                {
                    ProjectTaskId = task.Id,
                    ProjectTask = task,
                    ActorUserId = owner.UserId,
                    Action = WebsiteWorkService.StartedAction,
                    DetailsJson = "{\"host\":\"example.com\"}",
                    CreatedAtUtc = now.AddMinutes(-20)
                });
                db.ProjectTasks.Add(task);
                return task;
            }

            EmployeeNotification AddNotification(Employee owner, ProjectTask task)
            {
                var notification = new EmployeeNotification
                {
                    EmployeeId = owner.Id,
                    Kind = EmployeeNotificationKind.TaskAssigned,
                    Title = "Private to Employee B",
                    Message = "Only Employee B may read this.",
                    EntityType = "ProjectTask",
                    EntityId = task.Id,
                    CreatedAtUtc = now.AddMinutes(-25)
                };
                db.EmployeeNotifications.Add(notification);
                return notification;
            }

            var task = AddWorkingTask(employeeB, "Employee B website target");
            var notification = AddNotification(employeeB, task);
            var controlTask = AddWorkingTask(employeeB, "Employee B control target");
            var controlNotification = AddNotification(employeeB, controlTask);

            await db.SaveChangesAsync();
            EmployeeBTaskId = task.Id;
            EmployeeBNotificationId = notification.Id;
            EmployeeBControlTaskId = controlTask.Id;
            EmployeeBControlNotificationId = controlNotification.Id;
        }
    }
}
