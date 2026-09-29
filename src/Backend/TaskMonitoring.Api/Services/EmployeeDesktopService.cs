using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;

namespace TaskMonitoring.Api.Services;

public interface IEmployeeDesktopService
{
    Task<OperationResult<EmployeeDesktopDashboardResponse>> GetDashboardAsync(RequestActor actor, CancellationToken cancellationToken);
    Task<OperationResult<DesktopHeartbeatResponse>> RecordHeartbeatAsync(DesktopHeartbeatRequest request, RequestActor actor, CancellationToken cancellationToken);
    Task<PagedResponse<EmployeePresenceResponse>> GetPresenceAsync(bool? online, int page, int pageSize, CancellationToken cancellationToken);
}

public sealed class EmployeeDesktopService(
    AppDbContext dbContext,
    IAttendanceCoreService attendanceCoreService,
    IProjectTaskCoreService projectTaskCoreService,
    IAccessAssignmentService accessAssignmentService,
    TimeProvider timeProvider) : IEmployeeDesktopService
{
    private static readonly MonitoringDisclosureResponse Disclosure = new(
        "Provide employee self-service, attendance controls, assigned work/access information, and a transparent online-status heartbeat for business operations.",
        new[]
        {
            "Authenticated employee identity",
            "Last heartbeat time",
            "Desktop client version",
            "Platform name",
            "Attendance state derived by the server",
            "Assigned tasks and business access records requested by the employee client"
        },
        new[]
        {
            "Keystrokes or passwords",
            "Clipboard contents",
            "Microphone or camera recordings",
            "Hidden screenshots",
            "Browser history",
            "Unrelated private files",
            "Application-window contents"
        });

    public async Task<OperationResult<EmployeeDesktopDashboardResponse>> GetDashboardAsync(RequestActor actor, CancellationToken cancellationToken)
    {
        var employeeResult = await ResolveEmployeeAsync(actor, cancellationToken);
        if (employeeResult.Status != OperationStatus.Success || employeeResult.Value is null)
        {
            return Forward<EmployeeRecord, EmployeeDesktopDashboardResponse>(employeeResult);
        }

        var employee = employeeResult.Value;
        var attendanceResult = await attendanceCoreService.GetMyStatusAsync(actor, cancellationToken);
        if (attendanceResult.Status != OperationStatus.Success || attendanceResult.Value is null)
        {
            return Forward<AttendanceStateResponse, EmployeeDesktopDashboardResponse>(attendanceResult);
        }

        var tasks = await projectTaskCoreService.GetTasksAsync(
            projectId: null,
            search: null,
            status: null,
            priority: null,
            assigneeEmployeeId: employee.Id,
            page: 1,
            pageSize: 100,
            cancellationToken);

        var rdp = await accessAssignmentService.GetRdpAssignmentsAsync(employee.Id, true, null, 1, 100, cancellationToken);
        var ip = await accessAssignmentService.GetIpAssignmentsAsync(employee.Id, null, null, 1, 100, cancellationToken);
        var websites = await accessAssignmentService.GetWebsiteAssignmentsAsync(employee.Id, true, null, 1, 100, cancellationToken);

        var profile = new EmployeeDesktopProfileResponse(
            employee.Id,
            employee.EmployeeCode,
            employee.FullName,
            employee.Email,
            employee.JobTitle,
            employee.DepartmentName,
            employee.SupervisorName,
            employee.IsActive);

        var response = new EmployeeDesktopDashboardResponse(
            profile,
            attendanceResult.Value,
            tasks.Items,
            rdp.Items,
            ip.Items.Where(x => x.Status != IpAssignmentStatus.Released).ToArray(),
            websites.Items,
            Disclosure,
            UtcNow());

        return OperationResult<EmployeeDesktopDashboardResponse>.Success(response);
    }

    public async Task<OperationResult<DesktopHeartbeatResponse>> RecordHeartbeatAsync(
        DesktopHeartbeatRequest request,
        RequestActor actor,
        CancellationToken cancellationToken)
    {
        var employeeResult = await ResolveEmployeeAsync(actor, cancellationToken);
        if (employeeResult.Status != OperationStatus.Success || employeeResult.Value is null)
        {
            return Forward<EmployeeRecord, DesktopHeartbeatResponse>(employeeResult);
        }

        var attendanceResult = await attendanceCoreService.GetMyStatusAsync(actor, cancellationToken);
        if (attendanceResult.Status != OperationStatus.Success || attendanceResult.Value is null)
        {
            return Forward<AttendanceStateResponse, DesktopHeartbeatResponse>(attendanceResult);
        }

        var employee = employeeResult.Value;
        var now = UtcNow();
        var clientVersion = request.ClientVersion.Trim();
        var platform = request.Platform.Trim();
        var state = attendanceResult.Value.State.ToString();
        var presenceSet = dbContext.Set<EmployeeClientPresence>();

        var presence = await presenceSet.SingleOrDefaultAsync(x => x.EmployeeId == employee.Id, cancellationToken);
        var added = presence is null;

        if (presence is null)
        {
            presence = new EmployeeClientPresence
            {
                EmployeeId = employee.Id,
                ClientVersion = clientVersion,
                Platform = platform,
                AttendanceState = state,
                LastSeenAtUtc = now,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            presenceSet.Add(presence);
        }
        else
        {
            ApplyHeartbeat(presence, clientVersion, platform, state, now);
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException) when (added)
        {
            dbContext.Entry(presence).State = EntityState.Detached;
            presence = await presenceSet.SingleAsync(x => x.EmployeeId == employee.Id, cancellationToken);
            ApplyHeartbeat(presence, clientVersion, platform, state, now);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return OperationResult<DesktopHeartbeatResponse>.Success(new DesktopHeartbeatResponse(
            now,
            presence.LastSeenAtUtc,
            attendanceResult.Value.State,
            RecommendedHeartbeatSeconds));
    }

    public async Task<PagedResponse<EmployeePresenceResponse>> GetPresenceAsync(
        bool? online,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = Math.Clamp(page, 1, 1_000_000);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var cutoff = UtcNow().AddSeconds(-OnlineWindowSeconds);

        var query = dbContext.Set<EmployeeClientPresence>()
            .AsNoTracking()
            .Where(x => x.Employee.IsActive);

        if (online.HasValue)
        {
            query = online.Value
                ? query.Where(x => x.LastSeenAtUtc >= cutoff)
                : query.Where(x => x.LastSeenAtUtc < cutoff);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(x => x.LastSeenAtUtc)
            .ThenBy(x => x.Employee.NormalizedFullName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new EmployeePresenceResponse(
                x.EmployeeId,
                x.Employee.EmployeeCode,
                x.Employee.FullName,
                x.LastSeenAtUtc,
                x.LastSeenAtUtc >= cutoff,
                x.ClientVersion,
                x.Platform,
                x.AttendanceState))
            .ToListAsync(cancellationToken);

        return new PagedResponse<EmployeePresenceResponse>(items, page, pageSize, total);
    }

    private async Task<OperationResult<EmployeeRecord>> ResolveEmployeeAsync(RequestActor actor, CancellationToken cancellationToken)
    {
        if (!actor.UserId.HasValue)
        {
            return OperationResult<EmployeeRecord>.Invalid("invalid_session", "The authenticated user identity is missing.");
        }

        var employee = await dbContext.Employees
            .AsNoTracking()
            .Where(x => x.UserId == actor.UserId.Value)
            .Select(x => new EmployeeRecord(
                x.Id,
                x.EmployeeCode,
                x.FullName,
                x.User.Email,
                x.JobTitle,
                x.Department != null ? x.Department.Name : null,
                x.Supervisor != null ? x.Supervisor.FullName : null,
                x.IsActive && x.User.IsActive))
            .SingleOrDefaultAsync(cancellationToken);

        if (employee is null)
        {
            return OperationResult<EmployeeRecord>.NotFound("employee_profile_not_found", "No employee profile is linked to this account.");
        }

        return employee.IsActive
            ? OperationResult<EmployeeRecord>.Success(employee)
            : OperationResult<EmployeeRecord>.Invalid("employee_inactive", "The employee account is inactive.");
    }

    private static OperationResult<TOut> Forward<TIn, TOut>(OperationResult<TIn> source)
        => source.Status switch
        {
            OperationStatus.NotFound => OperationResult<TOut>.NotFound(source.ErrorCode ?? "not_found", source.Message ?? "The requested record was not found."),
            OperationStatus.Conflict => OperationResult<TOut>.Conflict(source.ErrorCode ?? "conflict", source.Message ?? "The request conflicts with the current state."),
            _ => OperationResult<TOut>.Invalid(source.ErrorCode ?? "invalid_request", source.Message ?? "The request could not be completed.")
        };

    private static void ApplyHeartbeat(EmployeeClientPresence presence, string clientVersion, string platform, string state, DateTime now)
    {
        presence.ClientVersion = clientVersion;
        presence.Platform = platform;
        presence.AttendanceState = state;
        presence.LastSeenAtUtc = now;
        presence.UpdatedAtUtc = now;
    }

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;

    private const int RecommendedHeartbeatSeconds = 60;
    private const int OnlineWindowSeconds = 150;

    private sealed record EmployeeRecord(
        Guid Id,
        string EmployeeCode,
        string FullName,
        string Email,
        string JobTitle,
        string? DepartmentName,
        string? SupervisorName,
        bool IsActive);
}
