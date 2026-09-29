namespace TaskMonitoring.Api.Security;

public static class PermissionCatalog
{
    public const string UsersRead = "users.read";
    public const string UsersManage = "users.manage";
    public const string EmployeesRead = "employees.read";
    public const string EmployeesManage = "employees.manage";
    public const string DepartmentsRead = "departments.read";
    public const string DepartmentsManage = "departments.manage";
    public const string RolesRead = "roles.read";
    public const string RolesManage = "roles.manage";
    public const string ShiftsRead = "shifts.read";
    public const string ShiftsManage = "shifts.manage";
    public const string AttendanceRead = "attendance.read";
    public const string ProjectsRead = "projects.read";
    public const string ProjectsManage = "projects.manage";
    public const string TasksRead = "tasks.read";
    public const string TasksManage = "tasks.manage";
    public const string TasksComment = "tasks.comment";
    public const string AuditRead = "audit.read";

    public static readonly IReadOnlyDictionary<string, string> Definitions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [UsersRead] = "View user accounts.",
        [UsersManage] = "Create, update, disable, and assign user accounts.",
        [EmployeesRead] = "View employee profiles and reporting relationships.",
        [EmployeesManage] = "Create and update employee profiles, access roles, and employment status.",
        [DepartmentsRead] = "View departments and their active status.",
        [DepartmentsManage] = "Create and update departments.",
        [RolesRead] = "View roles and permission assignments.",
        [RolesManage] = "Manage roles and permission assignments.",
        [ShiftsRead] = "View shift definitions and employee shift assignments.",
        [ShiftsManage] = "Create and update shifts and assign shifts to employees.",
        [AttendanceRead] = "View organization-wide attendance and work-session records.",
        [ProjectsRead] = "View projects and project membership.",
        [ProjectsManage] = "Create and update projects and manage project membership.",
        [TasksRead] = "View project tasks, comments, and activity history.",
        [TasksManage] = "Create, edit, assign, and transition project tasks.",
        [TasksComment] = "Add comments to project tasks.",
        [AuditRead] = "View security and administrative audit logs."
    };

    public static IEnumerable<string> All => Definitions.Keys;
}
