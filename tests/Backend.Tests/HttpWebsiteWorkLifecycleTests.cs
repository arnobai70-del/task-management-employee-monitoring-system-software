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
/// Phase 4 Gate: Website Work lifecycle end to end through HTTP (assign, start, open, submit, reopen, approve),
/// durable employee notifications, and the generic task routes that must not bypass the review state machine.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class HttpWebsiteWorkLifecycleTests(HttpWebsiteWorkLifecycleTests.ApiFixture api) : IClassFixture<HttpWebsiteWorkLifecycleTests.ApiFixture>
{
    [Fact]
    public async Task Assigned_work_moves_through_start_submit_reopen_and_approve_with_durable_notifications()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = await api.ClientForAsync(ApiFixture.ManagerEmail, ct);
        var employee = await api.ClientForAsync(ApiFixture.EmployeeAEmail, ct);
        var id = await api.CreateWorkAsync(manager, api.EmployeeAId, "Lifecycle target", ct);

        Assert.Contains(id.ToString(), await employee.GetStringAsync("/api/me/website-work", ct), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("InProgress", await StatusOfAsync(await employee.PostAsync($"/api/me/website-work/{id}/start", null, ct), ct));
        Assert.Equal("InProgress", await StatusOfAsync(await employee.PostAsync($"/api/me/website-work/{id}/start", null, ct), ct));
        Assert.Equal("Blocked", await StatusOfAsync(await employee.PostAsync($"/api/me/website-work/{id}/submit", null, ct), ct));
        Assert.Equal(HttpStatusCode.Conflict, (await employee.PostAsync($"/api/me/website-work/{id}/submit", null, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await employee.PostAsync($"/api/me/website-work/{id}/start", null, ct)).StatusCode);

        var reopenWithoutNote = await manager.PostAsJsonAsync($"/api/website-work/{id}/reopen", new { comment = "" }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, reopenWithoutNote.StatusCode);
        Assert.Equal("InProgress", await StatusOfAsync(await manager.PostAsJsonAsync($"/api/website-work/{id}/reopen", new { comment = "Fix the footer link" }, ct), ct));

        // Durable fallback: an employee who was offline sees the correction note on the next fetch.
        Assert.Contains("Fix the footer link", await employee.GetStringAsync("/api/me/notifications", ct));

        Assert.Equal("InProgress", await StatusOfAsync(await employee.PostAsync($"/api/me/website-work/{id}/start", null, ct), ct));
        Assert.Equal("Blocked", await StatusOfAsync(await employee.PostAsync($"/api/me/website-work/{id}/submit", null, ct), ct));
        Assert.Equal("Done", await StatusOfAsync(await manager.PostAsJsonAsync($"/api/website-work/{id}/approve", new { comment = "Looks good" }, ct), ct));

        Assert.Contains("Approved: Lifecycle target", await employee.GetStringAsync("/api/me/notifications", ct));
        Assert.Equal(HttpStatusCode.Conflict, (await employee.PostAsync($"/api/me/website-work/{id}/start", null, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PostAsJsonAsync($"/api/website-work/{id}/approve", new { comment = "" }, ct)).StatusCode);

        var actions = await api.ActivityActionsAsync(id, ct);
        Assert.Equal(
            new[]
            {
                "website-work.started", "website-work.opened", "website-work.submitted", "website-work.reopened",
                "website-work.opened", "website-work.submitted", "website-work.approved", "website-work.completed"
            },
            actions.Where(x => x.StartsWith("website-work.", StringComparison.Ordinal) && x != WebsiteWorkService.ConfiguredAction).ToArray());
    }

    [Theory]
    [InlineData("Done")]
    [InlineData("Blocked")]
    public async Task Generic_task_status_route_cannot_skip_website_work_review(string target)
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = await api.ClientForAsync(ApiFixture.ManagerEmail, ct);
        var employee = await api.ClientForAsync(ApiFixture.EmployeeAEmail, ct);
        var id = await api.CreateWorkAsync(manager, api.EmployeeAId, $"Bypass {target}", ct);
        Assert.Equal(HttpStatusCode.OK, (await employee.PostAsync($"/api/me/website-work/{id}/start", null, ct)).StatusCode);

        var response = await manager.PutAsJsonAsync($"/api/tasks/{id}/status", new { status = target }, ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("website_work_managed_separately", await response.Content.ReadAsStringAsync(ct));
        Assert.Equal(ProjectTaskStatus.InProgress, await api.StatusInDatabaseAsync(id, ct));
    }

    [Fact]
    public async Task Generic_task_status_route_cannot_reopen_approved_website_work_without_a_correction_note()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = await api.ClientForAsync(ApiFixture.ManagerEmail, ct);
        var employee = await api.ClientForAsync(ApiFixture.EmployeeAEmail, ct);
        var id = await api.CreateWorkAsync(manager, api.EmployeeAId, "Approved then reopened", ct);
        await employee.PostAsync($"/api/me/website-work/{id}/start", null, ct);
        await employee.PostAsync($"/api/me/website-work/{id}/submit", null, ct);
        Assert.Equal(HttpStatusCode.OK, (await manager.PostAsJsonAsync($"/api/website-work/{id}/approve", new { comment = "" }, ct)).StatusCode);

        var response = await manager.PutAsJsonAsync($"/api/tasks/{id}/status", new { status = "InProgress" }, ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ProjectTaskStatus.Done, await api.StatusInDatabaseAsync(id, ct));
    }

    [Fact]
    public async Task Generic_task_status_route_can_cancel_website_work_but_not_restore_it()
    {
        // AdminWeb WebsiteWorkManagement cancels through PUT /api/tasks/{id}/status; that path must keep working.
        // Restoring is rejected; a new assignment is the supported path (TasksController.ChangeStatus).
        var ct = TestContext.Current.CancellationToken;
        var manager = await api.ClientForAsync(ApiFixture.ManagerEmail, ct);
        var id = await api.CreateWorkAsync(manager, api.EmployeeAId, "Cancel target", ct);

        Assert.Equal(HttpStatusCode.OK, (await manager.PutAsJsonAsync($"/api/tasks/{id}/status", new { status = "Cancelled" }, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PutAsJsonAsync($"/api/tasks/{id}/status", new { status = "ToDo" }, ct)).StatusCode);
        Assert.Equal(ProjectTaskStatus.Cancelled, await api.StatusInDatabaseAsync(id, ct));
    }

    [Fact]
    public async Task Generic_task_update_cannot_reassign_started_website_work()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = await api.ClientForAsync(ApiFixture.ManagerEmail, ct);
        var employee = await api.ClientForAsync(ApiFixture.EmployeeAEmail, ct);
        var id = await api.CreateWorkAsync(manager, api.EmployeeAId, "Reassign target", ct);
        Assert.Equal(HttpStatusCode.OK, (await employee.PostAsync($"/api/me/website-work/{id}/start", null, ct)).StatusCode);

        var viaWebsiteWork = await manager.PutAsJsonAsync($"/api/website-work/{id}", api.WorkRequest(api.EmployeeBId, "Reassign target"), ct);
        var viaGenericTask = await manager.PutAsJsonAsync($"/api/tasks/{id}", new
        {
            title = "Reassign target",
            priority = "Normal",
            assigneeEmployeeId = api.EmployeeBId
        }, ct);

        Assert.Equal(HttpStatusCode.Conflict, viaWebsiteWork.StatusCode);
        Assert.Contains("website_work_assignee_locked", await viaWebsiteWork.Content.ReadAsStringAsync(ct));
        Assert.Equal(HttpStatusCode.Conflict, viaGenericTask.StatusCode);
        Assert.Contains("website_work_managed_separately", await viaGenericTask.Content.ReadAsStringAsync(ct));
        await using var scope = api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(api.EmployeeAId, (await db.ProjectTasks.AsNoTracking().SingleAsync(x => x.Id == id, ct)).AssigneeEmployeeId);
    }

    [Fact]
    public async Task Website_work_cannot_be_assigned_to_a_deactivated_employee()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = await api.ClientForAsync(ApiFixture.ManagerEmail, ct);

        var response = await manager.PostAsJsonAsync("/api/website-work", api.WorkRequest(api.DisabledEmployeeId, "Inactive target"), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("employee_inactive", await response.Content.ReadAsStringAsync(ct));
    }

    private static async Task<string> StatusOfAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Expected 200 but got {(int)response.StatusCode}: {body}");
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("status").GetString()!;
    }

    public sealed class ApiFixture : IAsyncLifetime
    {
        public const string Password = "Lifecycle-Test-Password-2026!";
        public const string EmployeeAEmail = "lifecycle-a@lifecycle.test";
        public const string EmployeeBEmail = "lifecycle-b@lifecycle.test";
        public const string ManagerEmail = "lifecycle-manager@lifecycle.test";

        private string _connectionString = string.Empty;
        private readonly Dictionary<string, string?> _previousEnvironment = new();

        public WebApplicationFactory<Program> Factory { get; private set; } = null!;
        public Guid ProjectId { get; private set; }
        public Guid EmployeeAId { get; private set; }
        public Guid EmployeeBId { get; private set; }
        public Guid DisabledEmployeeId { get; private set; }

        public async ValueTask InitializeAsync()
        {
            var baseConnection = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
            Assert.False(string.IsNullOrWhiteSpace(baseConnection), "TEST_POSTGRES_CONNECTION is required for HTTP lifecycle tests.");
            _connectionString = new NpgsqlConnectionStringBuilder(baseConnection)
            {
                Database = $"tm_website_lifecycle_{Guid.NewGuid():N}"
            }.ConnectionString;

            SetEnvironment("ConnectionStrings__DefaultConnection", _connectionString);
            SetEnvironment("Jwt__SigningKey", "http-website-work-lifecycle-test-signing-key-0123456789");
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

        private readonly Dictionary<string, HttpClient> _clients = new();

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

        public object WorkRequest(Guid employeeId, string title) => new
        {
            projectId = ProjectId,
            employeeId,
            title,
            url = "https://example.com/lifecycle",
            priority = "Normal"
        };

        public async Task<Guid> CreateWorkAsync(HttpClient manager, Guid employeeId, string title, CancellationToken ct)
        {
            var response = await manager.PostAsJsonAsync("/api/website-work", WorkRequest(employeeId, title), ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            Assert.True(response.IsSuccessStatusCode, $"Create returned {(int)response.StatusCode}: {body}");
            using var json = JsonDocument.Parse(body);
            return json.RootElement.GetProperty("id").GetGuid();
        }

        public async Task<ProjectTaskStatus> StatusInDatabaseAsync(Guid id, CancellationToken ct)
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return (await db.ProjectTasks.AsNoTracking().SingleAsync(x => x.Id == id, ct)).Status;
        }

        public async Task<string[]> ActivityActionsAsync(Guid id, CancellationToken ct)
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.TaskActivities.AsNoTracking()
                .Where(x => x.ProjectTaskId == id)
                .OrderBy(x => x.CreatedAtUtc)
                .Select(x => x.Action)
                .ToArrayAsync(ct);
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
            var managerRole = new Role { Name = "LifecycleTaskManager" };
            foreach (var permission in await db.Permissions
                         .Where(x => x.Code == PermissionCatalog.TasksRead || x.Code == PermissionCatalog.TasksManage ||
                             x.Code == PermissionCatalog.EmployeesScopeAll) // organisation-wide, as pre-existing roles are after the G-27 migration
                         .ToListAsync())
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

            var project = new Project
            {
                Code = "LC-WEB",
                NormalizedCode = "LC-WEB",
                Name = "Lifecycle Website Work",
                NormalizedName = "LIFECYCLE WEBSITE WORK",
                Status = ProjectStatus.Active
            };
            db.Projects.Add(project);

            Employee AddEmployee(User user, string code)
            {
                var employee = new Employee
                {
                    UserId = user.Id,
                    User = user,
                    EmployeeCode = code,
                    NormalizedEmployeeCode = code,
                    FullName = $"Lifecycle {code}",
                    NormalizedFullName = $"LIFECYCLE {code}",
                    JobTitle = "Worker",
                    IsActive = user.IsActive
                };
                db.Employees.Add(employee);
                db.ProjectMembers.Add(new ProjectMember { ProjectId = project.Id, Project = project, EmployeeId = employee.Id, Employee = employee });
                return employee;
            }

            var a = AddEmployee(AddUser(EmployeeAEmail, employeeRole), "LC-A");
            var b = AddEmployee(AddUser(EmployeeBEmail, employeeRole), "LC-B");
            var disabled = AddEmployee(AddUser("lifecycle-off@lifecycle.test", employeeRole, active: false), "LC-OFF");
            AddUser(ManagerEmail, managerRole);
            await db.SaveChangesAsync();

            ProjectId = project.Id;
            EmployeeAId = a.Id;
            EmployeeBId = b.Id;
            DisabledEmployeeId = disabled.Id;
        }
    }
}
