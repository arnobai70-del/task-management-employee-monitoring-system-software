using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskMonitoring.Api.Migrations
{
    /// <summary>
    /// Data-only (G-27): adds the employees.scope.all permission and grants it to every role that can already read
    /// employee data, so team scoping changes nothing until an administrator removes it from a role.
    /// </summary>
    public partial class EmployeeScopeAll : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO permissions ("Id", "Code", "Description")
                SELECT gen_random_uuid(), 'employees.scope.all',
                       'See every employee in employee lists, attendance, presence, monitoring activity, website work and productivity reports. Without it, those views show only the user and the employees who report to them, directly or indirectly.'
                WHERE NOT EXISTS (SELECT 1 FROM permissions WHERE "Code" = 'employees.scope.all');

                INSERT INTO role_permissions ("RoleId", "PermissionId")
                SELECT DISTINCT rp."RoleId", scope."Id"
                FROM role_permissions rp
                JOIN permissions p ON p."Id" = rp."PermissionId"
                CROSS JOIN (SELECT "Id" FROM permissions WHERE "Code" = 'employees.scope.all') scope
                WHERE p."Code" IN ('employees.read', 'attendance.read', 'presence.read', 'monitoring.read', 'tasks.read', 'tasks.manage', 'reports.read')
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DELETE FROM permissions WHERE "Code" = 'employees.scope.all';""");
        }
    }
}
