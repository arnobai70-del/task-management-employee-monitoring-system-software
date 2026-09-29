using System.ComponentModel.DataAnnotations;
using TaskMonitoring.Api.Domain;

namespace TaskMonitoring.Api.Contracts;

public sealed record ProjectResponse(
    Guid Id,
    string Code,
    string Name,
    string? Description,
    ProjectStatus Status,
    DateOnly? StartDate,
    DateOnly? DueDate,
    int ActiveMemberCount,
    int OpenTaskCount,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed class CreateProjectRequest
{
    [Required, StringLength(50, MinimumLength = 1)]
    public string Code { get; init; } = string.Empty;

    [Required, StringLength(200, MinimumLength = 2)]
    public string Name { get; init; } = string.Empty;

    [StringLength(4000)]
    public string? Description { get; init; }

    public ProjectStatus Status { get; init; } = ProjectStatus.Planning;
    public DateOnly? StartDate { get; init; }
    public DateOnly? DueDate { get; init; }
}

public sealed class UpdateProjectRequest
{
    [Required, StringLength(50, MinimumLength = 1)]
    public string Code { get; init; } = string.Empty;

    [Required, StringLength(200, MinimumLength = 2)]
    public string Name { get; init; } = string.Empty;

    [StringLength(4000)]
    public string? Description { get; init; }

    public ProjectStatus Status { get; init; } = ProjectStatus.Planning;
    public DateOnly? StartDate { get; init; }
    public DateOnly? DueDate { get; init; }
}

public sealed record ProjectMemberResponse(
    Guid Id,
    Guid EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    ProjectMemberRole Role,
    bool IsActive,
    DateTime AddedAtUtc,
    DateTime? RemovedAtUtc);

public sealed class UpsertProjectMemberRequest
{
    public Guid EmployeeId { get; init; }
    public ProjectMemberRole Role { get; init; } = ProjectMemberRole.Member;
}

public sealed record ProjectTaskResponse(
    Guid Id,
    Guid ProjectId,
    string ProjectCode,
    string ProjectName,
    string Title,
    string? Description,
    ProjectTaskStatus Status,
    ProjectTaskPriority Priority,
    Guid? AssigneeEmployeeId,
    string? AssigneeName,
    DateOnly? DueDate,
    DateTime? CompletedAtUtc,
    int CommentCount,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed class CreateProjectTaskRequest
{
    public Guid ProjectId { get; init; }

    [Required, StringLength(200, MinimumLength = 2)]
    public string Title { get; init; } = string.Empty;

    [StringLength(4000)]
    public string? Description { get; init; }

    public ProjectTaskPriority Priority { get; init; } = ProjectTaskPriority.Normal;
    public Guid? AssigneeEmployeeId { get; init; }
    public DateOnly? DueDate { get; init; }
}

public sealed class UpdateProjectTaskRequest
{
    [Required, StringLength(200, MinimumLength = 2)]
    public string Title { get; init; } = string.Empty;

    [StringLength(4000)]
    public string? Description { get; init; }

    public ProjectTaskPriority Priority { get; init; } = ProjectTaskPriority.Normal;
    public Guid? AssigneeEmployeeId { get; init; }
    public DateOnly? DueDate { get; init; }
}

public sealed class ChangeProjectTaskStatusRequest
{
    public ProjectTaskStatus Status { get; init; }
}

public sealed record TaskCommentResponse(
    Guid Id,
    Guid ProjectTaskId,
    Guid AuthorUserId,
    string AuthorEmail,
    string Body,
    DateTime CreatedAtUtc);

public sealed class CreateTaskCommentRequest
{
    [Required, StringLength(4000, MinimumLength = 1)]
    public string Body { get; init; } = string.Empty;
}

public sealed record TaskActivityResponse(
    Guid Id,
    Guid ProjectTaskId,
    Guid? ActorUserId,
    string? ActorEmail,
    string Action,
    string DetailsJson,
    DateTime CreatedAtUtc);
