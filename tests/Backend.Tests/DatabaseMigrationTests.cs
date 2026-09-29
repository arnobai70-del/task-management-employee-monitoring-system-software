using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Infrastructure;
using TaskMonitoring.Api.Security;
using TaskMonitoring.Api.Services;

namespace Backend.Tests;

public sealed class DatabaseMigrationTests
{
    [Fact]
    public async Task Migrations_apply_to_real_postgres_and_foundation_seed_is_idempotent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(connectionString), "TEST_POSTGRES_CONNECTION is required for migration tests.");

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        await using var db = new AppDbContext(options);
        await db.Database.MigrateAsync(cancellationToken);

        var pendingMigrations = await db.Database.GetPendingMigrationsAsync(cancellationToken);
        Assert.Empty(pendingMigrations);

        var initializer = new DatabaseInitializer(
            db,
            new PasswordHasher<User>(),
            new ConfigurationBuilder().Build(),
            NullLogger<DatabaseInitializer>.Instance);

        await initializer.SeedFoundationAsync(cancellationToken);
        await initializer.SeedFoundationAsync(cancellationToken);

        Assert.Equal(9, await db.Roles.CountAsync(cancellationToken));
        Assert.Equal(PermissionCatalog.Definitions.Count, await db.Permissions.CountAsync(cancellationToken));

        var superAdmin = await db.Roles
            .Include(x => x.RolePermissions)
            .SingleAsync(x => x.Name == "SuperAdmin", cancellationToken);
        Assert.Equal(PermissionCatalog.Definitions.Count, superAdmin.RolePermissions.Count);

        var user = new User
        {
            Email = "migration-test@example.com",
            NormalizedEmail = "MIGRATION-TEST@EXAMPLE.COM",
            PasswordHash = "ci-test-hash"
        };
        var department = new Department
        {
            Code = "QA",
            NormalizedCode = "QA",
            Name = "Quality Assurance",
            NormalizedName = "QUALITY ASSURANCE"
        };
        var employee = new Employee
        {
            UserId = user.Id,
            User = user,
            DepartmentId = department.Id,
            Department = department,
            EmployeeCode = "MIG-001",
            NormalizedEmployeeCode = "MIG-001",
            FullName = "Migration Test Employee",
            NormalizedFullName = "MIGRATION TEST EMPLOYEE",
            JobTitle = "QA Engineer"
        };
        var shift = new Shift
        {
            Code = "MIG-DAY",
            NormalizedCode = "MIG-DAY",
            Name = "Migration Day Shift",
            NormalizedName = "MIGRATION DAY SHIFT",
            StartTime = new TimeOnly(9, 0),
            EndTime = new TimeOnly(17, 0),
            TimeZoneId = "Asia/Dhaka",
            GraceMinutes = 5
        };
        var assignment = new EmployeeShiftAssignment
        {
            EmployeeId = employee.Id,
            Employee = employee,
            ShiftId = shift.Id,
            Shift = shift,
            EffectiveFrom = new DateOnly(2026, 9, 29)
        };
        var workSession = new WorkSession
        {
            EmployeeId = employee.Id,
            Employee = employee,
            ShiftId = shift.Id,
            Shift = shift,
            ShiftAssignmentId = assignment.Id,
            ShiftAssignment = assignment,
            WorkDate = new DateOnly(2026, 9, 29),
            ScheduledStartUtc = DateTime.Parse("2026-09-29T03:00:00Z").ToUniversalTime(),
            ScheduledEndUtc = DateTime.Parse("2026-09-29T11:00:00Z").ToUniversalTime(),
            StartedAtUtc = DateTime.Parse("2026-09-29T03:05:00Z").ToUniversalTime(),
            EndedAtUtc = DateTime.Parse("2026-09-29T11:00:00Z").ToUniversalTime(),
            LateMinutes = 0,
            EarlyLeaveMinutes = 0,
            TotalBreakMinutes = 15
        };
        var workBreak = new WorkBreak
        {
            WorkSessionId = workSession.Id,
            WorkSession = workSession,
            StartedAtUtc = DateTime.Parse("2026-09-29T07:00:00Z").ToUniversalTime(),
            EndedAtUtc = DateTime.Parse("2026-09-29T07:15:00Z").ToUniversalTime(),
            DurationMinutes = 15
        };
        var project = new Project
        {
            Code = "MIG-PRJ",
            NormalizedCode = "MIG-PRJ",
            Name = "Migration Project",
            NormalizedName = "MIGRATION PROJECT",
            Status = ProjectStatus.Active,
            StartDate = new DateOnly(2026, 9, 1),
            DueDate = new DateOnly(2026, 10, 31)
        };
        var projectMember = new ProjectMember
        {
            ProjectId = project.Id,
            Project = project,
            EmployeeId = employee.Id,
            Employee = employee,
            Role = ProjectMemberRole.Manager,
            IsActive = true
        };
        var projectTask = new ProjectTask
        {
            ProjectId = project.Id,
            Project = project,
            Title = "Migration Project Task",
            NormalizedTitle = "MIGRATION PROJECT TASK",
            Status = ProjectTaskStatus.InProgress,
            Priority = ProjectTaskPriority.High,
            AssigneeEmployeeId = employee.Id,
            AssigneeEmployee = employee,
            DueDate = new DateOnly(2026, 10, 15),
            CreatedByUserId = user.Id,
            CreatedByUser = user
        };
        var taskComment = new TaskComment
        {
            ProjectTaskId = projectTask.Id,
            ProjectTask = projectTask,
            AuthorUserId = user.Id,
            AuthorUser = user,
            Body = "Migration comment"
        };
        var taskActivity = new TaskActivity
        {
            ProjectTaskId = projectTask.Id,
            ProjectTask = projectTask,
            ActorUserId = user.Id,
            ActorUser = user,
            Action = "task.created",
            DetailsJson = "{}"
        };
        var surveyForm = new SurveyForm
        {
            ProjectId = project.Id,
            Project = project,
            Code = "MIG-SURVEY",
            NormalizedCode = "MIG-SURVEY",
            Name = "Migration Survey",
            NormalizedName = "MIGRATION SURVEY",
            Status = SurveyFormStatus.Published
        };
        var surveyQuestion = new SurveyQuestion
        {
            SurveyFormId = surveyForm.Id,
            SurveyForm = surveyForm,
            Key = "household_size",
            NormalizedKey = "HOUSEHOLD_SIZE",
            Prompt = "Household size",
            Type = SurveyQuestionType.Number,
            IsRequired = true,
            OptionsJson = "[]",
            SortOrder = 1
        };
        var surveyAssignment = new SurveyAssignment
        {
            SurveyFormId = surveyForm.Id,
            SurveyForm = surveyForm,
            EmployeeId = employee.Id,
            Employee = employee,
            AssignedByUserId = user.Id,
            AssignedByUser = user,
            Status = SurveyAssignmentStatus.Approved,
            DueDate = new DateOnly(2026, 10, 20)
        };
        var surveySubmission = new SurveySubmission
        {
            SurveyAssignmentId = surveyAssignment.Id,
            SurveyAssignment = surveyAssignment,
            RevisionNumber = 1,
            Status = SurveySubmissionStatus.Approved,
            SubmittedAtUtc = DateTime.Parse("2026-09-29T08:00:00Z").ToUniversalTime(),
            ReviewedAtUtc = DateTime.Parse("2026-09-29T08:05:00Z").ToUniversalTime(),
            ReviewedByUserId = user.Id,
            ReviewedByUser = user,
            ReviewComment = "Approved"
        };
        var surveyAnswer = new SurveyAnswer
        {
            SurveySubmissionId = surveySubmission.Id,
            SurveySubmission = surveySubmission,
            SurveyQuestionId = surveyQuestion.Id,
            SurveyQuestion = surveyQuestion,
            ValueJson = "4"
        };

        db.Users.Add(user);
        db.Departments.Add(department);
        db.Employees.Add(employee);
        db.Shifts.Add(shift);
        db.EmployeeShiftAssignments.Add(assignment);
        db.WorkSessions.Add(workSession);
        db.WorkBreaks.Add(workBreak);
        db.Projects.Add(project);
        db.ProjectMembers.Add(projectMember);
        db.ProjectTasks.Add(projectTask);
        db.TaskComments.Add(taskComment);
        db.TaskActivities.Add(taskActivity);
        db.SurveyForms.Add(surveyForm);
        db.SurveyQuestions.Add(surveyQuestion);
        db.SurveyAssignments.Add(surveyAssignment);
        db.SurveySubmissions.Add(surveySubmission);
        db.SurveyAnswers.Add(surveyAnswer);
        await db.SaveChangesAsync(cancellationToken);

        Assert.Equal(1, await db.Users.CountAsync(cancellationToken));
        Assert.Equal(1, await db.Departments.CountAsync(cancellationToken));
        Assert.Equal(1, await db.Employees.CountAsync(cancellationToken));
        Assert.Equal(1, await db.Shifts.CountAsync(cancellationToken));
        Assert.Equal(1, await db.EmployeeShiftAssignments.CountAsync(cancellationToken));
        Assert.Equal(1, await db.WorkSessions.CountAsync(cancellationToken));
        Assert.Equal(1, await db.WorkBreaks.CountAsync(cancellationToken));
        Assert.Equal(1, await db.Projects.CountAsync(cancellationToken));
        Assert.Equal(1, await db.ProjectMembers.CountAsync(cancellationToken));
        Assert.Equal(1, await db.ProjectTasks.CountAsync(cancellationToken));
        Assert.Equal(1, await db.TaskComments.CountAsync(cancellationToken));
        Assert.Equal(1, await db.TaskActivities.CountAsync(cancellationToken));
        Assert.Equal(1, await db.SurveyForms.CountAsync(cancellationToken));
        Assert.Equal(1, await db.SurveyQuestions.CountAsync(cancellationToken));
        Assert.Equal(1, await db.SurveyAssignments.CountAsync(cancellationToken));
        Assert.Equal(1, await db.SurveySubmissions.CountAsync(cancellationToken));
        Assert.Equal(1, await db.SurveyAnswers.CountAsync(cancellationToken));

        var reportingService = new ReportingDashboardService(
            db,
            new FixedTimeProvider(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero)));
        var dashboard = await reportingService.GetDashboardAsync(
            new DateOnly(2026, 9, 29),
            new DateOnly(2026, 9, 29),
            cancellationToken);
        var attendanceReport = await reportingService.GetAttendanceDailyAsync(
            new DateOnly(2026, 9, 29),
            new DateOnly(2026, 9, 29),
            department.Id,
            cancellationToken);
        var projectReport = await reportingService.GetProjectProgressAsync(project.Id, cancellationToken);
        var workloadReport = await reportingService.GetEmployeeWorkloadAsync(department.Id, 20, cancellationToken);
        var surveyReport = await reportingService.GetSurveyProgressAsync(project.Id, cancellationToken);

        Assert.Equal(OperationStatus.Success, dashboard.Status);
        Assert.Equal(1, dashboard.Value!.Attendance.Sessions);
        Assert.Equal(OperationStatus.Success, attendanceReport.Status);
        Assert.Single(attendanceReport.Value!);
        Assert.Equal(OperationStatus.Success, projectReport.Status);
        Assert.Single(projectReport.Value!);
        Assert.Equal(OperationStatus.Success, workloadReport.Status);
        Assert.Single(workloadReport.Value!);
        Assert.Equal(OperationStatus.Success, surveyReport.Status);
        Assert.Single(surveyReport.Value!);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
