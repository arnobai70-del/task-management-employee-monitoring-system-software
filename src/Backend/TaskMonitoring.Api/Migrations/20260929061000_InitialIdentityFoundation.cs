using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TaskMonitoring.Api.Data;

#nullable disable

namespace TaskMonitoring.Api.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260929061000_InitialIdentityFoundation")]
public partial class InitialIdentityFoundation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "audit_logs",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                Action = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                TargetType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                TargetId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                MetadataJson = table.Column<string>(type: "text", nullable: true),
                IpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                UserAgent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_audit_logs", x => x.Id));

        migrationBuilder.CreateTable(
            name: "permissions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Code = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_permissions", x => x.Id));

        migrationBuilder.CreateTable(
            name: "roles",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                IsActive = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_roles", x => x.Id));

        migrationBuilder.CreateTable(
            name: "users",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                NormalizedEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                PasswordHash = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                IsActive = table.Column<bool>(type: "boolean", nullable: false),
                FailedLoginAttempts = table.Column<int>(type: "integer", nullable: false),
                LockoutEndUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_users", x => x.Id));

        migrationBuilder.CreateTable(
            name: "role_permissions",
            columns: table => new
            {
                RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                PermissionId = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_role_permissions", x => new { x.RoleId, x.PermissionId });
                table.ForeignKey("FK_role_permissions_permissions_PermissionId", x => x.PermissionId, "permissions", "Id", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_role_permissions_roles_RoleId", x => x.RoleId, "roles", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "refresh_tokens",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                CreatedByIp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                RevokedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                RevokedByIp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                RevokeReason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                ReplacedByTokenId = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_refresh_tokens", x => x.Id);
                table.ForeignKey("FK_refresh_tokens_users_UserId", x => x.UserId, "users", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "user_roles",
            columns: table => new
            {
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                RoleId = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_user_roles", x => new { x.UserId, x.RoleId });
                table.ForeignKey("FK_user_roles_roles_RoleId", x => x.RoleId, "roles", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_user_roles_users_UserId", x => x.UserId, "users", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex("IX_audit_logs_ActorUserId_CreatedAtUtc", "audit_logs", new[] { "ActorUserId", "CreatedAtUtc" });
        migrationBuilder.CreateIndex("IX_audit_logs_CreatedAtUtc", "audit_logs", "CreatedAtUtc");
        migrationBuilder.CreateIndex("IX_permissions_Code", "permissions", "Code", unique: true);
        migrationBuilder.CreateIndex("IX_refresh_tokens_TokenHash", "refresh_tokens", "TokenHash", unique: true);
        migrationBuilder.CreateIndex("IX_refresh_tokens_UserId_ExpiresAtUtc", "refresh_tokens", new[] { "UserId", "ExpiresAtUtc" });
        migrationBuilder.CreateIndex("IX_role_permissions_PermissionId", "role_permissions", "PermissionId");
        migrationBuilder.CreateIndex("IX_roles_Name", "roles", "Name", unique: true);
        migrationBuilder.CreateIndex("IX_user_roles_RoleId", "user_roles", "RoleId");
        migrationBuilder.CreateIndex("IX_users_NormalizedEmail", "users", "NormalizedEmail", unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("audit_logs");
        migrationBuilder.DropTable("refresh_tokens");
        migrationBuilder.DropTable("role_permissions");
        migrationBuilder.DropTable("user_roles");
        migrationBuilder.DropTable("permissions");
        migrationBuilder.DropTable("roles");
        migrationBuilder.DropTable("users");
    }
}
