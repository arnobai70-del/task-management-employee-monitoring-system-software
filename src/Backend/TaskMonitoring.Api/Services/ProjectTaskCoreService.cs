using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;

namespace TaskMonitoring.Api.Services;

public interface IProjectTaskCoreService
{
    Task<PagedResponse<ProjectResponse>> GetProjectsAsync(string? search, ProjectStatus? status, int page, int pageSize, CancellationToken cancellationToken);
    Task<OperationResult<ProjectResponse>> GetProjectAsync(Guid id, CancellationToken cancellationToken);
    Task<OperationResult<ProjectResponse>> CreateProjectAsync(CreateProjectRequest request, RequestActor actor, CancellationToken cancellationToken);
    Task<OperationResult<ProjectResponse>> UpdateProjectAsync(Guid id, UpdateProjectRequest request, RequestActor actor, CancellationToken cancellationToken);
    Task<OperationResult<IReadOnlyCollection<ProjectMemberResponse>>> GetProjectMembersAsync(Guid projectId, bool includeInactive, CancellationToken cancellationToken);
    Task<OperationResult<ProjectMemberResponse>> UpsertProjectMemberAsync(Guid projectId, UpsertProjectMemberRequest request, RequestActor actor, CancellationToken cancellationToken);
    Task<OperationResult<ProjectMemberResponse>> RemoveProjectMemberAsync(Guid projectId, Guid employeeId, RequestActor actor, CancellationToken cancellationToken);
    Task<PagedResponse<ProjectTaskResponse>> GetTasksAsync(Guid? projectId, string? search, ProjectTaskStatus? status, ProjectTaskPriority? priority, Guid? assigneeEmployeeId, int page, int pageSize, CancellationToken cancellationToken);
    Task<OperationResult<ProjectTaskResponse>> GetTaskAsync(Guid id, CancellationToken cancellationToken);
    Task<OperationResult<ProjectTaskResponse>> CreateTaskAsync(CreateProjectTaskRequest request, RequestActor actor, CancellationToken cancellationToken);
    Task<OperationResult<ProjectTaskResponse>> UpdateTaskAsync(Guid id, UpdateProjectTaskRequest request, RequestActor actor, CancellationToken cancellationToken);
    Task<OperationResult<ProjectTaskResponse>> ChangeTaskStatusAsync(Guid id, ChangeProjectTaskStatusRequest request, RequestActor actor, CancellationToken cancellationToken);
    Task<OperationResult<IReadOnlyCollection<TaskCommentResponse>>> GetCommentsAsync(Guid taskId, CancellationToken cancellationToken);
    Task<OperationResult<TaskCommentResponse>> AddCommentAsync(Guid taskId, CreateTaskCommentRequest request, RequestActor actor, CancellationToken cancellationToken);
    Task<OperationResult<IReadOnlyCollection<TaskActivityResponse>>> GetActivitiesAsync(Guid taskId, CancellationToken cancellationToken);
}

public sealed class ProjectTaskCoreService(AppDbContext dbContext, TimeProvider timeProvider) : IProjectTaskCoreService
{
    public async Task<PagedResponse<ProjectResponse>> GetProjectsAsync(string? search, ProjectStatus? status, int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Clamp(page, 1, 1_000_000);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = dbContext.Projects.AsNoTracking();

        if (status.HasValue)
        {
            query = query.Where(x => x.Status == status.Value);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var normalized = Normalize(search);
            query = query.Where(x => x.NormalizedCode.Contains(normalized) || x.NormalizedName.Contains(normalized));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(x => x.NormalizedName)
            .ThenBy(x => x.NormalizedCode)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new ProjectResponse(
                x.Id,
                x.Code,
                x.Name,
                x.Description,
                x.Status,
                x.StartDate,
                x.DueDate,
                x.Members.Count(member => member.IsActive),
                x.Tasks.Count(task => task.Status != ProjectTaskStatus.Done && task.Status != ProjectTaskStatus.Cancelled),
                x.CreatedAtUtc,
                x.UpdatedAtUtc))
            .ToListAsync(cancellationToken);

        return new PagedResponse<ProjectResponse>(items, page, pageSize, totalCount);
    }

