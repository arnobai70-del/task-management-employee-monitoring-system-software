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
        [AuditRead] = "View security and administrative audit logs."
    };

    public static IEnumerable<string> All => Definitions.Keys;
}
