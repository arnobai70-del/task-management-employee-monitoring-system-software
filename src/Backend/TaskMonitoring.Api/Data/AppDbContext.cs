using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Domain;

namespace TaskMonitoring.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<Shift> Shifts => Set<Shift>();
    public DbSet<EmployeeShiftAssignment> EmployeeShiftAssignments => Set<EmployeeShiftAssignment>();
    public DbSet<WorkSession> WorkSessions => Set<WorkSession>();
    public DbSet<WorkBreak> WorkBreaks => Set<WorkBreak>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectMember> ProjectMembers => Set<ProjectMember>();
    public DbSet<ProjectTask> ProjectTasks => Set<ProjectTask>();
    public DbSet<TaskComment> TaskComments => Set<TaskComment>();
    public DbSet<TaskActivity> TaskActivities => Set<TaskActivity>();
    public DbSet<SurveyForm> SurveyForms => Set<SurveyForm>();
    public DbSet<SurveyQuestion> SurveyQuestions => Set<SurveyQuestion>();
    public DbSet<SurveyAssignment> SurveyAssignments => Set<SurveyAssignment>();
    public DbSet<SurveySubmission> SurveySubmissions => Set<SurveySubmission>();
    public DbSet<SurveyAnswer> SurveyAnswers => Set<SurveyAnswer>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("users");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Email).HasMaxLength(320).IsRequired();
            entity.Property(x => x.NormalizedEmail).HasMaxLength(320).IsRequired();
            entity.Property(x => x.PasswordHash).HasMaxLength(1024).IsRequired();
            entity.HasIndex(x => x.NormalizedEmail).IsUnique();
        });

        modelBuilder.Entity<Role>(entity =>
        {
            entity.ToTable("roles");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(100).IsRequired();
            entity.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<Permission>(entity =>
        {
            entity.ToTable("permissions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Code).HasMaxLength(150).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(500).IsRequired();
            entity.HasIndex(x => x.Code).IsUnique();
        });

        modelBuilder.Entity<UserRole>(entity =>
        {
            entity.ToTable("user_roles");
            entity.HasKey(x => new { x.UserId, x.RoleId });
            entity.HasOne(x => x.User).WithMany(x => x.UserRoles).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Role).WithMany(x => x.UserRoles).HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RolePermission>(entity =>
        {
            entity.ToTable("role_permissions");
            entity.HasKey(x => new { x.RoleId, x.PermissionId });
            entity.HasOne(x => x.Role).WithMany(x => x.RolePermissions).HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Permission).WithMany(x => x.RolePermissions).HasForeignKey(x => x.PermissionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.ToTable("refresh_tokens");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.CreatedByIp).HasMaxLength(64);
            entity.Property(x => x.RevokedByIp).HasMaxLength(64);
            entity.Property(x => x.RevokeReason).HasMaxLength(200);
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.HasIndex(x => new { x.UserId, x.ExpiresAtUtc });
            entity.HasOne(x => x.User).WithMany(x => x.RefreshTokens).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.ToTable("audit_logs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Action).HasMaxLength(150).IsRequired();
            entity.Property(x => x.TargetType).HasMaxLength(100).IsRequired();
            entity.Property(x => x.TargetId).HasMaxLength(100);
            entity.Property(x => x.IpAddress).HasMaxLength(64);
            entity.Property(x => x.UserAgent).HasMaxLength(512);
            entity.HasIndex(x => x.CreatedAtUtc);
            entity.HasIndex(x => new { x.ActorUserId, x.CreatedAtUtc });
        });

        modelBuilder.Entity<Department>(entity =>
        {
            entity.ToTable("departments");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Code).HasMaxLength(50).IsRequired();
            entity.Property(x => x.NormalizedCode).HasMaxLength(50).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(150).IsRequired();
            entity.Property(x => x.NormalizedName).HasMaxLength(150).IsRequired();
            entity.HasIndex(x => x.NormalizedCode).IsUnique();
            entity.HasIndex(x => x.NormalizedName).IsUnique();
        });

        modelBuilder.Entity<Employee>(entity =>
        {
            entity.ToTable("employees");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EmployeeCode).HasMaxLength(50).IsRequired();
            entity.Property(x => x.NormalizedEmployeeCode).HasMaxLength(50).IsRequired();
            entity.Property(x => x.FullName).HasMaxLength(200).IsRequired();
            entity.Property(x => x.NormalizedFullName).HasMaxLength(200).IsRequired();
            entity.Property(x => x.JobTitle).HasMaxLength(150).IsRequired();
            entity.Property(x => x.Phone).HasMaxLength(50);
            entity.Property(x => x.EmploymentType).HasConversion<string>().HasMaxLength(30).IsRequired();
            entity.HasIndex(x => x.UserId).IsUnique();
            entity.HasIndex(x => x.NormalizedEmployeeCode).IsUnique();
            entity.HasIndex(x => x.NormalizedFullName);
            entity.HasIndex(x => x.DepartmentId);
            entity.HasIndex(x => x.SupervisorEmployeeId);
            entity.HasOne(x => x.User).WithOne(x => x.Employee).HasForeignKey<Employee>(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Department).WithMany(x => x.Employees).HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Supervisor).WithMany(x => x.DirectReports).HasForeignKey(x => x.SupervisorEmployeeId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Shift>(entity =>
        {
            entity.ToTable("shifts");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Code).HasMaxLength(50).IsRequired();
            entity.Property(x => x.NormalizedCode).HasMaxLength(50).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(150).IsRequired();
            entity.Property(x => x.NormalizedName).HasMaxLength(150).IsRequired();
            entity.Property(x => x.TimeZoneId).HasMaxLength(100).IsRequired();
            entity.HasIndex(x => x.NormalizedCode).IsUnique();
            entity.HasIndex(x => x.NormalizedName).IsUnique();
        });

        modelBuilder.Entity<EmployeeShiftAssignment>(entity =>
        {
            entity.ToTable("employee_shift_assignments");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.EmployeeId, x.EffectiveFrom });
            entity.HasIndex(x => x.ShiftId);
            entity.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Shift).WithMany(x => x.Assignments).HasForeignKey(x => x.ShiftId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<WorkSession>(entity =>
        {
            entity.ToTable("work_sessions");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.EmployeeId, x.WorkDate }).IsUnique();
            entity.HasIndex(x => new { x.WorkDate, x.StartedAtUtc });
            entity.HasIndex(x => x.ShiftId);
            entity.HasIndex(x => x.ShiftAssignmentId);
            entity.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Shift).WithMany(x => x.WorkSessions).HasForeignKey(x => x.ShiftId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.ShiftAssignment).WithMany(x => x.WorkSessions).HasForeignKey(x => x.ShiftAssignmentId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<WorkBreak>(entity =>
        {
            entity.ToTable("work_breaks");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.WorkSessionId, x.StartedAtUtc });
            entity.HasIndex(x => x.WorkSessionId).HasFilter("\"EndedAtUtc\" IS NULL").IsUnique();
            entity.HasOne(x => x.WorkSession).WithMany(x => x.Breaks).HasForeignKey(x => x.WorkSessionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Project>(entity =>
        {
            entity.ToTable("projects");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Code).HasMaxLength(50).IsRequired();
            entity.Property(x => x.NormalizedCode).HasMaxLength(50).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.NormalizedName).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(4000);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
            entity.HasIndex(x => x.NormalizedCode).IsUnique();
            entity.HasIndex(x => x.NormalizedName);
            entity.HasIndex(x => x.Status);
        });

        modelBuilder.Entity<ProjectMember>(entity =>
        {
            entity.ToTable("project_members");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Role).HasConversion<string>().HasMaxLength(30).IsRequired();
            entity.HasIndex(x => new { x.ProjectId, x.EmployeeId }).IsUnique();
            entity.HasIndex(x => new { x.ProjectId, x.IsActive });
            entity.HasOne(x => x.Project).WithMany(x => x.Members).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ProjectTask>(entity =>
        {
            entity.ToTable("project_tasks");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Title).HasMaxLength(200).IsRequired();
            entity.Property(x => x.NormalizedTitle).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(4000);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
            entity.Property(x => x.Priority).HasConversion<string>().HasMaxLength(30).IsRequired();
            entity.HasIndex(x => new { x.ProjectId, x.Status });
            entity.HasIndex(x => new { x.AssigneeEmployeeId, x.Status });
            entity.HasIndex(x => x.DueDate);
            entity.HasIndex(x => x.NormalizedTitle);
            entity.HasOne(x => x.Project).WithMany(x => x.Tasks).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.AssigneeEmployee).WithMany().HasForeignKey(x => x.AssigneeEmployeeId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.CreatedByUser).WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TaskComment>(entity =>
        {
            entity.ToTable("task_comments");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Body).HasMaxLength(4000).IsRequired();
            entity.HasIndex(x => new { x.ProjectTaskId, x.CreatedAtUtc });
            entity.HasOne(x => x.ProjectTask).WithMany(x => x.Comments).HasForeignKey(x => x.ProjectTaskId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.AuthorUser).WithMany().HasForeignKey(x => x.AuthorUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TaskActivity>(entity =>
        {
            entity.ToTable("task_activities");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Action).HasMaxLength(100).IsRequired();
            entity.Property(x => x.DetailsJson).HasColumnType("jsonb").IsRequired();
            entity.HasIndex(x => new { x.ProjectTaskId, x.CreatedAtUtc });
            entity.HasOne(x => x.ProjectTask).WithMany(x => x.Activities).HasForeignKey(x => x.ProjectTaskId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.ActorUser).WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SurveyForm>(entity =>
        {
            entity.ToTable("survey_forms");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Code).HasMaxLength(50).IsRequired();
            entity.Property(x => x.NormalizedCode).HasMaxLength(50).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.NormalizedName).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(4000);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
            entity.HasIndex(x => x.NormalizedCode).IsUnique();
            entity.HasIndex(x => new { x.ProjectId, x.Status });
            entity.HasIndex(x => x.NormalizedName);
            entity.HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SurveyQuestion>(entity =>
        {
            entity.ToTable("survey_questions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Key).HasMaxLength(100).IsRequired();
            entity.Property(x => x.NormalizedKey).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Prompt).HasMaxLength(500).IsRequired();
            entity.Property(x => x.Type).HasConversion<string>().HasMaxLength(30).IsRequired();
            entity.Property(x => x.OptionsJson).HasColumnType("jsonb").IsRequired();
            entity.HasIndex(x => new { x.SurveyFormId, x.NormalizedKey }).IsUnique();
            entity.HasIndex(x => new { x.SurveyFormId, x.SortOrder }).IsUnique();
            entity.HasOne(x => x.SurveyForm).WithMany(x => x.Questions).HasForeignKey(x => x.SurveyFormId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SurveyAssignment>(entity =>
        {
            entity.ToTable("survey_assignments");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
            entity.HasIndex(x => new { x.SurveyFormId, x.EmployeeId }).IsUnique();
            entity.HasIndex(x => new { x.EmployeeId, x.Status });
            entity.HasIndex(x => new { x.SurveyFormId, x.Status });
            entity.HasIndex(x => x.DueDate);
            entity.HasOne(x => x.SurveyForm).WithMany(x => x.Assignments).HasForeignKey(x => x.SurveyFormId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.AssignedByUser).WithMany().HasForeignKey(x => x.AssignedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SurveySubmission>(entity =>
        {
            entity.ToTable("survey_submissions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(30).IsRequired();
            entity.Property(x => x.ReviewComment).HasMaxLength(2000);
            entity.HasIndex(x => new { x.SurveyAssignmentId, x.RevisionNumber }).IsUnique();
            entity.HasIndex(x => new { x.Status, x.SubmittedAtUtc });
            entity.HasOne(x => x.SurveyAssignment).WithMany(x => x.Submissions).HasForeignKey(x => x.SurveyAssignmentId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.ReviewedByUser).WithMany().HasForeignKey(x => x.ReviewedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SurveyAnswer>(entity =>
        {
            entity.ToTable("survey_answers");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ValueJson).HasColumnType("jsonb").IsRequired();
            entity.HasIndex(x => new { x.SurveySubmissionId, x.SurveyQuestionId }).IsUnique();
            entity.HasOne(x => x.SurveySubmission).WithMany(x => x.Answers).HasForeignKey(x => x.SurveySubmissionId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.SurveyQuestion).WithMany().HasForeignKey(x => x.SurveyQuestionId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
