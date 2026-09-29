using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskMonitoring.Api.Migrations
{
    /// <inheritdoc />
    public partial class SurveyFieldOperationsCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "survey_forms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    NormalizedCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_survey_forms", x => x.Id);
                    table.ForeignKey(
                        name: "FK_survey_forms_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "survey_assignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SurveyFormId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_survey_assignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_survey_assignments_employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_survey_assignments_survey_forms_SurveyFormId",
                        column: x => x.SurveyFormId,
                        principalTable: "survey_forms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_survey_assignments_users_AssignedByUserId",
                        column: x => x.AssignedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "survey_questions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SurveyFormId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NormalizedKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Prompt = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    IsRequired = table.Column<bool>(type: "boolean", nullable: false),
                    OptionsJson = table.Column<string>(type: "jsonb", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_survey_questions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_survey_questions_survey_forms_SurveyFormId",
                        column: x => x.SurveyFormId,
                        principalTable: "survey_forms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "survey_submissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SurveyAssignmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionNumber = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    SubmittedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReviewedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReviewedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewComment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_survey_submissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_survey_submissions_survey_assignments_SurveyAssignmentId",
                        column: x => x.SurveyAssignmentId,
                        principalTable: "survey_assignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_survey_submissions_users_ReviewedByUserId",
                        column: x => x.ReviewedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "survey_answers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SurveySubmissionId = table.Column<Guid>(type: "uuid", nullable: false),
                    SurveyQuestionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ValueJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_survey_answers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_survey_answers_survey_questions_SurveyQuestionId",
                        column: x => x.SurveyQuestionId,
                        principalTable: "survey_questions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_survey_answers_survey_submissions_SurveySubmissionId",
                        column: x => x.SurveySubmissionId,
                        principalTable: "survey_submissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_survey_answers_SurveyQuestionId",
                table: "survey_answers",
                column: "SurveyQuestionId");

            migrationBuilder.CreateIndex(
                name: "IX_survey_answers_SurveySubmissionId_SurveyQuestionId",
                table: "survey_answers",
                columns: new[] { "SurveySubmissionId", "SurveyQuestionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_survey_assignments_AssignedByUserId",
                table: "survey_assignments",
                column: "AssignedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_survey_assignments_DueDate",
                table: "survey_assignments",
                column: "DueDate");

            migrationBuilder.CreateIndex(
                name: "IX_survey_assignments_EmployeeId_Status",
                table: "survey_assignments",
                columns: new[] { "EmployeeId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_survey_assignments_SurveyFormId_EmployeeId",
                table: "survey_assignments",
                columns: new[] { "SurveyFormId", "EmployeeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_survey_assignments_SurveyFormId_Status",
                table: "survey_assignments",
                columns: new[] { "SurveyFormId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_survey_forms_NormalizedCode",
                table: "survey_forms",
                column: "NormalizedCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_survey_forms_NormalizedName",
                table: "survey_forms",
                column: "NormalizedName");

            migrationBuilder.CreateIndex(
                name: "IX_survey_forms_ProjectId_Status",
                table: "survey_forms",
                columns: new[] { "ProjectId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_survey_questions_SurveyFormId_NormalizedKey",
                table: "survey_questions",
                columns: new[] { "SurveyFormId", "NormalizedKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_survey_questions_SurveyFormId_SortOrder",
                table: "survey_questions",
                columns: new[] { "SurveyFormId", "SortOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_survey_submissions_ReviewedByUserId",
                table: "survey_submissions",
                column: "ReviewedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_survey_submissions_Status_SubmittedAtUtc",
                table: "survey_submissions",
                columns: new[] { "Status", "SubmittedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_survey_submissions_SurveyAssignmentId_RevisionNumber",
                table: "survey_submissions",
                columns: new[] { "SurveyAssignmentId", "RevisionNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "survey_answers");

            migrationBuilder.DropTable(
                name: "survey_questions");

            migrationBuilder.DropTable(
                name: "survey_submissions");

            migrationBuilder.DropTable(
                name: "survey_assignments");

            migrationBuilder.DropTable(
                name: "survey_forms");
        }
    }
}
