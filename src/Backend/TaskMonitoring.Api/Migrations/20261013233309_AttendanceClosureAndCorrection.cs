using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskMonitoring.Api.Migrations
{
    /// <inheritdoc />
    public partial class AttendanceClosureAndCorrection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClosureSource",
                table: "work_sessions",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CorrectedAtUtc",
                table: "work_sessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CorrectedByUserId",
                table: "work_sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CorrectionReason",
                table: "work_sessions",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_work_sessions_ScheduledEndUtc",
                table: "work_sessions",
                column: "ScheduledEndUtc",
                filter: "\"EndedAtUtc\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_work_sessions_ScheduledEndUtc",
                table: "work_sessions");

            migrationBuilder.DropColumn(
                name: "ClosureSource",
                table: "work_sessions");

            migrationBuilder.DropColumn(
                name: "CorrectedAtUtc",
                table: "work_sessions");

            migrationBuilder.DropColumn(
                name: "CorrectedByUserId",
                table: "work_sessions");

            migrationBuilder.DropColumn(
                name: "CorrectionReason",
                table: "work_sessions");
        }
    }
}
