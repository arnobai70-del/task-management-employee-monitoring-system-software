using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskMonitoring.Api.Contracts;
using TaskMonitoring.Api.Data;
using TaskMonitoring.Api.Domain;

namespace TaskMonitoring.Api.Services;

public interface ISurveyCoreService
{
    Task<PagedResponse<SurveyFormResponse>> GetFormsAsync(string? search, Guid? projectId, SurveyFormStatus? status, int page, int pageSize, CancellationToken cancellationToken);
    Task<OperationResult<SurveyFormDetailResponse>> GetFormAsync(Guid id, CancellationToken cancellationToken);
    Task<OperationResult<SurveyFormDetailResponse>> CreateFormAsync(CreateSurveyFormRequest request, RequestActor actor, CancellationToken cancellationToken);
    Task<OperationResult<SurveyFormDetailResponse>> UpdateFormAsync(Guid id, UpdateSurveyFormRequest request, RequestActor actor, CancellationToken cancellationToken);
    Task<OperationResult<SurveyFormDetailResponse>> ReplaceQuestionsAsync(Guid id, ReplaceSurveyQuestionsRequest request, RequestActor actor, CancellationToken cancellationToken);
    Task<OperationResult<SurveyFormDetailResponse>> ChangeFormStatusAsync(Guid id, ChangeSurveyFormStatusRequest request, RequestActor actor, CancellationToken cancellationToken);
    Task<PagedResponse<SurveyAssignmentResponse>> GetAssignmentsAsync(Guid? surveyFormId, Guid? employeeId, SurveyAssignmentStatus? status, int page, int pageSize, CancellationToken cancellationToken);
    Task<OperationResult<SurveyAssignmentResponse>> AssignAsync(CreateSurveyAssignmentRequest request, RequestActor actor, CancellationToken cancellationToken);
    Task<OperationResult<SurveyAssignmentResponse>> CancelAssignmentAsync(Guid id, RequestActor actor, CancellationToken cancellationToken);
    Task<OperationResult<IReadOnlyCollection<SurveyAssignmentResponse>>> GetMyAssignmentsAsync(RequestActor actor, CancellationToken cancellationToken);
    Task<OperationResult<SurveySubmissionResponse>> SaveDraftAsync(Guid assignmentId, SaveSurveySubmissionRequest request, RequestActor actor, CancellationToken cancellationToken);
    Task<OperationResult<SurveySubmissionResponse>> SubmitAsync(Guid assignmentId, SaveSurveySubmissionRequest request, RequestActor actor, CancellationToken cancellationToken);
    Task<PagedResponse<SurveySubmissionResponse>> GetPendingSubmissionsAsync(Guid? surveyFormId, int page, int pageSize, CancellationToken cancellationToken);
    Task<OperationResult<SurveySubmissionResponse>> ReviewAsync(Guid submissionId, ReviewSurveySubmissionRequest request, RequestActor actor, CancellationToken cancellationToken);
}

public sealed class SurveyCoreService(AppDbContext dbContext, TimeProvider timeProvider) : ISurveyCoreService
{
    public async Task<PagedResponse<SurveyFormResponse>> GetFormsAsync(string? search, Guid? projectId, SurveyFormStatus? status, int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Clamp(page, 1, 1_000_000);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = dbContext.SurveyForms.AsNoTracking();

        if (projectId.HasValue) query = query.Where(x => x.ProjectId == projectId.Value);
        if (status.HasValue) query = query.Where(x => x.Status == status.Value);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var normalized = Normalize(search);
            query = query.Where(x => x.NormalizedCode.Contains(normalized) || x.NormalizedName.Contains(normalized));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(x => x.NormalizedName)
            .ThenBy(x => x.NormalizedCode)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new SurveyFormResponse(
                x.Id, x.ProjectId, x.Project.Code, x.Project.Name, x.Code, x.Name, x.Description, x.Status,
                x.Questions.Count, x.Assignments.Count(a => a.Status != SurveyAssignmentStatus.Cancelled),
                x.CreatedAtUtc, x.UpdatedAtUtc))
            .ToListAsync(cancellationToken);

