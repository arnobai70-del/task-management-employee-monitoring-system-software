using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskMonitoring.Api.Migrations
{
    /// <inheritdoc />
    public partial class TransparentMonitoringTelemetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "approved_monitoring_applications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProcessName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    NormalizedProcessName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    CaptureWindowTitle = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_approved_monitoring_applications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "approved_monitoring_domains",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Domain = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    NormalizedDomain = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    IncludeSubdomains = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_approved_monitoring_domains", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "monitoring_activity_segments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ProcessName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    ApplicationName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    WindowTitle = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Domain = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastObservedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SampleIntervalSeconds = table.Column<int>(type: "integer", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_monitoring_activity_segments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_monitoring_activity_segments_employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "monitoring_policies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    SampleIntervalSeconds = table.Column<int>(type: "integer", nullable: false),
                    RetentionDays = table.Column<int>(type: "integer", nullable: false),
                    DisclosureText = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_monitoring_policies", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_approved_monitoring_applications_IsActive_DisplayName",
                table: "approved_monitoring_applications",
                columns: new[] { "IsActive", "DisplayName" });

            migrationBuilder.CreateIndex(
                name: "IX_approved_monitoring_applications_NormalizedProcessName",
                table: "approved_monitoring_applications",
                column: "NormalizedProcessName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_approved_monitoring_domains_IsActive_Domain",
                table: "approved_monitoring_domains",
                columns: new[] { "IsActive", "Domain" });

            migrationBuilder.CreateIndex(
                name: "IX_approved_monitoring_domains_NormalizedDomain",
                table: "approved_monitoring_domains",
                column: "NormalizedDomain",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_activity_segments_Domain",
                table: "monitoring_activity_segments",
                column: "Domain");

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_activity_segments_EmployeeId_LastObservedAtUtc",
                table: "monitoring_activity_segments",
                columns: new[] { "EmployeeId", "LastObservedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_activity_segments_Kind_LastObservedAtUtc",
                table: "monitoring_activity_segments",
                columns: new[] { "Kind", "LastObservedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_activity_segments_ProcessName",
                table: "monitoring_activity_segments",
                column: "ProcessName");

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_policies_CreatedAtUtc",
                table: "monitoring_policies",
                column: "CreatedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "approved_monitoring_applications");

            migrationBuilder.DropTable(
                name: "approved_monitoring_domains");

            migrationBuilder.DropTable(
                name: "monitoring_activity_segments");

            migrationBuilder.DropTable(
                name: "monitoring_policies");
        }
    }
}
