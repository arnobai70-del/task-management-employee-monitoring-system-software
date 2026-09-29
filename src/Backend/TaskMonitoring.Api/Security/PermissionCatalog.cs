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
    public const string SurveysRead = "surveys.read";
    public const string SurveysManage = "surveys.manage";
    public const string SurveyAssignmentsRead = "survey.assignments.read";
    public const string SurveyAssignmentsManage = "survey.assignments.manage";
    public const string SurveySubmit = "survey.submit";
    public const string SurveyReview = "survey.review";
    public const string ReportsRead = "reports.read";
    public const string PresenceRead = "presence.read";
    public const string AccessAssignmentsRead = "access.assignments.read";
    public const string AccessAssignmentsManage = "access.assignments.manage";
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
        [SurveysRead] = "View survey forms and questionnaires.",
        [SurveysManage] = "Create, edit, publish, close, and archive survey forms.",
        [SurveyAssignmentsRead] = "View field survey assignments and submission status.",
        [SurveyAssignmentsManage] = "Assign or cancel survey fieldwork for employees.",
        [SurveySubmit] = "Complete and submit assigned field surveys.",
        [SurveyReview] = "Review, approve, and reject submitted field surveys.",
        [ReportsRead] = "View organization-wide dashboard metrics and operational reports.",
        [PresenceRead] = "View current employee online/offline, working, break, and last-seen presence state.",
        [AccessAssignmentsRead] = "View employee RDP, IP, and website access assignments.",
        [AccessAssignmentsManage] = "Create, update, deactivate, release, and reserve employee RDP, IP, and website access assignments.",
        [AuditRead] = "View security and administrative audit logs."
    };

    public static IEnumerable<string> All => Definitions.Keys;
}