    public async Task<OperationResult<ProjectResponse>> GetProjectAsync(Guid id, CancellationToken cancellationToken)
    {
        var project = await dbContext.Projects
            .AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new ProjectResponse(
                x.Id,
                x.Code,
                x.Name,
                x.Description,
                x.Status,
                x.StartDate,
                x.DueDate,
                x.Members.Count(member => member.IsActive),
                x.Tasks.Count(task => task.Status != ProjectTaskStatus.Done && task.Status != ProjectTaskStatus.Cancelled),
                x.CreatedAtUtc,
                x.UpdatedAtUtc))
            .SingleOrDefaultAsync(cancellationToken);

        return project is null
            ? OperationResult<ProjectResponse>.NotFound("project_not_found", "Project was not found.")
            : OperationResult<ProjectResponse>.Success(project);
    }

    public async Task<OperationResult<ProjectResponse>> CreateProjectAsync(CreateProjectRequest request, RequestActor actor, CancellationToken cancellationToken)
    {
        var validation = ValidateProject(request.Code, request.Name, request.Description, request.Status, request.StartDate, request.DueDate, creating: true);
        if (validation.Error is not null)
        {
            return OperationResult<ProjectResponse>.Invalid(validation.Error.Code, validation.Error.Message);
        }

        if (await dbContext.Projects.AnyAsync(x => x.NormalizedCode == validation.NormalizedCode, cancellationToken))
        {
            return OperationResult<ProjectResponse>.Conflict("project_code_exists", "A project with this code already exists.");
        }

        var now = UtcNow();
        var project = new Project
        {
            Code = validation.Code!,
            NormalizedCode = validation.NormalizedCode!,
            Name = validation.Name!,
            NormalizedName = validation.NormalizedName!,
            Description = validation.Description,
            Status = request.Status,
            StartDate = request.StartDate,
            DueDate = request.DueDate,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        dbContext.Projects.Add(project);
        AddAudit(actor, "project.created", "Project", project.Id, new { project.Code, project.Name, project.Status, project.StartDate, project.DueDate });

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return OperationResult<ProjectResponse>.Conflict("project_conflict", "The project conflicts with an existing record.");
        }

        return OperationResult<ProjectResponse>.Success(ToProjectResponse(project, 0, 0));
    }

    public async Task<OperationResult<ProjectResponse>> UpdateProjectAsync(Guid id, UpdateProjectRequest request, RequestActor actor, CancellationToken cancellationToken)
    {
        var project = await dbContext.Projects.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (project is null)
        {
            return OperationResult<ProjectResponse>.NotFound("project_not_found", "Project was not found.");
        }

        if (project.Status == ProjectStatus.Archived)
        {
            return OperationResult<ProjectResponse>.Conflict("project_archived", "Archived projects cannot be modified.");
        }

        var validation = ValidateProject(request.Code, request.Name, request.Description, request.Status, request.StartDate, request.DueDate, creating: false);
        if (validation.Error is not null)
        {
            return OperationResult<ProjectResponse>.Invalid(validation.Error.Code, validation.Error.Message);
        }

        if (await dbContext.Projects.AnyAsync(x => x.Id != id && x.NormalizedCode == validation.NormalizedCode, cancellationToken))
        {
            return OperationResult<ProjectResponse>.Conflict("project_code_exists", "A project with this code already exists.");
        }

        if (request.Status is ProjectStatus.Completed or ProjectStatus.Archived)
        {
            var hasOpenTasks = await dbContext.ProjectTasks.AnyAsync(x =>
                x.ProjectId == id && x.Status != ProjectTaskStatus.Done && x.Status != ProjectTaskStatus.Cancelled,
                cancellationToken);
            if (hasOpenTasks)
            {
                return OperationResult<ProjectResponse>.Conflict("project_has_open_tasks", "All project tasks must be done or cancelled before completing or archiving the project.");
            }
        }

        project.Code = validation.Code!;
        project.NormalizedCode = validation.NormalizedCode!;
        project.Name = validation.Name!;
        project.NormalizedName = validation.NormalizedName!;
        project.Description = validation.Description;
        project.Status = request.Status;
        project.StartDate = request.StartDate;
        project.DueDate = request.DueDate;
        project.UpdatedAtUtc = UtcNow();

        AddAudit(actor, "project.updated", "Project", project.Id, new { project.Code, project.Name, project.Status, project.StartDate, project.DueDate });
        await dbContext.SaveChangesAsync(cancellationToken);

        var activeMembers = await dbContext.ProjectMembers.CountAsync(x => x.ProjectId == id && x.IsActive, cancellationToken);
        var openTasks = await dbContext.ProjectTasks.CountAsync(x => x.ProjectId == id && x.Status != ProjectTaskStatus.Done && x.Status != ProjectTaskStatus.Cancelled, cancellationToken);
        return OperationResult<ProjectResponse>.Success(ToProjectResponse(project, activeMembers, openTasks));
    }

    public async Task<OperationResult<IReadOnlyCollection<ProjectMemberResponse>>> GetProjectMembersAsync(Guid projectId, bool includeInactive, CancellationToken cancellationToken)
    {
        if (!await dbContext.Projects.AnyAsync(x => x.Id == projectId, cancellationToken))
        {
            return OperationResult<IReadOnlyCollection<ProjectMemberResponse>>.NotFound("project_not_found", "Project was not found.");
        }

        var query = dbContext.ProjectMembers.AsNoTracking().Include(x => x.Employee).Where(x => x.ProjectId == projectId);
        if (!includeInactive)
        {
            query = query.Where(x => x.IsActive);
        }

        var members = await query
            .OrderBy(x => x.Employee.NormalizedFullName)
            .ThenBy(x => x.Employee.NormalizedEmployeeCode)
            .Select(x => new ProjectMemberResponse(x.Id, x.EmployeeId, x.Employee.EmployeeCode, x.Employee.FullName, x.Role, x.IsActive, x.AddedAtUtc, x.RemovedAtUtc))
            .ToListAsync(cancellationToken);

        return OperationResult<IReadOnlyCollection<ProjectMemberResponse>>.Success(members);
    }

    public async Task<OperationResult<ProjectMemberResponse>> UpsertProjectMemberAsync(Guid projectId, UpsertProjectMemberRequest request, RequestActor actor, CancellationToken cancellationToken)
    {
        if (request.EmployeeId == Guid.Empty)
        {
            return OperationResult<ProjectMemberResponse>.Invalid("employee_required", "Employee is required.");
        }

        if (!Enum.IsDefined(request.Role))
        {
            return OperationResult<ProjectMemberResponse>.Invalid("project_member_role_invalid", "Project member role is invalid.");
        }

        var project = await dbContext.Projects.SingleOrDefaultAsync(x => x.Id == projectId, cancellationToken);
        if (project is null)
        {
            return OperationResult<ProjectMemberResponse>.NotFound("project_not_found", "Project was not found.");
        }

        if (project.Status == ProjectStatus.Archived)
        {
            return OperationResult<ProjectMemberResponse>.Conflict("project_archived", "Archived projects cannot be modified.");
        }

        var employee = await dbContext.Employees.SingleOrDefaultAsync(x => x.Id == request.EmployeeId, cancellationToken);
        if (employee is null)
        {
            return OperationResult<ProjectMemberResponse>.NotFound("employee_not_found", "Employee was not found.");
        }

        if (!employee.IsActive)
        {
            return OperationResult<ProjectMemberResponse>.Invalid("employee_inactive", "Inactive employees cannot be added to projects.");
        }

        var now = UtcNow();
        var membership = await dbContext.ProjectMembers.SingleOrDefaultAsync(x => x.ProjectId == projectId && x.EmployeeId == request.EmployeeId, cancellationToken);
        var auditAction = "project.member.updated";
        if (membership is null)
        {
            membership = new ProjectMember
            {
                ProjectId = projectId,
                Project = project,
                EmployeeId = employee.Id,
                Employee = employee,
                Role = request.Role,
                IsActive = true,
                AddedAtUtc = now
            };
            dbContext.ProjectMembers.Add(membership);
            auditAction = "project.member.added";
        }
        else
        {
            membership.Role = request.Role;
            if (!membership.IsActive)
            {
                membership.IsActive = true;
                membership.AddedAtUtc = now;
                membership.RemovedAtUtc = null;
                auditAction = "project.member.reactivated";
            }
        }

        AddAudit(actor, auditAction, "ProjectMember", membership.Id, new { membership.ProjectId, membership.EmployeeId, membership.Role });
        await dbContext.SaveChangesAsync(cancellationToken);

        return OperationResult<ProjectMemberResponse>.Success(new ProjectMemberResponse(
            membership.Id, employee.Id, employee.EmployeeCode, employee.FullName, membership.Role, membership.IsActive, membership.AddedAtUtc, membership.RemovedAtUtc));
    }

    public async Task<OperationResult<ProjectMemberResponse>> RemoveProjectMemberAsync(Guid projectId, Guid employeeId, RequestActor actor, CancellationToken cancellationToken)
    {
        var membership = await dbContext.ProjectMembers
            .Include(x => x.Project)
            .Include(x => x.Employee)
            .SingleOrDefaultAsync(x => x.ProjectId == projectId && x.EmployeeId == employeeId, cancellationToken);

        if (membership is null)
        {
            return OperationResult<ProjectMemberResponse>.NotFound("project_member_not_found", "Project member was not found.");
        }

        if (membership.Project.Status == ProjectStatus.Archived)
        {
            return OperationResult<ProjectMemberResponse>.Conflict("project_archived", "Archived projects cannot be modified.");
        }

        if (!membership.IsActive)
        {
            return OperationResult<ProjectMemberResponse>.Conflict("project_member_inactive", "Project member is already inactive.");
        }

        var hasOpenAssignments = await dbContext.ProjectTasks.AnyAsync(x =>
            x.ProjectId == projectId && x.AssigneeEmployeeId == employeeId && x.Status != ProjectTaskStatus.Done && x.Status != ProjectTaskStatus.Cancelled,
            cancellationToken);
        if (hasOpenAssignments)
        {
            return OperationResult<ProjectMemberResponse>.Conflict("project_member_has_open_tasks", "Reassign or close the member's open tasks before removing them from the project.");
        }

        membership.IsActive = false;
        membership.RemovedAtUtc = UtcNow();
        AddAudit(actor, "project.member.removed", "ProjectMember", membership.Id, new { membership.ProjectId, membership.EmployeeId });
        await dbContext.SaveChangesAsync(cancellationToken);

        return OperationResult<ProjectMemberResponse>.Success(new ProjectMemberResponse(
            membership.Id, membership.EmployeeId, membership.Employee.EmployeeCode, membership.Employee.FullName, membership.Role, membership.IsActive, membership.AddedAtUtc, membership.RemovedAtUtc));
    }

    public async Task<PagedResponse<ProjectTaskResponse>> GetTasksAsync(
        Guid? projectId,
        string? search,
        ProjectTaskStatus? status,
        ProjectTaskPriority? priority,
        Guid? assigneeEmployeeId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = Math.Clamp(page, 1, 1_000_000);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = TaskQuery().AsNoTracking();

        if (projectId.HasValue)
        {
            query = query.Where(x => x.ProjectId == projectId.Value);
        }

        if (status.HasValue)
        {
            query = query.Where(x => x.Status == status.Value);
        }

        if (priority.HasValue)
        {
            query = query.Where(x => x.Priority == priority.Value);
        }

        if (assigneeEmployeeId.HasValue)
        {
            query = query.Where(x => x.AssigneeEmployeeId == assigneeEmployeeId.Value);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var normalized = Normalize(search);
            query = query.Where(x => x.NormalizedTitle.Contains(normalized));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var tasks = await query
            .OrderBy(x => x.Status == ProjectTaskStatus.Done || x.Status == ProjectTaskStatus.Cancelled)
            .ThenByDescending(x => x.Priority)
            .ThenBy(x => x.DueDate)
            .ThenBy(x => x.NormalizedTitle)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResponse<ProjectTaskResponse>(tasks.Select(ToTaskResponse).ToArray(), page, pageSize, totalCount);
    }

    public async Task<OperationResult<ProjectTaskResponse>> GetTaskAsync(Guid id, CancellationToken cancellationToken)
    {
        var task = await TaskQuery().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return task is null
            ? OperationResult<ProjectTaskResponse>.NotFound("task_not_found", "Task was not found.")
            : OperationResult<ProjectTaskResponse>.Success(ToTaskResponse(task));
    }

    public async Task<OperationResult<ProjectTaskResponse>> CreateTaskAsync(CreateProjectTaskRequest request, RequestActor actor, CancellationToken cancellationToken)
    {
        if (request.ProjectId == Guid.Empty)
        {
            return OperationResult<ProjectTaskResponse>.Invalid("project_required", "Project is required.");
        }

        var project = await dbContext.Projects.SingleOrDefaultAsync(x => x.Id == request.ProjectId, cancellationToken);
        if (project is null)
        {
            return OperationResult<ProjectTaskResponse>.NotFound("project_not_found", "Project was not found.");
        }

        if (project.Status is ProjectStatus.Completed or ProjectStatus.Archived)
        {
            return OperationResult<ProjectTaskResponse>.Conflict("project_closed", "Tasks cannot be added to completed or archived projects.");
        }

        var validation = ValidateTask(request.Title, request.Description, request.Priority, request.DueDate, project);
        if (validation.Error is not null)
        {
            return OperationResult<ProjectTaskResponse>.Invalid(validation.Error.Code, validation.Error.Message);
        }

        var assignee = await ValidateAssigneeAsync(project.Id, request.AssigneeEmployeeId, cancellationToken);
        if (assignee.Error is not null)
        {
            return OperationResult<ProjectTaskResponse>.Invalid(assignee.Error.Code, assignee.Error.Message);
        }

        var now = UtcNow();
        var task = new ProjectTask
        {
            ProjectId = project.Id,
            Project = project,
            Title = validation.Title!,
            NormalizedTitle = validation.NormalizedTitle!,
            Description = validation.Description,
            Priority = request.Priority,
            AssigneeEmployeeId = request.AssigneeEmployeeId,
            AssigneeEmployee = assignee.Employee,
            DueDate = request.DueDate,
            CreatedByUserId = actor.UserId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        dbContext.ProjectTasks.Add(task);
        AddTaskActivity(task, actor.UserId, "task.created", new { task.Title, task.Priority, task.AssigneeEmployeeId, task.DueDate });
        AddAudit(actor, "task.created", "ProjectTask", task.Id, new { task.ProjectId, task.Title, task.Priority, task.AssigneeEmployeeId, task.DueDate });
        await dbContext.SaveChangesAsync(cancellationToken);

        task.AssigneeEmployee = assignee.Employee;
        return OperationResult<ProjectTaskResponse>.Success(ToTaskResponse(task));
    }

    public async Task<OperationResult<ProjectTaskResponse>> UpdateTaskAsync(Guid id, UpdateProjectTaskRequest request, RequestActor actor, CancellationToken cancellationToken)
    {
        var task = await TaskQuery().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (task is null)
        {
            return OperationResult<ProjectTaskResponse>.NotFound("task_not_found", "Task was not found.");
        }

        if (task.Project.Status == ProjectStatus.Archived)
        {
            return OperationResult<ProjectTaskResponse>.Conflict("project_archived", "Tasks in archived projects cannot be modified.");
        }

        var validation = ValidateTask(request.Title, request.Description, request.Priority, request.DueDate, task.Project);
        if (validation.Error is not null)
        {
            return OperationResult<ProjectTaskResponse>.Invalid(validation.Error.Code, validation.Error.Message);
        }

        var assignee = await ValidateAssigneeAsync(task.ProjectId, request.AssigneeEmployeeId, cancellationToken);
        if (assignee.Error is not null)
        {
            return OperationResult<ProjectTaskResponse>.Invalid(assignee.Error.Code, assignee.Error.Message);
        }

        var previousAssignee = task.AssigneeEmployeeId;
        var previousPriority = task.Priority;
        var previousDueDate = task.DueDate;
        task.Title = validation.Title!;
        task.NormalizedTitle = validation.NormalizedTitle!;
        task.Description = validation.Description;
        task.Priority = request.Priority;
        task.AssigneeEmployeeId = request.AssigneeEmployeeId;
        task.AssigneeEmployee = assignee.Employee;
        task.DueDate = request.DueDate;
        task.UpdatedAtUtc = UtcNow();

        AddTaskActivity(task, actor.UserId, "task.updated", new
        {
            previousAssignee,
            task.AssigneeEmployeeId,
            previousPriority,
            task.Priority,
            previousDueDate,
            task.DueDate
        });
        AddAudit(actor, "task.updated", "ProjectTask", task.Id, new { task.ProjectId, task.Title, task.Priority, task.AssigneeEmployeeId, task.DueDate });
        await dbContext.SaveChangesAsync(cancellationToken);

        return OperationResult<ProjectTaskResponse>.Success(ToTaskResponse(task));
    }

    public async Task<OperationResult<ProjectTaskResponse>> ChangeTaskStatusAsync(Guid id, ChangeProjectTaskStatusRequest request, RequestActor actor, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(request.Status))
        {
            return OperationResult<ProjectTaskResponse>.Invalid("task_status_invalid", "Task status is invalid.");
        }

        var task = await TaskQuery().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (task is null)
        {
            return OperationResult<ProjectTaskResponse>.NotFound("task_not_found", "Task was not found.");
        }

        if (task.Project.Status == ProjectStatus.Archived)
        {
            return OperationResult<ProjectTaskResponse>.Conflict("project_archived", "Tasks in archived projects cannot be modified.");
        }

        if (task.Status == request.Status)
        {
            return OperationResult<ProjectTaskResponse>.Success(ToTaskResponse(task));
        }

        if (!CanTransition(task.Status, request.Status))
        {
            return OperationResult<ProjectTaskResponse>.Conflict("task_status_transition_invalid", $"Task cannot move from {task.Status} to {request.Status}.");
        }

        var previous = task.Status;
        task.Status = request.Status;
        task.CompletedAtUtc = request.Status == ProjectTaskStatus.Done ? UtcNow() : null;
        task.UpdatedAtUtc = UtcNow();
        AddTaskActivity(task, actor.UserId, "task.status.changed", new { From = previous, To = task.Status });
        AddAudit(actor, "task.status.changed", "ProjectTask", task.Id, new { task.ProjectId, From = previous, To = task.Status });
        await dbContext.SaveChangesAsync(cancellationToken);

        return OperationResult<ProjectTaskResponse>.Success(ToTaskResponse(task));
    }

    public async Task<OperationResult<IReadOnlyCollection<TaskCommentResponse>>> GetCommentsAsync(Guid taskId, CancellationToken cancellationToken)
    {
        if (!await dbContext.ProjectTasks.AnyAsync(x => x.Id == taskId, cancellationToken))
        {
            return OperationResult<IReadOnlyCollection<TaskCommentResponse>>.NotFound("task_not_found", "Task was not found.");
        }

        var comments = await dbContext.TaskComments
            .AsNoTracking()
            .Include(x => x.AuthorUser)
            .Where(x => x.ProjectTaskId == taskId)
            .OrderBy(x => x.CreatedAtUtc)
            .Select(x => new TaskCommentResponse(x.Id, x.ProjectTaskId, x.AuthorUserId, x.AuthorUser.Email, x.Body, x.CreatedAtUtc))
            .ToListAsync(cancellationToken);

        return OperationResult<IReadOnlyCollection<TaskCommentResponse>>.Success(comments);
    }

    public async Task<OperationResult<TaskCommentResponse>> AddCommentAsync(Guid taskId, CreateTaskCommentRequest request, RequestActor actor, CancellationToken cancellationToken)
    {
        if (!actor.UserId.HasValue)
        {
            return OperationResult<TaskCommentResponse>.Invalid("actor_required", "A valid authenticated user is required.");
        }

        var body = request.Body.Trim();
        if (string.IsNullOrWhiteSpace(body))
        {
            return OperationResult<TaskCommentResponse>.Invalid("comment_required", "Comment text is required.");
        }

        var task = await TaskQuery().SingleOrDefaultAsync(x => x.Id == taskId, cancellationToken);
        if (task is null)
        {
            return OperationResult<TaskCommentResponse>.NotFound("task_not_found", "Task was not found.");
        }

        if (task.Project.Status == ProjectStatus.Archived)
        {
            return OperationResult<TaskCommentResponse>.Conflict("project_archived", "Comments cannot be added to archived projects.");
        }

        var user = await dbContext.Users.SingleOrDefaultAsync(x => x.Id == actor.UserId.Value && x.IsActive, cancellationToken);
        if (user is null)
        {
            return OperationResult<TaskCommentResponse>.Invalid("actor_inactive", "The authenticated user is inactive or unavailable.");
        }

        var now = UtcNow();
        var comment = new TaskComment
        {
            ProjectTaskId = task.Id,
            ProjectTask = task,
            AuthorUserId = user.Id,
            AuthorUser = user,
            Body = body,
            CreatedAtUtc = now
        };
        dbContext.TaskComments.Add(comment);
        AddTaskActivity(task, actor.UserId, "task.comment.added", new { comment.Id });
        AddAudit(actor, "task.comment.added", "TaskComment", comment.Id, new { task.Id });
        await dbContext.SaveChangesAsync(cancellationToken);

        return OperationResult<TaskCommentResponse>.Success(new TaskCommentResponse(comment.Id, task.Id, user.Id, user.Email, comment.Body, comment.CreatedAtUtc));
    }

    public async Task<OperationResult<IReadOnlyCollection<TaskActivityResponse>>> GetActivitiesAsync(Guid taskId, CancellationToken cancellationToken)
    {
        if (!await dbContext.ProjectTasks.AnyAsync(x => x.Id == taskId, cancellationToken))
        {
            return OperationResult<IReadOnlyCollection<TaskActivityResponse>>.NotFound("task_not_found", "Task was not found.");
        }

        var activities = await dbContext.TaskActivities
            .AsNoTracking()
            .Include(x => x.ActorUser)
            .Where(x => x.ProjectTaskId == taskId)
            .OrderBy(x => x.CreatedAtUtc)
            .Select(x => new TaskActivityResponse(x.Id, x.ProjectTaskId, x.ActorUserId, x.ActorUser == null ? null : x.ActorUser.Email, x.Action, x.DetailsJson, x.CreatedAtUtc))
            .ToListAsync(cancellationToken);

        return OperationResult<IReadOnlyCollection<TaskActivityResponse>>.Success(activities);
    }

    private async Task<(Employee? Employee, ApiOperationError? Error)> ValidateAssigneeAsync(Guid projectId, Guid? employeeId, CancellationToken cancellationToken)
    {
        if (!employeeId.HasValue)
        {
            return (null, null);
        }

        var employee = await dbContext.Employees.SingleOrDefaultAsync(x => x.Id == employeeId.Value, cancellationToken);
        if (employee is null)
        {
            return (null, new ApiOperationError("assignee_not_found", "Assignee employee was not found."));
        }

        if (!employee.IsActive)
        {
            return (null, new ApiOperationError("assignee_inactive", "Inactive employees cannot be assigned tasks."));
        }

        var isMember = await dbContext.ProjectMembers.AnyAsync(x => x.ProjectId == projectId && x.EmployeeId == employeeId.Value && x.IsActive, cancellationToken);
        return isMember
            ? (employee, null)
            : (null, new ApiOperationError("assignee_not_project_member", "Assignee must be an active member of the project."));
    }

    private static (string? Title, string? NormalizedTitle, string? Description, ApiOperationError? Error) ValidateTask(
        string title,
        string? description,
        ProjectTaskPriority priority,
        DateOnly? dueDate,
        Project project)
    {
        var trimmedTitle = title.Trim();
        var trimmedDescription = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (trimmedTitle.Length is < 2 or > 200)
        {
            return (null, null, null, new ApiOperationError("task_title_invalid", "Task title must contain between 2 and 200 characters."));
        }

        if (!Enum.IsDefined(priority))
        {
            return (null, null, null, new ApiOperationError("task_priority_invalid", "Task priority is invalid."));
        }

        if (dueDate.HasValue && project.StartDate.HasValue && dueDate.Value < project.StartDate.Value)
        {
            return (null, null, null, new ApiOperationError("task_due_before_project_start", "Task due date cannot be before the project start date."));
        }

        if (dueDate.HasValue && project.DueDate.HasValue && dueDate.Value > project.DueDate.Value)
        {
            return (null, null, null, new ApiOperationError("task_due_after_project_due", "Task due date cannot be after the project due date."));
        }

        return (trimmedTitle, Normalize(trimmedTitle), trimmedDescription, null);
    }

    private static (string? Code, string? NormalizedCode, string? Name, string? NormalizedName, string? Description, ApiOperationError? Error) ValidateProject(
        string code,
        string name,
        string? description,
        ProjectStatus status,
        DateOnly? startDate,
        DateOnly? dueDate,
        bool creating)
    {
        var trimmedCode = code.Trim();
        var trimmedName = name.Trim();
        var trimmedDescription = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (trimmedCode.Length is < 1 or > 50)
        {
            return (null, null, null, null, null, new ApiOperationError("project_code_invalid", "Project code must contain between 1 and 50 characters."));
        }

        if (trimmedName.Length is < 2 or > 200)
        {
            return (null, null, null, null, null, new ApiOperationError("project_name_invalid", "Project name must contain between 2 and 200 characters."));
        }

        if (!Enum.IsDefined(status))
        {
            return (null, null, null, null, null, new ApiOperationError("project_status_invalid", "Project status is invalid."));
        }

        if (creating && status is ProjectStatus.Completed or ProjectStatus.Archived)
        {
            return (null, null, null, null, null, new ApiOperationError("project_status_invalid", "New projects cannot start as completed or archived."));
        }

        if (startDate.HasValue && dueDate.HasValue && dueDate.Value < startDate.Value)
        {
            return (null, null, null, null, null, new ApiOperationError("project_date_range_invalid", "Project due date cannot be before the start date."));
        }

        return (trimmedCode, Normalize(trimmedCode), trimmedName, Normalize(trimmedName), trimmedDescription, null);
    }

    private IQueryable<ProjectTask> TaskQuery() => dbContext.ProjectTasks
        .Include(x => x.Project)
        .Include(x => x.AssigneeEmployee)
        .Include(x => x.Comments);

    private static ProjectResponse ToProjectResponse(Project project, int activeMemberCount, int openTaskCount) => new(
        project.Id,
        project.Code,
        project.Name,
        project.Description,
        project.Status,
        project.StartDate,
        project.DueDate,
        activeMemberCount,
        openTaskCount,
        project.CreatedAtUtc,
        project.UpdatedAtUtc);

    private static ProjectResponse ToProjectResponseProjection(Project project) => new(
        project.Id,
        project.Code,
        project.Name,
        project.Description,
        project.Status,
        project.StartDate,
        project.DueDate,
        project.Members.Count(x => x.IsActive),
        project.Tasks.Count(x => x.Status != ProjectTaskStatus.Done && x.Status != ProjectTaskStatus.Cancelled),
        project.CreatedAtUtc,
        project.UpdatedAtUtc);

    private static ProjectTaskResponse ToTaskResponse(ProjectTask task) => new(
        task.Id,
        task.ProjectId,
        task.Project.Code,
        task.Project.Name,
        task.Title,
        task.Description,
        task.Status,
        task.Priority,
        task.AssigneeEmployeeId,
        task.AssigneeEmployee?.FullName,
        task.DueDate,
        task.CompletedAtUtc,
        task.Comments.Count,
        task.CreatedAtUtc,
        task.UpdatedAtUtc);

    private void AddTaskActivity(ProjectTask task, Guid? actorUserId, string action, object details)
    {
        var activity = new TaskActivity
        {
            ProjectTaskId = task.Id,
            ProjectTask = task,
            ActorUserId = actorUserId,
            Action = action,
            DetailsJson = JsonSerializer.Serialize(details),
            CreatedAtUtc = UtcNow()
        };
        dbContext.TaskActivities.Add(activity);
    }

    private void AddAudit(RequestActor actor, string action, string targetType, Guid targetId, object details)
    {
        dbContext.AuditLogs.Add(new AuditLog
        {
            ActorUserId = actor.UserId,
            Action = action,
            TargetType = targetType,
            TargetId = targetId.ToString(),
            MetadataJson = JsonSerializer.Serialize(details),
            IpAddress = actor.IpAddress,
            UserAgent = actor.UserAgent,
            CreatedAtUtc = UtcNow()
        });
    }

    private static bool CanTransition(ProjectTaskStatus from, ProjectTaskStatus to) => from switch
    {
        ProjectTaskStatus.ToDo => to is ProjectTaskStatus.InProgress or ProjectTaskStatus.Blocked or ProjectTaskStatus.Cancelled,
        ProjectTaskStatus.InProgress => to is ProjectTaskStatus.Blocked or ProjectTaskStatus.Done or ProjectTaskStatus.Cancelled,
        ProjectTaskStatus.Blocked => to is ProjectTaskStatus.InProgress or ProjectTaskStatus.Cancelled,
        ProjectTaskStatus.Done => to is ProjectTaskStatus.InProgress,
        ProjectTaskStatus.Cancelled => to is ProjectTaskStatus.ToDo,
        _ => false
    };

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;
    private static string Normalize(string value) => value.Trim().ToUpperInvariant();
}
