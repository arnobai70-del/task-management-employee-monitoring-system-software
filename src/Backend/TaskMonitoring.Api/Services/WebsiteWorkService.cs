using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;

namespace TaskMonitoring.Api.Services;

public interface IWebsiteWorkService
{
    Task<PagedResponse<WebsiteWorkResponse>> GetAllAsync(
        string? search,
        ProjectTaskStatus? status,
        Guid? employeeId,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<PagedResponse<WebsiteWorkCompletionResponse>> GetRecentCompletionsAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<OperationResult<WebsiteWorkResponse>> CreateAsync(
        UpsertWebsiteWorkRequest request,
        RequestActor actor,
        CancellationToken cancellationToken);

    Task<OperationResult<WebsiteWorkResponse>> UpdateAsync(
        Guid id,
        UpsertWebsiteWorkRequest request,
        RequestActor actor,
        CancellationToken cancellationToken);

    Task<OperationResult<IReadOnlyCollection<WebsiteWorkResponse>>> GetMineAsync(
        RequestActor actor,
        bool includeClosed,
        CancellationToken cancellationToken);

    Task<OperationResult<WebsiteWorkResponse>> StartAsync(
        Guid id,
        RequestActor actor,
        CancellationToken cancellationToken);

    Task<OperationResult<WebsiteWorkResponse>> CompleteAsync(
        Guid id,
        RequestActor actor,
        CancellationToken cancellationToken);
}

public sealed class WebsiteWorkService(
    AppDbContext dbContext,
    TimeProvider timeProvider,
    IWebsiteWorkRealtimePublisher realtimePublisher,
    ILogger<WebsiteWorkService> logger) : IWebsiteWorkService
{
    public const string ConfiguredAction = "website-work.configured";
    public const string UpdatedAction = "website-work.updated";
    public const string StartedAction = "website-work.started";
    public const string OpenedAction = "website-work.opened";
    public const string CompletedAction = "website-work.completed";

    public async Task<PagedResponse<WebsiteWorkResponse>> GetAllAsync(
        string? search,
        ProjectTaskStatus? status,
        Guid? employeeId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = Math.Clamp(page, 1, 1_000_000);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = WorkQuery().AsNoTracking();

        if (status.HasValue)
        {
            query = query.Where(x => x.Status == status.Value);
        }

        if (employeeId.HasValue)
        {
            query = query.Where(x => x.AssigneeEmployeeId == employeeId.Value);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var normalized = Normalize(search);
            query = query.Where(x =>
                x.NormalizedTitle.Contains(normalized) ||
                x.Project.NormalizedName.Contains(normalized) ||
                (x.AssigneeEmployee != null && x.AssigneeEmployee.NormalizedFullName.Contains(normalized)));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var tasks = await query
            .OrderBy(x => x.Status == ProjectTaskStatus.Done || x.Status == ProjectTaskStatus.Cancelled)
            .ThenBy(x => x.DueDate == null)
            .ThenBy(x => x.DueDate)
            .ThenByDescending(x => x.Priority)
            .ThenBy(x => x.NormalizedTitle)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResponse<WebsiteWorkResponse>(tasks.Select(ToResponse).ToArray(), page, pageSize, totalCount);
    }

    public async Task<PagedResponse<WebsiteWorkCompletionResponse>> GetRecentCompletionsAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = Math.Clamp(page, 1, 1_000_000);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = dbContext.TaskActivities
            .AsNoTracking()
            .Where(x => x.Action == CompletedAction);

        var totalCount = await query.CountAsync(cancellationToken);
        var activities = await query
            .OrderByDescending(x => x.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var items = new List<WebsiteWorkCompletionResponse>(activities.Count);
        foreach (var activity in activities)
        {
            if (TryReadCompletion(activity, out var completion))
            {
                items.Add(completion!);
            }
        }

        return new PagedResponse<WebsiteWorkCompletionResponse>(items, page, pageSize, totalCount);
    }

    public async Task<OperationResult<WebsiteWorkResponse>> CreateAsync(
        UpsertWebsiteWorkRequest request,
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        var validation = await ValidateRequestAsync(request, null, cancellationToken);
        if (validation.Error is not null)
        {
            return Error<WebsiteWorkResponse>(validation.Error);
        }

        var now = UtcNow();
        var task = new ProjectTask
        {
            ProjectId = validation.Project!.Id,
            Project = validation.Project,
            Title = validation.Title!,
            NormalizedTitle = Normalize(validation.Title!),
            Description = validation.Instructions,
            Status = ProjectTaskStatus.ToDo,
            Priority = request.Priority,
            AssigneeEmployeeId = validation.Employee!.Id,
            AssigneeEmployee = validation.Employee,
            DueDate = request.DueDate,
            CreatedByUserId = actor.UserId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        dbContext.ProjectTasks.Add(task);
        AddActivity(task, actor.UserId, ConfiguredAction, new { url = validation.Url });
        AddActivity(task, actor.UserId, "task.created", new { task.Title, task.Priority, task.AssigneeEmployeeId, task.DueDate, kind = "website-work" });
        AddAudit(actor, "website-work.created", task.Id, new
        {
            task.ProjectId,
            task.AssigneeEmployeeId,
            task.Title,
            task.Priority,
            task.DueDate,
            host = validation.Host
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult<WebsiteWorkResponse>.Success(ToResponse(task));
    }

    public async Task<OperationResult<WebsiteWorkResponse>> UpdateAsync(
        Guid id,
        UpsertWebsiteWorkRequest request,
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        var task = await WorkQuery(tracked: true).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (task is null)
        {
            return OperationResult<WebsiteWorkResponse>.NotFound("website_work_not_found", "Website work assignment was not found.");
        }

        if (task.Status is ProjectTaskStatus.Done or ProjectTaskStatus.Cancelled)
        {
            return OperationResult<WebsiteWorkResponse>.Conflict("website_work_closed", "Completed or cancelled website work cannot be edited.");
        }

        if (request.ProjectId != task.ProjectId)
        {
            return OperationResult<WebsiteWorkResponse>.Conflict("website_work_project_locked", "Move work to another project by creating a new assignment.");
        }

        if (request.EmployeeId != Guid.Empty &&
            request.EmployeeId != task.AssigneeEmployeeId &&
            task.Activities.Any(activity => activity.Action == StartedAction))
        {
            return OperationResult<WebsiteWorkResponse>.Conflict(
                "website_work_assignee_locked",
                "Website work that has already started cannot be reassigned. Create a new assignment for the other employee so work history remains accurate.");
        }

        var validation = await ValidateRequestAsync(request, task.Id, cancellationToken);
        if (validation.Error is not null)
        {
            return Error<WebsiteWorkResponse>(validation.Error);
        }

        var previousEmployeeId = task.AssigneeEmployeeId;
        var previousUrl = CurrentUrl(task);
        var urlChanged = !string.Equals(previousUrl, validation.Url, StringComparison.Ordinal);

        task.Title = validation.Title!;
        task.NormalizedTitle = Normalize(validation.Title!);
        task.Description = validation.Instructions;
        task.Priority = request.Priority;
        task.AssigneeEmployeeId = validation.Employee!.Id;
        task.AssigneeEmployee = validation.Employee;
        task.DueDate = request.DueDate;
        task.UpdatedAtUtc = UtcNow();

        if (urlChanged)
        {
            AddActivity(task, actor.UserId, ConfiguredAction, new { url = validation.Url });
        }

        AddActivity(task, actor.UserId, UpdatedAction, new
        {
            previousEmployeeId,
            task.AssigneeEmployeeId,
            task.Priority,
            task.DueDate,
            urlChanged,
            host = validation.Host
        });
        AddAudit(actor, "website-work.updated", task.Id, new
        {
            task.ProjectId,
            previousEmployeeId,
            task.AssigneeEmployeeId,
            task.Title,
            task.Priority,
            task.DueDate,
            urlChanged,
            host = validation.Host
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult<WebsiteWorkResponse>.Success(ToResponse(task));
    }

    public async Task<OperationResult<IReadOnlyCollection<WebsiteWorkResponse>>> GetMineAsync(
        RequestActor actor,
        bool includeClosed,
        CancellationToken cancellationToken)
    {
        var employeeResult = await ResolveEmployeeAsync(actor, cancellationToken);
        if (employeeResult.Error is not null)
        {
            return Error<IReadOnlyCollection<WebsiteWorkResponse>>(employeeResult.Error);
        }

        var query = WorkQuery()
            .AsNoTracking()
            .Where(x => x.AssigneeEmployeeId == employeeResult.Employee!.Id);
        if (!includeClosed)
        {
            query = query.Where(x => x.Status != ProjectTaskStatus.Done && x.Status != ProjectTaskStatus.Cancelled);
        }

        var tasks = await query
            .OrderBy(x => x.Status == ProjectTaskStatus.Done || x.Status == ProjectTaskStatus.Cancelled)
            .ThenBy(x => x.DueDate == null)
            .ThenBy(x => x.DueDate)
            .ThenByDescending(x => x.Priority)
            .ToListAsync(cancellationToken);

        return OperationResult<IReadOnlyCollection<WebsiteWorkResponse>>.Success(tasks.Select(ToResponse).ToArray());
    }

    public async Task<OperationResult<WebsiteWorkResponse>> StartAsync(
        Guid id,
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        var employeeResult = await ResolveEmployeeAsync(actor, cancellationToken);
        if (employeeResult.Error is not null)
        {
            return Error<WebsiteWorkResponse>(employeeResult.Error);
        }

        var employee = employeeResult.Employee!;
        var task = await WorkQuery(tracked: true)
            .SingleOrDefaultAsync(x => x.Id == id && x.AssigneeEmployeeId == employee.Id, cancellationToken);
        if (task is null)
        {
            return OperationResult<WebsiteWorkResponse>.NotFound("website_work_not_found", "Website work assignment was not found.");
        }

        if (task.Project.Status == ProjectStatus.Archived)
        {
            return OperationResult<WebsiteWorkResponse>.Conflict("project_archived", "Work in an archived project cannot be started.");
        }

        var url = CurrentUrl(task);
        var urlValidation = ValidateUrl(url);
        if (urlValidation.Error is not null)
        {
            return OperationResult<WebsiteWorkResponse>.Invalid(urlValidation.Error.Code, urlValidation.Error.Message);
        }

        if (task.Status == ProjectTaskStatus.ToDo)
        {
            task.Status = ProjectTaskStatus.InProgress;
            task.CompletedAtUtc = null;
            task.UpdatedAtUtc = UtcNow();
            AddActivity(task, actor.UserId, StartedAction, new { host = urlValidation.Host });
            AddAudit(actor, "website-work.started", task.Id, new { task.ProjectId, employee.Id, host = urlValidation.Host });
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        else if (task.Status == ProjectTaskStatus.InProgress)
        {
            AddActivity(task, actor.UserId, OpenedAction, new { host = urlValidation.Host });
            AddAudit(actor, "website-work.opened", task.Id, new { task.ProjectId, employee.Id, host = urlValidation.Host });
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        else
        {
            return OperationResult<WebsiteWorkResponse>.Conflict(
                "website_work_not_startable",
                task.Status == ProjectTaskStatus.Blocked
                    ? "This website work is blocked. Ask a manager before continuing."
                    : "Completed or cancelled website work cannot be started.");
        }

        return OperationResult<WebsiteWorkResponse>.Success(ToResponse(task));
    }

    public async Task<OperationResult<WebsiteWorkResponse>> CompleteAsync(
        Guid id,
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        var employeeResult = await ResolveEmployeeAsync(actor, cancellationToken);
        if (employeeResult.Error is not null)
        {
            return Error<WebsiteWorkResponse>(employeeResult.Error);
        }

        var employee = employeeResult.Employee!;
        var task = await WorkQuery(tracked: true)
            .SingleOrDefaultAsync(x => x.Id == id && x.AssigneeEmployeeId == employee.Id, cancellationToken);
        if (task is null)
        {
            return OperationResult<WebsiteWorkResponse>.NotFound("website_work_not_found", "Website work assignment was not found.");
        }

        if (task.Status != ProjectTaskStatus.InProgress)
        {
            return OperationResult<WebsiteWorkResponse>.Conflict(
                "website_work_not_working",
                "Start the website work before marking it complete.");
        }

        var now = UtcNow();
        task.Status = ProjectTaskStatus.Done;
        task.CompletedAtUtc = now;
        task.UpdatedAtUtc = now;

        var completion = new WebsiteWorkCompletionResponse(
            task.Id,
            task.ProjectId,
            task.Project.Name,
            employee.Id,
            employee.EmployeeCode,
            employee.FullName,
            task.Title,
            now,
            $"{employee.FullName} completed: {task.Title}");

        AddActivity(task, actor.UserId, CompletedAction, new
        {
            taskId = completion.TaskId,
            projectId = completion.ProjectId,
            projectName = completion.ProjectName,
            employeeId = completion.EmployeeId,
            employeeCode = completion.EmployeeCode,
            employeeName = completion.EmployeeName,
            taskTitle = completion.TaskTitle,
            completedAtUtc = completion.CompletedAtUtc,
            message = completion.Message
        });
        AddAudit(actor, "website-work.completed", task.Id, new
        {
            task.ProjectId,
            employee.Id,
            employee.EmployeeCode,
            employee.FullName,
            task.Title
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            await realtimePublisher.PublishCompletionAsync(completion, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Website work completion {TaskId} was saved but manager realtime delivery failed.", task.Id);
        }

        return OperationResult<WebsiteWorkResponse>.Success(ToResponse(task));
    }

    private IQueryable<ProjectTask> WorkQuery(bool tracked = false)
    {
        var query = dbContext.ProjectTasks
            .Include(x => x.Project)
            .Include(x => x.AssigneeEmployee)
            .Include(x => x.Activities)
            .Where(x => x.Activities.Any(activity => activity.Action == ConfiguredAction));
        return tracked ? query : query.AsNoTracking();
    }

    private async Task<(Project? Project, Employee? Employee, string? Title, string? Instructions, string? Url, string? Host, ApiOperationError? Error)> ValidateRequestAsync(
        UpsertWebsiteWorkRequest request,
        Guid? existingTaskId,
        CancellationToken cancellationToken)
    {
        if (request.ProjectId == Guid.Empty)
        {
            return (null, null, null, null, null, null, new ApiOperationError("project_required", "Project is required."));
        }

        if (request.EmployeeId == Guid.Empty)
        {
            return (null, null, null, null, null, null, new ApiOperationError("employee_required", "Employee is required."));
        }

        var title = request.Title.Trim();
        if (title.Length is < 2 or > 200)
        {
            return (null, null, null, null, null, null, new ApiOperationError("website_work_title_invalid", "Work target must contain between 2 and 200 characters."));
        }

        var instructions = string.IsNullOrWhiteSpace(request.Instructions) ? null : request.Instructions.Trim();
        if (instructions is { Length: > 4000 })
        {
            return (null, null, null, null, null, null, new ApiOperationError("website_work_instructions_too_long", "Instructions cannot exceed 4000 characters."));
        }

        if (!Enum.IsDefined(request.Priority))
        {
            return (null, null, null, null, null, null, new ApiOperationError("website_work_priority_invalid", "Priority is invalid."));
        }

        var urlValidation = ValidateUrl(request.Url);
        if (urlValidation.Error is not null)
        {
            return (null, null, null, null, null, null, urlValidation.Error);
        }

        var project = await dbContext.Projects.SingleOrDefaultAsync(x => x.Id == request.ProjectId, cancellationToken);
        if (project is null)
        {
            return (null, null, null, null, null, null, new ApiOperationError("project_not_found", "Project was not found."));
        }

        if (project.Status is ProjectStatus.Completed or ProjectStatus.Archived)
        {
            return (null, null, null, null, null, null, new ApiOperationError("project_closed", "Website work cannot be assigned in a completed or archived project."));
        }

        if (request.DueDate.HasValue && project.StartDate.HasValue && request.DueDate.Value < project.StartDate.Value)
        {
            return (null, null, null, null, null, null, new ApiOperationError("website_work_due_before_project_start", "Due date cannot be before the project start date."));
        }

        if (request.DueDate.HasValue && project.DueDate.HasValue && request.DueDate.Value > project.DueDate.Value)
        {
            return (null, null, null, null, null, null, new ApiOperationError("website_work_due_after_project_due", "Due date cannot be after the project due date."));
        }

        var employee = await dbContext.Employees.SingleOrDefaultAsync(x => x.Id == request.EmployeeId, cancellationToken);
        if (employee is null)
        {
            return (null, null, null, null, null, null, new ApiOperationError("employee_not_found", "Employee was not found."));
        }

        if (!employee.IsActive)
        {
            return (null, null, null, null, null, null, new ApiOperationError("employee_inactive", "Inactive employees cannot receive website work."));
        }

        var isMember = await dbContext.ProjectMembers.AnyAsync(x =>
            x.ProjectId == project.Id && x.EmployeeId == employee.Id && x.IsActive,
            cancellationToken);
        if (!isMember)
        {
            return (null, null, null, null, null, null, new ApiOperationError("employee_not_project_member", "Employee must be an active member of the selected project."));
        }

        if (existingTaskId.HasValue)
        {
            var duplicate = await dbContext.ProjectTasks.AnyAsync(x =>
                x.Id != existingTaskId.Value &&
                x.ProjectId == project.Id &&
                x.AssigneeEmployeeId == employee.Id &&
                x.NormalizedTitle == Normalize(title) &&
                x.Status != ProjectTaskStatus.Done &&
                x.Status != ProjectTaskStatus.Cancelled &&
                x.Activities.Any(activity => activity.Action == ConfiguredAction),
                cancellationToken);
            if (duplicate)
            {
                return (null, null, null, null, null, null, new ApiOperationError("website_work_duplicate", "This employee already has an open website work assignment with the same target."));
            }
        }
        else
        {
            var duplicate = await dbContext.ProjectTasks.AnyAsync(x =>
                x.ProjectId == project.Id &&
                x.AssigneeEmployeeId == employee.Id &&
                x.NormalizedTitle == Normalize(title) &&
                x.Status != ProjectTaskStatus.Done &&
                x.Status != ProjectTaskStatus.Cancelled &&
                x.Activities.Any(activity => activity.Action == ConfiguredAction),
                cancellationToken);
            if (duplicate)
            {
                return (null, null, null, null, null, null, new ApiOperationError("website_work_duplicate", "This employee already has an open website work assignment with the same target."));
            }
        }

        return (project, employee, title, instructions, urlValidation.Url, urlValidation.Host, null);
    }

    private async Task<(Employee? Employee, ApiOperationError? Error)> ResolveEmployeeAsync(
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        if (!actor.UserId.HasValue)
        {
            return (null, new ApiOperationError("authenticated_user_required", "An authenticated user is required."));
        }

        var employee = await dbContext.Employees
            .Include(x => x.User)
            .SingleOrDefaultAsync(x => x.UserId == actor.UserId.Value, cancellationToken);
        if (employee is null)
        {
            return (null, new ApiOperationError("employee_profile_not_found", "No employee profile is linked to this account."));
        }

        if (!employee.IsActive || !employee.User.IsActive)
        {
            return (null, new ApiOperationError("employee_inactive", "The employee profile is inactive."));
        }

        return (employee, null);
    }

    private static (string? Url, string? Host, ApiOperationError? Error) ValidateUrl(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.Length > 2048)
        {
            return (null, null, new ApiOperationError("website_work_url_invalid", "Enter a valid HTTP/HTTPS website URL."));
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            return (null, null, new ApiOperationError("website_work_url_invalid", "Enter a safe HTTP/HTTPS URL without embedded credentials."));
        }

        return (uri.AbsoluteUri, uri.IdnHost.ToLowerInvariant(), null);
    }

    private static string? CurrentUrl(ProjectTask task)
    {
        var activity = task.Activities
            .Where(x => x.Action == ConfiguredAction)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ThenByDescending(x => x.Id)
            .FirstOrDefault();
        if (activity is null)
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(activity.DetailsJson);
            return json.RootElement.TryGetProperty("url", out var url) ? url.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static WebsiteWorkResponse ToResponse(ProjectTask task)
    {
        var url = CurrentUrl(task) ?? string.Empty;
        var startedAt = task.Activities
            .Where(x => x.Action == StartedAction)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Select(x => (DateTime?)x.CreatedAtUtc)
            .FirstOrDefault();

        return new WebsiteWorkResponse(
            task.Id,
            task.ProjectId,
            task.Project.Code,
            task.Project.Name,
            task.AssigneeEmployeeId,
            task.AssigneeEmployee?.EmployeeCode,
            task.AssigneeEmployee?.FullName,
            task.Title,
            task.Description,
            url,
            task.Status,
            task.Priority,
            task.DueDate,
            startedAt,
            task.CompletedAtUtc,
            task.CreatedAtUtc,
            task.UpdatedAtUtc);
    }

    private static bool TryReadCompletion(TaskActivity activity, out WebsiteWorkCompletionResponse? completion)
    {
        completion = null;
        try
        {
            using var json = JsonDocument.Parse(activity.DetailsJson);
            var root = json.RootElement;
            if (!TryGuid(root, "taskId", out var taskId) ||
                !TryGuid(root, "projectId", out var projectId) ||
                !TryGuid(root, "employeeId", out var employeeId))
            {
                return false;
            }

            var projectName = root.GetProperty("projectName").GetString() ?? string.Empty;
            var employeeCode = root.GetProperty("employeeCode").GetString() ?? string.Empty;
            var employeeName = root.GetProperty("employeeName").GetString() ?? string.Empty;
            var taskTitle = root.GetProperty("taskTitle").GetString() ?? string.Empty;
            var message = root.GetProperty("message").GetString() ?? $"{employeeName} completed: {taskTitle}";
            var completedAt = root.TryGetProperty("completedAtUtc", out var completedElement) && completedElement.TryGetDateTime(out var parsed)
                ? parsed
                : activity.CreatedAtUtc;

            completion = new WebsiteWorkCompletionResponse(
                taskId,
                projectId,
                projectName,
                employeeId,
                employeeCode,
                employeeName,
                taskTitle,
                completedAt,
                message);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return false;
        }
    }

    private static bool TryGuid(JsonElement root, string propertyName, out Guid value)
    {
        value = Guid.Empty;
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        return property.ValueKind == JsonValueKind.String && Guid.TryParse(property.GetString(), out value);
    }

    private void AddActivity(ProjectTask task, Guid? actorUserId, string action, object details)
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
        task.Activities.Add(activity);
    }

    private void AddAudit(RequestActor actor, string action, Guid taskId, object details)
    {
        dbContext.AuditLogs.Add(new AuditLog
        {
            ActorUserId = actor.UserId,
            Action = action,
            TargetType = "ProjectTask",
            TargetId = taskId.ToString(),
            MetadataJson = JsonSerializer.Serialize(details),
            IpAddress = actor.IpAddress,
            UserAgent = actor.UserAgent,
            CreatedAtUtc = UtcNow()
        });
    }

    private static OperationResult<T> Error<T>(ApiOperationError error)
        => error.Code.EndsWith("_not_found", StringComparison.Ordinal)
            ? OperationResult<T>.NotFound(error.Code, error.Message)
            : error.Code is "website_work_duplicate" or "project_closed" or "website_work_closed" or "website_work_project_locked"
                ? OperationResult<T>.Conflict(error.Code, error.Message)
                : OperationResult<T>.Invalid(error.Code, error.Message);

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;
    private static string Normalize(string value) => value.Trim().ToUpperInvariant();
}