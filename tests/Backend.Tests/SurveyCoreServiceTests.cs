using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;
using TaskMonitoring.Api.Services;

namespace Backend.Tests;

public sealed class SurveyCoreServiceTests
{
    [Fact]
    public async Task Survey_cannot_publish_without_questions()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var project = AddProject(db);
        var form = AddSurvey(db, project, SurveyFormStatus.Draft, addQuestion: false);
        await db.SaveChangesAsync(cancellationToken);

        var result = await Service(db).ChangeFormStatusAsync(form.Id, new ChangeSurveyFormStatusRequest { Status = SurveyFormStatus.Published }, Actor(), cancellationToken);

        Assert.Equal(OperationStatus.Conflict, result.Status);
        Assert.Equal("survey_has_no_questions", result.ErrorCode);
    }

    [Fact]
    public async Task Published_survey_questions_are_locked()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var project = AddProject(db);
        var form = AddSurvey(db, project, SurveyFormStatus.Published);
        await db.SaveChangesAsync(cancellationToken);

        var result = await Service(db).ReplaceQuestionsAsync(form.Id, new ReplaceSurveyQuestionsRequest
        {
            Questions = [new SurveyQuestionInput { Key = "name", Prompt = "New prompt", Type = SurveyQuestionType.Text }]
        }, Actor(), cancellationToken);

        Assert.Equal(OperationStatus.Conflict, result.Status);
        Assert.Equal("survey_questions_locked", result.ErrorCode);
    }

    [Fact]
    public async Task Assignment_requires_active_employee()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var project = AddProject(db);
        var form = AddSurvey(db, project, SurveyFormStatus.Published);
        var (_, employee) = AddEmployee(db, isActive: false);
        await db.SaveChangesAsync(cancellationToken);

        var result = await Service(db).AssignAsync(new CreateSurveyAssignmentRequest { SurveyFormId = form.Id, EmployeeId = employee.Id }, Actor(), cancellationToken);

        Assert.Equal(OperationStatus.Conflict, result.Status);
        Assert.Equal("employee_inactive", result.ErrorCode);
    }

    [Fact]
    public async Task Submission_requires_assignment_owner()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var project = AddProject(db);
        var form = AddSurvey(db, project, SurveyFormStatus.Published);
        var (_, employee) = AddEmployee(db);
        var (otherUser, _) = AddEmployee(db);
        var assignment = AddAssignment(db, form, employee);
        await db.SaveChangesAsync(cancellationToken);

        var result = await Service(db).SaveDraftAsync(assignment.Id, new SaveSurveySubmissionRequest(), Actor(otherUser.Id), cancellationToken);

        Assert.Equal(OperationStatus.Invalid, result.Status);
        Assert.Equal("survey_assignment_not_owned", result.ErrorCode);
    }

    [Fact]
    public async Task Required_answers_are_enforced_on_submit_but_not_draft()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var project = AddProject(db);
        var form = AddSurvey(db, project, SurveyFormStatus.Published);
        var (user, employee) = AddEmployee(db);
        var assignment = AddAssignment(db, form, employee);
        await db.SaveChangesAsync(cancellationToken);
        var service = Service(db);

        var draft = await service.SaveDraftAsync(assignment.Id, new SaveSurveySubmissionRequest(), Actor(user.Id), cancellationToken);
        var submitted = await service.SubmitAsync(assignment.Id, new SaveSurveySubmissionRequest(), Actor(user.Id), cancellationToken);

        Assert.Equal(OperationStatus.Success, draft.Status);
        Assert.Equal(OperationStatus.Invalid, submitted.Status);
        Assert.Equal("survey_required_answer_missing", submitted.ErrorCode);
    }

    [Fact]
    public async Task Rejected_submission_resubmits_as_new_revision_and_preserves_history()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var project = AddProject(db);
        var form = AddSurvey(db, project, SurveyFormStatus.Published);
        var question = form.Questions.Single();
        var (surveyorUser, employee) = AddEmployee(db);
        var reviewer = new User
        {
            Email = "reviewer@example.com",
            NormalizedEmail = "REVIEWER@EXAMPLE.COM",
            PasswordHash = "test-hash",
            IsActive = true
        };
        db.Users.Add(reviewer);
        var assignment = AddAssignment(db, form, employee);
        await db.SaveChangesAsync(cancellationToken);
        var service = Service(db);
        var answers = new SaveSurveySubmissionRequest
        {
            Answers = [new SurveyAnswerInput { QuestionId = question.Id, Value = JsonSerializer.SerializeToElement("first") }]
        };

        var first = await service.SubmitAsync(assignment.Id, answers, Actor(surveyorUser.Id), cancellationToken);
        Assert.Equal(OperationStatus.Success, first.Status);
        var rejected = await service.ReviewAsync(first.Value!.Id, new ReviewSurveySubmissionRequest { Decision = SurveyReviewDecision.Reject, Comment = "Fix response." }, Actor(reviewer.Id), cancellationToken);
        Assert.Equal(OperationStatus.Success, rejected.Status);

        var second = await service.SubmitAsync(assignment.Id, new SaveSurveySubmissionRequest
        {
            Answers = [new SurveyAnswerInput { QuestionId = question.Id, Value = JsonSerializer.SerializeToElement("corrected") }]
        }, Actor(surveyorUser.Id), cancellationToken);

        Assert.Equal(OperationStatus.Success, second.Status);
        Assert.Equal(2, second.Value!.RevisionNumber);
        Assert.Equal(2, await db.SurveySubmissions.CountAsync(x => x.SurveyAssignmentId == assignment.Id, cancellationToken));
        Assert.Equal(1, await db.SurveySubmissions.CountAsync(x => x.Status == SurveySubmissionStatus.Rejected, cancellationToken));
        Assert.Equal(1, await db.SurveySubmissions.CountAsync(x => x.Status == SurveySubmissionStatus.Submitted, cancellationToken));
    }

    [Fact]
    public async Task Choice_answer_must_match_configured_options()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var project = AddProject(db);
        var form = AddSurvey(db, project, SurveyFormStatus.Published, questionType: SurveyQuestionType.SingleChoice, optionsJson: "[\"Yes\",\"No\"]");
        var question = form.Questions.Single();
        var (user, employee) = AddEmployee(db);
        var assignment = AddAssignment(db, form, employee);
        await db.SaveChangesAsync(cancellationToken);

        var result = await Service(db).SubmitAsync(assignment.Id, new SaveSurveySubmissionRequest
        {
            Answers = [new SurveyAnswerInput { QuestionId = question.Id, Value = JsonSerializer.SerializeToElement("Maybe") }]
        }, Actor(user.Id), cancellationToken);

        Assert.Equal(OperationStatus.Invalid, result.Status);
        Assert.Equal("survey_answer_value_invalid", result.ErrorCode);
    }

    [Fact]
    public async Task Survey_cannot_close_while_submissions_wait_for_review()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var db = CreateDbContext();
        var project = AddProject(db);
        var form = AddSurvey(db, project, SurveyFormStatus.Published);
        var question = form.Questions.Single();
        var (user, employee) = AddEmployee(db);
        var assignment = AddAssignment(db, form, employee);
        await db.SaveChangesAsync(cancellationToken);
        var service = Service(db);
        var submitted = await service.SubmitAsync(assignment.Id, new SaveSurveySubmissionRequest
        {
            Answers = [new SurveyAnswerInput { QuestionId = question.Id, Value = JsonSerializer.SerializeToElement("done") }]
        }, Actor(user.Id), cancellationToken);
        Assert.Equal(OperationStatus.Success, submitted.Status);

        var close = await service.ChangeFormStatusAsync(form.Id, new ChangeSurveyFormStatusRequest { Status = SurveyFormStatus.Closed }, Actor(), cancellationToken);

        Assert.Equal(OperationStatus.Conflict, close.Status);
        Assert.Equal("survey_has_pending_reviews", close.ErrorCode);
    }

    private static AppDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static SurveyCoreService Service(AppDbContext db) => new(db, new FixedTimeProvider(new DateTimeOffset(UtcNow)));
    private static DateTime UtcNow => DateTime.Parse("2026-09-29T08:00:00Z").ToUniversalTime();

    private static Project AddProject(AppDbContext db)
    {
        var code = $"SUR-{Guid.NewGuid():N}"[..12];
        var project = new Project
        {
            Code = code,
            NormalizedCode = code.ToUpperInvariant(),
            Name = "Survey Field Project",
            NormalizedName = "SURVEY FIELD PROJECT",
            Status = ProjectStatus.Active,
            StartDate = new DateOnly(2026, 9, 1),
            DueDate = new DateOnly(2026, 12, 31)
        };
        db.Projects.Add(project);
        return project;
    }

    private static SurveyForm AddSurvey(AppDbContext db, Project project, SurveyFormStatus status, bool addQuestion = true, SurveyQuestionType questionType = SurveyQuestionType.Text, string optionsJson = "[]")
    {
        var code = $"FORM-{Guid.NewGuid():N}"[..14];
        var form = new SurveyForm
        {
            ProjectId = project.Id,
            Project = project,
            Code = code,
            NormalizedCode = code.ToUpperInvariant(),
            Name = "Household Survey",
            NormalizedName = "HOUSEHOLD SURVEY",
            Status = status,
            CreatedAtUtc = UtcNow,
            UpdatedAtUtc = UtcNow
        };
        db.SurveyForms.Add(form);
        if (addQuestion)
        {
            form.Questions.Add(new SurveyQuestion
            {
                SurveyFormId = form.Id,
                SurveyForm = form,
                Key = "answer",
                NormalizedKey = "ANSWER",
                Prompt = "Required answer",
                Type = questionType,
                IsRequired = true,
                OptionsJson = optionsJson,
                SortOrder = 1
            });
        }
        return form;
    }

    private static (User User, Employee Employee) AddEmployee(AppDbContext db, bool isActive = true)
    {
        var id = Guid.NewGuid().ToString("N");
        var user = new User
        {
            Email = $"survey-{id}@example.com",
            NormalizedEmail = $"SURVEY-{id}@EXAMPLE.COM",
            PasswordHash = "test-hash",
            IsActive = isActive
        };
        var employee = new Employee
        {
            UserId = user.Id,
            User = user,
            EmployeeCode = $"S-{id}"[..12],
            NormalizedEmployeeCode = $"S-{id}"[..12].ToUpperInvariant(),
            FullName = $"Survey Employee {id[..8]}",
            NormalizedFullName = $"SURVEY EMPLOYEE {id[..8]}".ToUpperInvariant(),
            JobTitle = "Surveyor",
            IsActive = isActive
        };
        db.Users.Add(user);
        db.Employees.Add(employee);
        return (user, employee);
    }

    private static SurveyAssignment AddAssignment(AppDbContext db, SurveyForm form, Employee employee)
    {
        var assignment = new SurveyAssignment
        {
            SurveyFormId = form.Id,
            SurveyForm = form,
            EmployeeId = employee.Id,
            Employee = employee,
            Status = SurveyAssignmentStatus.Assigned,
            DueDate = new DateOnly(2026, 10, 31),
            CreatedAtUtc = UtcNow,
            UpdatedAtUtc = UtcNow
        };
        db.SurveyAssignments.Add(assignment);
        return assignment;
    }

    private static RequestActor Actor(Guid? userId = null) => new(userId, "127.0.0.1", "tests");

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