        return new PagedResponse<SurveyFormResponse>(items, page, pageSize, totalCount);
    }

    public async Task<OperationResult<SurveyFormDetailResponse>> GetFormAsync(Guid id, CancellationToken cancellationToken)
    {
        var form = await LoadFormAsync(id, tracking: false, cancellationToken);
        return form is null
            ? OperationResult<SurveyFormDetailResponse>.NotFound("survey_not_found", "Survey form was not found.")
            : OperationResult<SurveyFormDetailResponse>.Success(ToFormDetail(form));
    }

    public async Task<OperationResult<SurveyFormDetailResponse>> CreateFormAsync(CreateSurveyFormRequest request, RequestActor actor, CancellationToken cancellationToken)
    {
        var code = request.Code.Trim();
        var name = request.Name.Trim();
        var description = CleanOptional(request.Description);
        if (request.ProjectId == Guid.Empty || code.Length is < 1 or > 50 || name.Length is < 2 or > 200)
            return OperationResult<SurveyFormDetailResponse>.Invalid("survey_invalid", "Project, code, and name are required and must be within allowed lengths.");

        var project = await dbContext.Projects.SingleOrDefaultAsync(x => x.Id == request.ProjectId, cancellationToken);
        if (project is null) return OperationResult<SurveyFormDetailResponse>.NotFound("project_not_found", "Project was not found.");
        if (project.Status is ProjectStatus.Completed or ProjectStatus.Archived)
            return OperationResult<SurveyFormDetailResponse>.Conflict("project_closed", "A survey cannot be created for a completed or archived project.");

        var normalizedCode = Normalize(code);
        if (await dbContext.SurveyForms.AnyAsync(x => x.NormalizedCode == normalizedCode, cancellationToken))
            return OperationResult<SurveyFormDetailResponse>.Conflict("survey_code_exists", "A survey form with this code already exists.");

        var now = UtcNow();
        var form = new SurveyForm
        {
            ProjectId = project.Id,
            Project = project,
            Code = code,
            NormalizedCode = normalizedCode,
            Name = name,
            NormalizedName = Normalize(name),
            Description = description,
            Status = SurveyFormStatus.Draft,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        dbContext.SurveyForms.Add(form);
        AddAudit(actor, "survey.created", "SurveyForm", form.Id, new { form.Code, form.Name, form.ProjectId });
        await dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult<SurveyFormDetailResponse>.Success(ToFormDetail(form));
    }

    public async Task<OperationResult<SurveyFormDetailResponse>> UpdateFormAsync(Guid id, UpdateSurveyFormRequest request, RequestActor actor, CancellationToken cancellationToken)
    {
        var form = await LoadFormAsync(id, tracking: true, cancellationToken);
        if (form is null) return OperationResult<SurveyFormDetailResponse>.NotFound("survey_not_found", "Survey form was not found.");
        if (form.Status != SurveyFormStatus.Draft)
            return OperationResult<SurveyFormDetailResponse>.Conflict("survey_not_editable", "Only draft survey forms can be edited.");

        var code = request.Code.Trim();
        var name = request.Name.Trim();
        if (code.Length is < 1 or > 50 || name.Length is < 2 or > 200)
            return OperationResult<SurveyFormDetailResponse>.Invalid("survey_invalid", "Code and name are required and must be within allowed lengths.");

        var normalizedCode = Normalize(code);
        if (await dbContext.SurveyForms.AnyAsync(x => x.Id != id && x.NormalizedCode == normalizedCode, cancellationToken))
            return OperationResult<SurveyFormDetailResponse>.Conflict("survey_code_exists", "A survey form with this code already exists.");

        form.Code = code;
        form.NormalizedCode = normalizedCode;
        form.Name = name;
        form.NormalizedName = Normalize(name);
        form.Description = CleanOptional(request.Description);
        form.UpdatedAtUtc = UtcNow();
        AddAudit(actor, "survey.updated", "SurveyForm", form.Id, new { form.Code, form.Name });
        await dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult<SurveyFormDetailResponse>.Success(ToFormDetail(form));
    }

    public async Task<OperationResult<SurveyFormDetailResponse>> ReplaceQuestionsAsync(Guid id, ReplaceSurveyQuestionsRequest request, RequestActor actor, CancellationToken cancellationToken)
    {
        var form = await LoadFormAsync(id, tracking: true, cancellationToken);
        if (form is null) return OperationResult<SurveyFormDetailResponse>.NotFound("survey_not_found", "Survey form was not found.");
        if (form.Status != SurveyFormStatus.Draft)
            return OperationResult<SurveyFormDetailResponse>.Conflict("survey_questions_locked", "Questions cannot be changed after the survey is published.");
        if (request.Questions.Count > 100)
            return OperationResult<SurveyFormDetailResponse>.Invalid("survey_too_many_questions", "A survey can contain at most 100 questions.");

        var normalizedKeys = new HashSet<string>(StringComparer.Ordinal);
        var questions = new List<SurveyQuestion>();
        var order = 1;
        foreach (var input in request.Questions)
        {
            var validation = ValidateQuestion(input);
            if (validation.ErrorCode is not null)
                return OperationResult<SurveyFormDetailResponse>.Invalid(validation.ErrorCode, validation.Message!);
            if (!normalizedKeys.Add(validation.NormalizedKey!))
                return OperationResult<SurveyFormDetailResponse>.Invalid("survey_question_key_duplicate", "Question keys must be unique within a survey.");

            questions.Add(new SurveyQuestion
            {
                SurveyFormId = form.Id,
                SurveyForm = form,
                Key = validation.Key!,
                NormalizedKey = validation.NormalizedKey!,
                Prompt = validation.Prompt!,
                Type = input.Type,
                IsRequired = input.IsRequired,
                OptionsJson = JsonSerializer.Serialize(validation.Options),
                SortOrder = order++
            });
        }

        dbContext.SurveyQuestions.RemoveRange(form.Questions);
        form.Questions.Clear();
        foreach (var question in questions) form.Questions.Add(question);
        form.UpdatedAtUtc = UtcNow();
        AddAudit(actor, "survey.questions.replaced", "SurveyForm", form.Id, new { Count = questions.Count });
        await dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult<SurveyFormDetailResponse>.Success(ToFormDetail(form));
    }

    public async Task<OperationResult<SurveyFormDetailResponse>> ChangeFormStatusAsync(Guid id, ChangeSurveyFormStatusRequest request, RequestActor actor, CancellationToken cancellationToken)
    {
        var form = await LoadFormAsync(id, tracking: true, cancellationToken);
        if (form is null) return OperationResult<SurveyFormDetailResponse>.NotFound("survey_not_found", "Survey form was not found.");
        if (!Enum.IsDefined(request.Status))
            return OperationResult<SurveyFormDetailResponse>.Invalid("survey_status_invalid", "Survey status is invalid.");
        if (request.Status == form.Status) return OperationResult<SurveyFormDetailResponse>.Success(ToFormDetail(form));

        var allowed = (form.Status, request.Status) switch
        {
            (SurveyFormStatus.Draft, SurveyFormStatus.Published) => true,
            (SurveyFormStatus.Published, SurveyFormStatus.Closed) => true,
            (SurveyFormStatus.Draft, SurveyFormStatus.Archived) => true,
            (SurveyFormStatus.Closed, SurveyFormStatus.Archived) => true,
            _ => false
        };
        if (!allowed) return OperationResult<SurveyFormDetailResponse>.Conflict("survey_status_transition_invalid", "This survey status transition is not allowed.");
        if (request.Status == SurveyFormStatus.Published && form.Questions.Count == 0)
            return OperationResult<SurveyFormDetailResponse>.Conflict("survey_has_no_questions", "A survey must contain at least one question before it can be published.");
        if (request.Status is SurveyFormStatus.Closed or SurveyFormStatus.Archived)
        {
            var pending = await dbContext.SurveyAssignments.AnyAsync(x => x.SurveyFormId == id && x.Status == SurveyAssignmentStatus.Submitted, cancellationToken);
            if (pending) return OperationResult<SurveyFormDetailResponse>.Conflict("survey_has_pending_reviews", "Submitted survey responses must be reviewed before closing or archiving the survey.");
        }

        var previous = form.Status;
        form.Status = request.Status;
        form.UpdatedAtUtc = UtcNow();
        AddAudit(actor, "survey.status.changed", "SurveyForm", form.Id, new { From = previous, To = form.Status });
        await dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult<SurveyFormDetailResponse>.Success(ToFormDetail(form));
    }

    public async Task<PagedResponse<SurveyAssignmentResponse>> GetAssignmentsAsync(Guid? surveyFormId, Guid? employeeId, SurveyAssignmentStatus? status, int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Clamp(page, 1, 1_000_000);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = dbContext.SurveyAssignments.AsNoTracking();
        if (surveyFormId.HasValue) query = query.Where(x => x.SurveyFormId == surveyFormId.Value);
        if (employeeId.HasValue) query = query.Where(x => x.EmployeeId == employeeId.Value);
        if (status.HasValue) query = query.Where(x => x.Status == status.Value);
        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .Include(x => x.SurveyForm)
            .Include(x => x.Employee)
            .Include(x => x.Submissions)
            .OrderByDescending(x => x.UpdatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return new PagedResponse<SurveyAssignmentResponse>(items.Select(ToAssignmentResponse).ToList(), page, pageSize, totalCount);
    }

    public async Task<OperationResult<SurveyAssignmentResponse>> AssignAsync(CreateSurveyAssignmentRequest request, RequestActor actor, CancellationToken cancellationToken)
    {
        var form = await dbContext.SurveyForms.SingleOrDefaultAsync(x => x.Id == request.SurveyFormId, cancellationToken);
        if (form is null) return OperationResult<SurveyAssignmentResponse>.NotFound("survey_not_found", "Survey form was not found.");
        if (form.Status != SurveyFormStatus.Published)
            return OperationResult<SurveyAssignmentResponse>.Conflict("survey_not_published", "Assignments can only be created for published surveys.");
        var employee = await dbContext.Employees.SingleOrDefaultAsync(x => x.Id == request.EmployeeId, cancellationToken);
        if (employee is null) return OperationResult<SurveyAssignmentResponse>.NotFound("employee_not_found", "Employee was not found.");
        if (!employee.IsActive) return OperationResult<SurveyAssignmentResponse>.Conflict("employee_inactive", "Survey assignments require an active employee.");

        var existing = await dbContext.SurveyAssignments
            .Include(x => x.SurveyForm)
            .Include(x => x.Employee)
            .Include(x => x.Submissions)
            .SingleOrDefaultAsync(x => x.SurveyFormId == request.SurveyFormId && x.EmployeeId == request.EmployeeId, cancellationToken);
        var now = UtcNow();
        if (existing is not null)
        {
            if (existing.Status != SurveyAssignmentStatus.Cancelled)
                return OperationResult<SurveyAssignmentResponse>.Conflict("survey_assignment_exists", "This employee already has an active or historical non-cancelled assignment for the survey.");
            existing.Status = SurveyAssignmentStatus.Assigned;
            existing.DueDate = request.DueDate;
            existing.AssignedByUserId = actor.UserId;
            existing.UpdatedAtUtc = now;
            AddAudit(actor, "survey.assignment.reactivated", "SurveyAssignment", existing.Id, new { existing.SurveyFormId, existing.EmployeeId });
            await dbContext.SaveChangesAsync(cancellationToken);
            return OperationResult<SurveyAssignmentResponse>.Success(ToAssignmentResponse(existing));
        }

        var assignment = new SurveyAssignment
        {
            SurveyFormId = form.Id,
            SurveyForm = form,
            EmployeeId = employee.Id,
            Employee = employee,
            AssignedByUserId = actor.UserId,
            Status = SurveyAssignmentStatus.Assigned,
            DueDate = request.DueDate,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        dbContext.SurveyAssignments.Add(assignment);
        AddAudit(actor, "survey.assignment.created", "SurveyAssignment", assignment.Id, new { assignment.SurveyFormId, assignment.EmployeeId, assignment.DueDate });
        await dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult<SurveyAssignmentResponse>.Success(ToAssignmentResponse(assignment));
    }

    public async Task<OperationResult<SurveyAssignmentResponse>> CancelAssignmentAsync(Guid id, RequestActor actor, CancellationToken cancellationToken)
    {
        var assignment = await dbContext.SurveyAssignments
            .Include(x => x.SurveyForm).Include(x => x.Employee).Include(x => x.Submissions)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (assignment is null) return OperationResult<SurveyAssignmentResponse>.NotFound("survey_assignment_not_found", "Survey assignment was not found.");
        if (assignment.Status is SurveyAssignmentStatus.Submitted or SurveyAssignmentStatus.Approved)
            return OperationResult<SurveyAssignmentResponse>.Conflict("survey_assignment_locked", "Submitted or approved assignments cannot be cancelled.");
        assignment.Status = SurveyAssignmentStatus.Cancelled;
        assignment.UpdatedAtUtc = UtcNow();
        AddAudit(actor, "survey.assignment.cancelled", "SurveyAssignment", assignment.Id, new { assignment.SurveyFormId, assignment.EmployeeId });
        await dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult<SurveyAssignmentResponse>.Success(ToAssignmentResponse(assignment));
    }

    public async Task<OperationResult<IReadOnlyCollection<SurveyAssignmentResponse>>> GetMyAssignmentsAsync(RequestActor actor, CancellationToken cancellationToken)
    {
        var employee = await ResolveActorEmployeeAsync(actor, cancellationToken);
        if (employee is null) return OperationResult<IReadOnlyCollection<SurveyAssignmentResponse>>.Invalid("employee_profile_required", "An active employee profile is required.");
        var assignments = await dbContext.SurveyAssignments.AsNoTracking()
            .Include(x => x.SurveyForm).Include(x => x.Employee).Include(x => x.Submissions)
            .Where(x => x.EmployeeId == employee.Id && x.Status != SurveyAssignmentStatus.Cancelled)
            .OrderBy(x => x.DueDate).ThenByDescending(x => x.UpdatedAtUtc)
            .ToListAsync(cancellationToken);
        return OperationResult<IReadOnlyCollection<SurveyAssignmentResponse>>.Success(assignments.Select(ToAssignmentResponse).ToList());
    }

    public Task<OperationResult<SurveySubmissionResponse>> SaveDraftAsync(Guid assignmentId, SaveSurveySubmissionRequest request, RequestActor actor, CancellationToken cancellationToken)
        => SaveOrSubmitAsync(assignmentId, request, actor, submit: false, cancellationToken);

    public Task<OperationResult<SurveySubmissionResponse>> SubmitAsync(Guid assignmentId, SaveSurveySubmissionRequest request, RequestActor actor, CancellationToken cancellationToken)
        => SaveOrSubmitAsync(assignmentId, request, actor, submit: true, cancellationToken);

    public async Task<PagedResponse<SurveySubmissionResponse>> GetPendingSubmissionsAsync(Guid? surveyFormId, int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Clamp(page, 1, 1_000_000);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = SubmissionQuery().Where(x => x.Status == SurveySubmissionStatus.Submitted);
        if (surveyFormId.HasValue) query = query.Where(x => x.SurveyAssignment.SurveyFormId == surveyFormId.Value);
        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query.OrderBy(x => x.SubmittedAtUtc).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new PagedResponse<SurveySubmissionResponse>(items.Select(ToSubmissionResponse).ToList(), page, pageSize, totalCount);
    }

    public async Task<OperationResult<SurveySubmissionResponse>> ReviewAsync(Guid submissionId, ReviewSurveySubmissionRequest request, RequestActor actor, CancellationToken cancellationToken)
    {
        if (actor.UserId is null) return OperationResult<SurveySubmissionResponse>.Invalid("actor_required", "An authenticated reviewer is required.");
        if (!Enum.IsDefined(request.Decision)) return OperationResult<SurveySubmissionResponse>.Invalid("survey_review_decision_invalid", "Review decision is invalid.");
        var comment = CleanOptional(request.Comment);
        if (request.Decision == SurveyReviewDecision.Reject && string.IsNullOrWhiteSpace(comment))
            return OperationResult<SurveySubmissionResponse>.Invalid("survey_rejection_comment_required", "A rejection comment is required.");

        var submission = await SubmissionQuery(tracking: true).SingleOrDefaultAsync(x => x.Id == submissionId, cancellationToken);
        if (submission is null) return OperationResult<SurveySubmissionResponse>.NotFound("survey_submission_not_found", "Survey submission was not found.");
        if (submission.Status != SurveySubmissionStatus.Submitted)
            return OperationResult<SurveySubmissionResponse>.Conflict("survey_submission_not_reviewable", "Only submitted responses can be reviewed.");

        var now = UtcNow();
        submission.Status = request.Decision == SurveyReviewDecision.Approve ? SurveySubmissionStatus.Approved : SurveySubmissionStatus.Rejected;
        submission.ReviewedAtUtc = now;
        submission.ReviewedByUserId = actor.UserId;
        submission.ReviewComment = comment;
        submission.UpdatedAtUtc = now;
        submission.SurveyAssignment.Status = request.Decision == SurveyReviewDecision.Approve ? SurveyAssignmentStatus.Approved : SurveyAssignmentStatus.Rejected;
        submission.SurveyAssignment.UpdatedAtUtc = now;
        AddAudit(actor, request.Decision == SurveyReviewDecision.Approve ? "survey.submission.approved" : "survey.submission.rejected", "SurveySubmission", submission.Id,
            new { submission.SurveyAssignmentId, submission.RevisionNumber, Comment = comment });
        await dbContext.SaveChangesAsync(cancellationToken);
        return OperationResult<SurveySubmissionResponse>.Success(ToSubmissionResponse(submission));
    }

    private async Task<OperationResult<SurveySubmissionResponse>> SaveOrSubmitAsync(Guid assignmentId, SaveSurveySubmissionRequest request, RequestActor actor, bool submit, CancellationToken cancellationToken)
    {
        var employee = await ResolveActorEmployeeAsync(actor, cancellationToken);
        if (employee is null) return OperationResult<SurveySubmissionResponse>.Invalid("employee_profile_required", "An active employee profile is required.");

        var assignment = await dbContext.SurveyAssignments
            .Include(x => x.SurveyForm).ThenInclude(x => x.Questions)
            .Include(x => x.Employee)
            .Include(x => x.Submissions).ThenInclude(x => x.Answers)
            .SingleOrDefaultAsync(x => x.Id == assignmentId, cancellationToken);
        if (assignment is null) return OperationResult<SurveySubmissionResponse>.NotFound("survey_assignment_not_found", "Survey assignment was not found.");
        if (assignment.EmployeeId != employee.Id) return OperationResult<SurveySubmissionResponse>.Invalid("survey_assignment_not_owned", "This survey assignment belongs to another employee.");
        if (assignment.SurveyForm.Status != SurveyFormStatus.Published)
            return OperationResult<SurveySubmissionResponse>.Conflict("survey_not_accepting_responses", "This survey is not accepting responses.");
        if (assignment.Status is SurveyAssignmentStatus.Cancelled or SurveyAssignmentStatus.Approved or SurveyAssignmentStatus.Submitted)
            return OperationResult<SurveySubmissionResponse>.Conflict("survey_assignment_locked", "This assignment is not currently editable.");

        var answerValidation = ValidateAnswers(assignment.SurveyForm.Questions, request.Answers, requireRequired: submit);
        if (answerValidation.ErrorCode is not null)
            return OperationResult<SurveySubmissionResponse>.Invalid(answerValidation.ErrorCode, answerValidation.Message!);

        var latest = assignment.Submissions.OrderByDescending(x => x.RevisionNumber).FirstOrDefault();
        SurveySubmission submission;
        var now = UtcNow();
        if (latest is null)
        {
            submission = NewSubmission(assignment, 1, now);
            assignment.Submissions.Add(submission);
            dbContext.SurveySubmissions.Add(submission);
        }
        else if (latest.Status == SurveySubmissionStatus.Draft)
        {
            submission = latest;
        }
        else if (latest.Status == SurveySubmissionStatus.Rejected)
        {
            submission = NewSubmission(assignment, latest.RevisionNumber + 1, now);
            assignment.Submissions.Add(submission);
            dbContext.SurveySubmissions.Add(submission);
        }
        else
        {
            return OperationResult<SurveySubmissionResponse>.Conflict("survey_submission_locked", "The latest survey submission cannot be edited.");
        }

        dbContext.SurveyAnswers.RemoveRange(submission.Answers);
        submission.Answers.Clear();
        foreach (var answer in request.Answers)
        {
            var question = assignment.SurveyForm.Questions.Single(x => x.Id == answer.QuestionId);
            submission.Answers.Add(new SurveyAnswer
            {
                SurveySubmissionId = submission.Id,
                SurveySubmission = submission,
                SurveyQuestionId = question.Id,
                SurveyQuestion = question,
                ValueJson = answer.Value.GetRawText()
            });
        }

        submission.Status = submit ? SurveySubmissionStatus.Submitted : SurveySubmissionStatus.Draft;
        submission.SubmittedAtUtc = submit ? now : null;
        submission.ReviewedAtUtc = null;
        submission.ReviewedByUserId = null;
        submission.ReviewComment = null;
        submission.UpdatedAtUtc = now;
        assignment.Status = submit ? SurveyAssignmentStatus.Submitted : SurveyAssignmentStatus.InProgress;
        assignment.UpdatedAtUtc = now;
        AddAudit(actor, submit ? "survey.submission.submitted" : "survey.submission.draft.saved", "SurveySubmission", submission.Id,
            new { assignment.SurveyFormId, assignment.EmployeeId, submission.RevisionNumber, AnswerCount = request.Answers.Count });
        await dbContext.SaveChangesAsync(cancellationToken);

        var loaded = await SubmissionQuery().SingleAsync(x => x.Id == submission.Id, cancellationToken);
        return OperationResult<SurveySubmissionResponse>.Success(ToSubmissionResponse(loaded));
    }

    private async Task<Employee?> ResolveActorEmployeeAsync(RequestActor actor, CancellationToken cancellationToken)
    {
        if (actor.UserId is null) return null;
        return await dbContext.Employees.SingleOrDefaultAsync(x => x.UserId == actor.UserId && x.IsActive && x.User.IsActive, cancellationToken);
    }

    private IQueryable<SurveySubmission> SubmissionQuery(bool tracking = false)
    {
        var query = tracking ? dbContext.SurveySubmissions.AsQueryable() : dbContext.SurveySubmissions.AsNoTracking();
        return query
            .Include(x => x.SurveyAssignment).ThenInclude(x => x.SurveyForm)
            .Include(x => x.SurveyAssignment).ThenInclude(x => x.Employee)
            .Include(x => x.ReviewedByUser)
            .Include(x => x.Answers).ThenInclude(x => x.SurveyQuestion);
    }

    private async Task<SurveyForm?> LoadFormAsync(Guid id, bool tracking, CancellationToken cancellationToken)
    {
        var query = tracking ? dbContext.SurveyForms.AsQueryable() : dbContext.SurveyForms.AsNoTracking();
        return await query.Include(x => x.Project).Include(x => x.Questions).Include(x => x.Assignments).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    private static SurveySubmission NewSubmission(SurveyAssignment assignment, int revision, DateTime now) => new()
    {
        SurveyAssignmentId = assignment.Id,
        SurveyAssignment = assignment,
        RevisionNumber = revision,
        Status = SurveySubmissionStatus.Draft,
        CreatedAtUtc = now,
        UpdatedAtUtc = now
    };

    private static (string? ErrorCode, string? Message, string? Key, string? NormalizedKey, string? Prompt, IReadOnlyCollection<string> Options) ValidateQuestion(SurveyQuestionInput input)
    {
        if (!Enum.IsDefined(input.Type)) return ("survey_question_type_invalid", "Question type is invalid.", null, null, null, Array.Empty<string>());
        var key = input.Key.Trim();
        var prompt = input.Prompt.Trim();
        if (key.Length is < 1 or > 100 || prompt.Length is < 2 or > 500)
            return ("survey_question_invalid", "Question key and prompt are required and must be within allowed lengths.", null, null, null, Array.Empty<string>());
        var options = input.Options.Select(x => x?.Trim() ?? string.Empty).Where(x => x.Length > 0).ToList();
        var isChoice = input.Type is SurveyQuestionType.SingleChoice or SurveyQuestionType.MultipleChoice;
        if (isChoice)
        {
            if (options.Count is < 2 or > 50) return ("survey_question_options_invalid", "Choice questions require between 2 and 50 options.", null, null, null, Array.Empty<string>());
            if (options.Any(x => x.Length > 200)) return ("survey_question_option_too_long", "Choice options cannot exceed 200 characters.", null, null, null, Array.Empty<string>());
            if (options.Select(Normalize).Distinct(StringComparer.Ordinal).Count() != options.Count)
                return ("survey_question_options_duplicate", "Choice options must be unique.", null, null, null, Array.Empty<string>());
        }
        else if (options.Count > 0)
        {
            return ("survey_question_options_not_allowed", "Only choice questions can define options.", null, null, null, Array.Empty<string>());
        }
        return (null, null, key, Normalize(key), prompt, options);
    }

    private static (string? ErrorCode, string? Message) ValidateAnswers(ICollection<SurveyQuestion> questions, IReadOnlyCollection<SurveyAnswerInput> answers, bool requireRequired)
    {
        var answerMap = new Dictionary<Guid, SurveyAnswerInput>();
        foreach (var answer in answers)
        {
            if (!answerMap.TryAdd(answer.QuestionId, answer)) return ("survey_answer_duplicate", "Each question can only be answered once.");
        }
        var questionMap = questions.ToDictionary(x => x.Id);
        if (answerMap.Keys.Any(id => !questionMap.ContainsKey(id))) return ("survey_answer_question_invalid", "One or more answers reference a question outside this survey.");

        foreach (var answer in answers)
        {
            var question = questionMap[answer.QuestionId];
            if (!IsValidAnswer(question, answer.Value)) return ("survey_answer_value_invalid", $"Answer for question '{question.Key}' does not match its question type or choices.");
        }

        if (requireRequired)
        {
            foreach (var question in questions.Where(x => x.IsRequired))
            {
                if (!answerMap.TryGetValue(question.Id, out var answer) || IsEmptyRequiredValue(answer.Value))
                    return ("survey_required_answer_missing", $"Question '{question.Key}' requires an answer.");
            }
        }
        return (null, null);
    }

    private static bool IsValidAnswer(SurveyQuestion question, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return !question.IsRequired;
        var options = JsonSerializer.Deserialize<List<string>>(question.OptionsJson) ?? [];
        return question.Type switch
        {
            SurveyQuestionType.Text or SurveyQuestionType.LongText => value.ValueKind == JsonValueKind.String,
            SurveyQuestionType.Number => value.ValueKind == JsonValueKind.Number,
            SurveyQuestionType.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            SurveyQuestionType.Date => value.ValueKind == JsonValueKind.String && DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            SurveyQuestionType.SingleChoice => value.ValueKind == JsonValueKind.String && options.Contains(value.GetString() ?? string.Empty, StringComparer.Ordinal),
            SurveyQuestionType.MultipleChoice => ValidateMultiChoice(value, options),
            _ => false
        };
    }

    private static bool ValidateMultiChoice(JsonElement value, IReadOnlyCollection<string> options)
    {
        if (value.ValueKind != JsonValueKind.Array) return false;
        var values = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) return false;
            var text = item.GetString() ?? string.Empty;
            if (!options.Contains(text, StringComparer.Ordinal)) return false;
            values.Add(text);
        }
        return values.Count == values.Distinct(StringComparer.Ordinal).Count();
    }

    private static bool IsEmptyRequiredValue(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null ||
        (value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString())) ||
        (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0);

    private static SurveyFormDetailResponse ToFormDetail(SurveyForm form) => new(
        new SurveyFormResponse(form.Id, form.ProjectId, form.Project.Code, form.Project.Name, form.Code, form.Name, form.Description, form.Status,
            form.Questions.Count, form.Assignments.Count(x => x.Status != SurveyAssignmentStatus.Cancelled), form.CreatedAtUtc, form.UpdatedAtUtc),
        form.Questions.OrderBy(x => x.SortOrder).Select(x => new SurveyQuestionResponse(x.Id, x.Key, x.Prompt, x.Type, x.IsRequired,
            JsonSerializer.Deserialize<List<string>>(x.OptionsJson) ?? [], x.SortOrder)).ToList());

    private static SurveyAssignmentResponse ToAssignmentResponse(SurveyAssignment assignment)
    {
        var latest = assignment.Submissions.OrderByDescending(x => x.RevisionNumber).FirstOrDefault();
        return new SurveyAssignmentResponse(assignment.Id, assignment.SurveyFormId, assignment.SurveyForm.Code, assignment.SurveyForm.Name,
            assignment.EmployeeId, assignment.Employee.EmployeeCode, assignment.Employee.FullName, assignment.Status, assignment.DueDate,
            latest?.RevisionNumber ?? 0, latest?.Status, assignment.CreatedAtUtc, assignment.UpdatedAtUtc);
    }

    private static SurveySubmissionResponse ToSubmissionResponse(SurveySubmission submission) => new(
        submission.Id,
        submission.SurveyAssignmentId,
        submission.SurveyAssignment.SurveyFormId,
        submission.SurveyAssignment.SurveyForm.Code,
        submission.SurveyAssignment.SurveyForm.Name,
        submission.SurveyAssignment.EmployeeId,
        submission.SurveyAssignment.Employee.FullName,
        submission.RevisionNumber,
        submission.Status,
        submission.SubmittedAtUtc,
        submission.ReviewedAtUtc,
        submission.ReviewedByUserId,
        submission.ReviewedByUser?.Email,
        submission.ReviewComment,
        submission.Answers.OrderBy(x => x.SurveyQuestion.SortOrder).Select(x => new SurveyAnswerResponse(
            x.SurveyQuestionId, x.SurveyQuestion.Key, x.SurveyQuestion.Prompt, x.SurveyQuestion.Type, x.ValueJson)).ToList(),
        submission.CreatedAtUtc,
        submission.UpdatedAtUtc);

    private void AddAudit(RequestActor actor, string action, string targetType, Guid targetId, object details)
    {
        dbContext.AuditLogs.Add(new AuditLog
        {
            ActorUserId = actor.UserId,
            Action = action,
            TargetType = targetType,
            TargetId = targetId.ToString(),
            MetadataJson = JsonSerializer.Serialize(details),
            IpAddress = actor.IpAddress,
            UserAgent = actor.UserAgent,
            CreatedAtUtc = UtcNow()
        });
    }

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;
    private static string Normalize(string value) => value.Trim().ToUpperInvariant();
    private static string? CleanOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
