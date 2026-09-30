import { useEffect, useMemo, useState, type FormEvent } from 'react';
import { apiFetch } from './api';
import { useAuth } from './auth';
import type { PagedResponse } from './types';
import './management.css';

interface ProjectOption { id: string; code: string; name: string; status: string; }
interface EmployeeOption { id: string; employeeCode: string; fullName: string; }

type SurveyFormStatus = 'Draft' | 'Published' | 'Closed' | 'Archived';
type SurveyQuestionType = 'Text' | 'LongText' | 'Number' | 'Boolean' | 'Date' | 'SingleChoice' | 'MultipleChoice';
type SurveyAssignmentStatus = 'Assigned' | 'InProgress' | 'Submitted' | 'Approved' | 'Rejected' | 'Cancelled';

interface SurveyFormRecord {
  id: string;
  projectId: string;
  projectCode: string;
  projectName: string;
  code: string;
  name: string;
  description: string | null;
  status: SurveyFormStatus;
  questionCount: number;
  assignmentCount: number;
  createdAtUtc: string;
  updatedAtUtc: string;
}

interface SurveyQuestion {
  id: string;
  key: string;
  prompt: string;
  type: SurveyQuestionType;
  isRequired: boolean;
  options: string[];
  sortOrder: number;
}

interface SurveyFormDetail {
  form: SurveyFormRecord;
  questions: SurveyQuestion[];
}

interface SurveyAssignment {
  id: string;
  surveyFormId: string;
  surveyCode: string;
  surveyName: string;
  employeeId: string;
  employeeCode: string;
  employeeName: string;
  status: SurveyAssignmentStatus;
  dueDate: string | null;
  latestRevisionNumber: number;
  latestSubmissionStatus: string | null;
  createdAtUtc: string;
  updatedAtUtc: string;
}

interface SurveyAnswer {
  questionId: string;
  questionKey: string;
  prompt: string;
  type: SurveyQuestionType;
  valueJson: string;
}

interface SurveySubmission {
  id: string;
  assignmentId: string;
  surveyFormId: string;
  surveyCode: string;
  surveyName: string;
  employeeId: string;
  employeeName: string;
  revisionNumber: number;
  status: string;
  submittedAtUtc: string | null;
  reviewedAtUtc: string | null;
  reviewerEmail: string | null;
  reviewComment: string | null;
  answers: SurveyAnswer[];
}

interface SurveyDraft { projectId: string; code: string; name: string; description: string; }
interface QuestionDraft { key: string; prompt: string; type: SurveyQuestionType; isRequired: boolean; optionsText: string; }

const emptySurvey: SurveyDraft = { projectId: '', code: '', name: '', description: '' };
const emptyQuestion = (): QuestionDraft => ({ key: '', prompt: '', type: 'Text', isRequired: false, optionsText: '' });
const questionTypes: SurveyQuestionType[] = ['Text', 'LongText', 'Number', 'Boolean', 'Date', 'SingleChoice', 'MultipleChoice'];

function statusBadge(value: string) {
  return <span className={`status-badge status-${value.toLowerCase()}`}>{value}</span>;
}

function formatDate(value: string | null | undefined): string {
  if (!value) return '—';
  const parsed = new Date(value.length === 10 ? `${value}T00:00:00` : value);
  return Number.isNaN(parsed.getTime()) ? value : new Intl.DateTimeFormat(undefined, { dateStyle: 'medium' }).format(parsed);
}

function formatDateTime(value: string | null | undefined): string {
  if (!value) return '—';
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? value : new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(parsed);
}

function answerValue(valueJson: string): string {
  try {
    const value = JSON.parse(valueJson) as unknown;
    if (value === null || value === undefined) return '—';
    if (Array.isArray(value)) return value.map(item => String(item)).join(', ');
    if (typeof value === 'object') return JSON.stringify(value);
    return String(value);
  } catch {
    return valueJson;
  }
}

export default function SurveyManagementPage() {
  const { can } = useAuth();
  const mayManageForms = can('surveys.manage');
  const mayReadAssignments = can('survey.assignments.read');
  const mayManageAssignments = can('survey.assignments.manage');
  const mayReview = can('survey.review');

  const [forms, setForms] = useState<SurveyFormRecord[]>([]);
  const [projects, setProjects] = useState<ProjectOption[]>([]);
  const [employees, setEmployees] = useState<EmployeeOption[]>([]);
  const [assignments, setAssignments] = useState<SurveyAssignment[]>([]);
  const [pending, setPending] = useState<SurveySubmission[]>([]);
  const [selectedFormId, setSelectedFormId] = useState<string | null>(null);
  const [detail, setDetail] = useState<SurveyFormDetail | null>(null);
  const [draft, setDraft] = useState<SurveyDraft>(emptySurvey);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [questionDrafts, setQuestionDrafts] = useState<QuestionDraft[]>([]);
  const [showForm, setShowForm] = useState(false);
  const [showQuestions, setShowQuestions] = useState(false);
  const [showAssignmentForm, setShowAssignmentForm] = useState(false);
  const [assignmentEmployeeId, setAssignmentEmployeeId] = useState('');
  const [assignmentDueDate, setAssignmentDueDate] = useState('');
  const [reviewingSubmissionId, setReviewingSubmissionId] = useState<string | null>(null);
  const [reviewComment, setReviewComment] = useState('');
  const [searchDraft, setSearchDraft] = useState('');
  const [search, setSearch] = useState('');
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [version, setVersion] = useState(0);

  const selectedForm = useMemo(() => forms.find(item => item.id === selectedFormId) ?? null, [forms, selectedFormId]);
  const reviewingSubmission = useMemo(() => pending.find(item => item.id === reviewingSubmissionId) ?? null, [pending, reviewingSubmissionId]);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError('');
    const suffix = search ? `&search=${encodeURIComponent(search)}` : '';
    const requests: Promise<unknown>[] = [
      apiFetch<PagedResponse<SurveyFormRecord>>(`/api/surveys?page=1&pageSize=100${suffix}`),
      apiFetch<ProjectOption[]>('/api/admin-lookups/surveys/projects')
    ];
    if (mayReadAssignments) {
      requests.push(apiFetch<PagedResponse<SurveyAssignment>>('/api/survey-assignments?page=1&pageSize=100'));
      requests.push(apiFetch<EmployeeOption[]>('/api/admin-lookups/surveys/employees'));
    }
    if (mayReview) requests.push(apiFetch<PagedResponse<SurveySubmission>>('/api/survey-submissions/pending?page=1&pageSize=100'));

    Promise.all(requests)
      .then(results => {
        if (cancelled) return;
        let index = 0;
        const formPage = results[index++] as PagedResponse<SurveyFormRecord>;
        const projectOptions = results[index++] as ProjectOption[];
        setForms(formPage.items);
        setProjects(projectOptions);
        if (selectedFormId && !formPage.items.some(item => item.id === selectedFormId)) {
          setSelectedFormId(null);
          setDetail(null);
        }
        if (mayReadAssignments) {
          const assignmentPage = results[index++] as PagedResponse<SurveyAssignment>;
          setAssignments(assignmentPage.items);
          setEmployees(results[index++] as EmployeeOption[]);
        } else {
          setAssignments([]);
          setEmployees([]);
        }
        if (mayReview) {
          const pendingPage = results[index++] as PagedResponse<SurveySubmission>;
          setPending(pendingPage.items);
        } else {
          setPending([]);
        }
      })
      .catch(caught => { if (!cancelled) setError(caught instanceof Error ? caught.message : 'Unable to load survey administration.'); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [search, version, mayReadAssignments, mayReview, selectedFormId]);

  useEffect(() => {
    if (!selectedFormId) { setDetail(null); return; }
    let cancelled = false;
    apiFetch<SurveyFormDetail>(`/api/surveys/${selectedFormId}`)
      .then(value => {
        if (cancelled) return;
        setDetail(value);
        setQuestionDrafts(value.questions.map(question => ({
          key: question.key,
          prompt: question.prompt,
          type: question.type,
          isRequired: question.isRequired,
          optionsText: question.options.join('\n')
        })));
      })
      .catch(caught => { if (!cancelled) setError(caught instanceof Error ? caught.message : 'Unable to load survey details.'); });
    return () => { cancelled = true; };
  }, [selectedFormId, version]);

  function refresh(message?: string) {
    if (message) setNotice(message);
    setVersion(value => value + 1);
  }

  function newSurvey() {
    setEditingId(null);
    setDraft(emptySurvey);
    setShowForm(true);
    setError('');
    setNotice('');
  }

  function editSurvey(item: SurveyFormRecord) {
    setEditingId(item.id);
    setSelectedFormId(item.id);
    setDraft({ projectId: item.projectId, code: item.code, name: item.name, description: item.description || '' });
    setShowForm(true);
    setError('');
    setNotice('');
  }

  async function saveSurvey(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!mayManageForms) return;
    setBusy(true); setError(''); setNotice('');
    try {
      const payload = editingId
        ? { code: draft.code.trim(), name: draft.name.trim(), description: draft.description.trim() || null }
        : { projectId: draft.projectId, code: draft.code.trim(), name: draft.name.trim(), description: draft.description.trim() || null };
      const saved = await apiFetch<SurveyFormDetail>(`/api/surveys${editingId ? `/${editingId}` : ''}`, {
        method: editingId ? 'PUT' : 'POST',
        body: JSON.stringify(payload)
      });
      setSelectedFormId(saved.form.id);
      setShowForm(false);
      setEditingId(null);
      refresh(`Survey ${editingId ? 'updated' : 'created'}.`);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : 'Unable to save survey.');
    } finally { setBusy(false); }
  }

  function openQuestions(item: SurveyFormRecord) {
    setSelectedFormId(item.id);
    setShowQuestions(true);
    setError('');
    setNotice('');
  }

  function updateQuestion(index: number, patch: Partial<QuestionDraft>) {
    setQuestionDrafts(current => current.map((item, itemIndex) => itemIndex === index ? { ...item, ...patch } : item));
  }

  async function saveQuestions() {
    if (!mayManageForms || !selectedFormId) return;
    setBusy(true); setError(''); setNotice('');
    try {
      await apiFetch(`/api/surveys/${selectedFormId}/questions`, {
        method: 'PUT',
        body: JSON.stringify({
          questions: questionDrafts.map(question => ({
            key: question.key.trim(),
            prompt: question.prompt.trim(),
            type: question.type,
            isRequired: question.isRequired,
            options: question.type === 'SingleChoice' || question.type === 'MultipleChoice'
              ? question.optionsText.split(/\r?\n|,/).map(value => value.trim()).filter(Boolean)
              : []
          }))
        })
      });
      setShowQuestions(false);
      refresh('Survey questions saved.');
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : 'Unable to save survey questions.');
    } finally { setBusy(false); }
  }

  async function changeStatus(item: SurveyFormRecord, status: SurveyFormStatus) {
    if (!mayManageForms) return;
    setBusy(true); setError(''); setNotice('');
    try {
      await apiFetch(`/api/surveys/${item.id}/status`, { method: 'PUT', body: JSON.stringify({ status }) });
      setSelectedFormId(item.id);
      refresh(`Survey status changed to ${status}.`);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : 'Unable to change survey status.');
    } finally { setBusy(false); }
  }

  function openAssignment(item: SurveyFormRecord) {
    setSelectedFormId(item.id);
    setAssignmentEmployeeId('');
    setAssignmentDueDate('');
    setShowAssignmentForm(true);
    setError('');
    setNotice('');
  }

  async function assignSurvey(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!mayManageAssignments || !selectedFormId) return;
    setBusy(true); setError(''); setNotice('');
    try {
      await apiFetch('/api/survey-assignments', {
        method: 'POST',
        body: JSON.stringify({ surveyFormId: selectedFormId, employeeId: assignmentEmployeeId, dueDate: assignmentDueDate || null })
      });
      setShowAssignmentForm(false);
      setAssignmentEmployeeId('');
      setAssignmentDueDate('');
      refresh('Survey assigned.');
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : 'Unable to assign survey.');
    } finally { setBusy(false); }
  }

  async function cancelAssignment(item: SurveyAssignment) {
    if (!mayManageAssignments) return;
    if (!window.confirm(`Cancel ${item.surveyName} for ${item.employeeName}?`)) return;
    setBusy(true); setError(''); setNotice('');
    try {
      await apiFetch(`/api/survey-assignments/${item.id}`, { method: 'DELETE' });
      refresh('Survey assignment cancelled.');
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : 'Unable to cancel survey assignment.');
    } finally { setBusy(false); }
  }

  async function reviewSubmission(decision: 'Approve' | 'Reject') {
    if (!mayReview || !reviewingSubmissionId) return;
    if (decision === 'Reject' && !reviewComment.trim()) {
      setError('A rejection comment is required.');
      return;
    }
    setBusy(true); setError(''); setNotice('');
    try {
      await apiFetch(`/api/survey-submissions/${reviewingSubmissionId}/review`, {
        method: 'POST',
        body: JSON.stringify({ decision, comment: reviewComment.trim() || null })
      });
      setReviewingSubmissionId(null);
      setReviewComment('');
      refresh(`Submission ${decision === 'Approve' ? 'approved' : 'rejected'}.`);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : 'Unable to review submission.');
    } finally { setBusy(false); }
  }

  const assignmentsForSelected = selectedFormId ? assignments.filter(item => item.surveyFormId === selectedFormId) : assignments;
  const pendingForSelected = selectedFormId ? pending.filter(item => item.surveyFormId === selectedFormId) : pending;

  return <>
    <div className="page-header">
      <div><p className="eyebrow">Field operations</p><h1>Surveys</h1><p className="muted">Build project questionnaires, publish field work, assign employees and review submitted revisions.</p></div>
      <div className="header-actions">
        <form className="inline-search" onSubmit={event => { event.preventDefault(); setSearch(searchDraft.trim()); }}>
          <input value={searchDraft} onChange={event => setSearchDraft(event.target.value)} placeholder="Search surveys" />
          <button className="ghost-button">Search</button>
        </form>
        {mayManageForms && <button className="primary-button" onClick={newSurvey}>New survey</button>}
      </div>
    </div>

    {notice && <div className="success-banner">{notice}</div>}
    {error && <div className="error-banner">{error}</div>}

    {showForm && mayManageForms && <article className="panel management-form-panel">
      <div className="panel-heading"><div><h2>{editingId ? 'Edit draft survey' : 'Create survey'}</h2><p>Forms remain editable only while in Draft.</p></div><button className="ghost-button" onClick={() => setShowForm(false)}>Close</button></div>
      <form className="form-grid" onSubmit={saveSurvey}>
        <label><span>Project</span><select required disabled={Boolean(editingId)} value={draft.projectId} onChange={event => setDraft(value => ({ ...value, projectId: event.target.value }))}><option value="">Select project…</option>{projects.map(item => <option key={item.id} value={item.id}>{item.name} ({item.code})</option>)}</select></label>
        <label><span>Code</span><input required maxLength={50} value={draft.code} onChange={event => setDraft(value => ({ ...value, code: event.target.value }))} /></label>
        <label className="wide-field"><span>Name</span><input required minLength={2} maxLength={200} value={draft.name} onChange={event => setDraft(value => ({ ...value, name: event.target.value }))} /></label>
        <label className="wide-field"><span>Description</span><textarea maxLength={4000} value={draft.description} onChange={event => setDraft(value => ({ ...value, description: event.target.value }))} /></label>
        <div className="wide-field form-actions"><button className="primary-button" disabled={busy}>{busy ? 'Saving…' : 'Save survey'}</button><button type="button" className="ghost-button" onClick={() => setShowForm(false)}>Cancel</button></div>
      </form>
    </article>}

    {showQuestions && mayManageForms && detail?.form.id === selectedFormId && <article className="panel management-form-panel">
      <div className="panel-heading"><div><h2>Questions · {detail.form.name}</h2><p>Question keys are stable field identifiers. Choice options can be entered one per line or comma-separated.</p></div><button className="ghost-button" onClick={() => setShowQuestions(false)}>Close</button></div>
      <div className="stack-lg">
        {questionDrafts.map((question, index) => <div className="role-fieldset" key={`${index}-${question.key}`}>
          <div className="form-grid">
            <label><span>Key</span><input required maxLength={100} value={question.key} onChange={event => updateQuestion(index, { key: event.target.value })} /></label>
            <label><span>Type</span><select value={question.type} onChange={event => updateQuestion(index, { type: event.target.value as SurveyQuestionType })}>{questionTypes.map(type => <option key={type}>{type}</option>)}</select></label>
            <label className="wide-field"><span>Prompt</span><textarea required maxLength={500} value={question.prompt} onChange={event => updateQuestion(index, { prompt: event.target.value })} /></label>
            {(question.type === 'SingleChoice' || question.type === 'MultipleChoice') && <label className="wide-field"><span>Options</span><textarea value={question.optionsText} onChange={event => updateQuestion(index, { optionsText: event.target.value })} placeholder={'Option A\nOption B'} /></label>}
            <label className="check-field"><input type="checkbox" checked={question.isRequired} onChange={event => updateQuestion(index, { isRequired: event.target.checked })} /><span>Required</span></label>
            <div className="form-actions"><button type="button" className="text-button danger" onClick={() => setQuestionDrafts(current => current.filter((_, itemIndex) => itemIndex !== index))}>Remove question</button></div>
          </div>
        </div>)}
        <div className="form-actions"><button type="button" className="ghost-button" onClick={() => setQuestionDrafts(current => [...current, emptyQuestion()])}>Add question</button><button type="button" className="primary-button" disabled={busy} onClick={() => void saveQuestions()}>{busy ? 'Saving…' : 'Save questions'}</button></div>
      </div>
    </article>}

    {showAssignmentForm && mayManageAssignments && selectedForm && <article className="panel management-form-panel">
      <div className="panel-heading"><div><h2>Assign · {selectedForm.name}</h2><p>Only published surveys can be assigned.</p></div><button className="ghost-button" onClick={() => setShowAssignmentForm(false)}>Close</button></div>
      <form className="form-grid" onSubmit={assignSurvey}>
        <label><span>Employee</span><select required value={assignmentEmployeeId} onChange={event => setAssignmentEmployeeId(event.target.value)}><option value="">Select employee…</option>{employees.map(item => <option key={item.id} value={item.id}>{item.fullName} ({item.employeeCode})</option>)}</select></label>
        <label><span>Due date</span><input type="date" value={assignmentDueDate} onChange={event => setAssignmentDueDate(event.target.value)} /></label>
        <div className="wide-field form-actions"><button className="primary-button" disabled={busy}>{busy ? 'Assigning…' : 'Assign survey'}</button></div>
      </form>
    </article>}

    <article className="panel table-panel">
      <div className="panel-heading"><div><h2>Survey forms</h2><p>{forms.length} loaded</p></div><button className="ghost-button" onClick={() => refresh()}>Refresh</button></div>
      {loading ? <div className="loading-block">Loading survey administration…</div> : <div className="table-wrap"><table><thead><tr><th>Survey</th><th>Project</th><th>Status</th><th>Questions</th><th>Assignments</th><th>Actions</th></tr></thead><tbody>
        {forms.map(item => <tr key={item.id}>
          <td><button className="text-button" onClick={() => setSelectedFormId(item.id)}>{item.name}</button><small>{item.code}</small></td>
          <td>{item.projectName}<small>{item.projectCode}</small></td>
          <td>{statusBadge(item.status)}</td><td>{item.questionCount}</td><td>{item.assignmentCount}</td>
          <td className="action-cell">
            <button className="text-button" onClick={() => setSelectedFormId(item.id)}>View</button>
            {mayManageForms && item.status === 'Draft' && <><button className="text-button" onClick={() => editSurvey(item)}>Edit</button><button className="text-button" onClick={() => openQuestions(item)}>Questions</button><button className="text-button" disabled={busy || item.questionCount === 0} onClick={() => void changeStatus(item, 'Published')}>Publish</button><button className="text-button danger" disabled={busy} onClick={() => void changeStatus(item, 'Archived')}>Archive</button></>}
            {mayManageForms && item.status === 'Published' && <button className="text-button" disabled={busy} onClick={() => void changeStatus(item, 'Closed')}>Close</button>}
            {mayManageForms && item.status === 'Closed' && <button className="text-button danger" disabled={busy} onClick={() => void changeStatus(item, 'Archived')}>Archive</button>}
            {mayManageAssignments && item.status === 'Published' && <button className="text-button" onClick={() => openAssignment(item)}>Assign</button>}
          </td>
        </tr>)}
        {!forms.length && <tr><td colSpan={6} className="empty-cell">No surveys found.</td></tr>}
      </tbody></table></div>}
    </article>

    {detail && selectedFormId && <article className="panel table-panel">
      <div className="panel-heading"><div><h2>{detail.form.name}</h2><p>{detail.form.description || 'No description'} · Updated {formatDateTime(detail.form.updatedAtUtc)}</p></div>{statusBadge(detail.form.status)}</div>
      <div className="table-wrap"><table><thead><tr><th>#</th><th>Key</th><th>Prompt</th><th>Type</th><th>Required</th><th>Options</th></tr></thead><tbody>
        {detail.questions.map((question, index) => <tr key={question.id}><td>{index + 1}</td><td>{question.key}</td><td>{question.prompt}</td><td>{question.type}</td><td>{question.isRequired ? 'Yes' : 'No'}</td><td>{question.options.join(', ') || '—'}</td></tr>)}
        {!detail.questions.length && <tr><td colSpan={6} className="empty-cell">No questions yet.</td></tr>}
      </tbody></table></div>
    </article>}

    {mayReadAssignments && <article className="panel table-panel">
      <div className="panel-heading"><div><h2>{selectedForm ? `Assignments · ${selectedForm.name}` : 'Survey assignments'}</h2><p>{assignmentsForSelected.length} loaded</p></div>{selectedFormId && <button className="ghost-button" onClick={() => setSelectedFormId(null)}>Show all</button>}</div>
      <div className="table-wrap"><table><thead><tr><th>Survey</th><th>Employee</th><th>Status</th><th>Due</th><th>Revision</th>{mayManageAssignments && <th>Action</th>}</tr></thead><tbody>
        {assignmentsForSelected.map(item => <tr key={item.id}><td>{item.surveyName}<small>{item.surveyCode}</small></td><td>{item.employeeName}<small>{item.employeeCode}</small></td><td>{statusBadge(item.status)}</td><td>{formatDate(item.dueDate)}</td><td>{item.latestRevisionNumber || '—'}<small>{item.latestSubmissionStatus || ''}</small></td>{mayManageAssignments && <td>{!['Submitted', 'Approved', 'Cancelled'].includes(item.status) ? <button className="text-button danger" disabled={busy} onClick={() => void cancelAssignment(item)}>Cancel</button> : '—'}</td>}</tr>)}
        {!assignmentsForSelected.length && <tr><td colSpan={mayManageAssignments ? 6 : 5} className="empty-cell">No assignments found.</td></tr>}
      </tbody></table></div>
    </article>}

    {mayReview && <article className="panel table-panel">
      <div className="panel-heading"><div><h2>{selectedForm ? `Pending review · ${selectedForm.name}` : 'Pending survey review'}</h2><p>{pendingForSelected.length} submitted revisions</p></div></div>
      <div className="table-wrap"><table><thead><tr><th>Survey</th><th>Employee</th><th>Revision</th><th>Submitted</th><th>Action</th></tr></thead><tbody>
        {pendingForSelected.map(item => <tr key={item.id}><td>{item.surveyName}<small>{item.surveyCode}</small></td><td>{item.employeeName}</td><td>#{item.revisionNumber}</td><td>{formatDateTime(item.submittedAtUtc)}</td><td><button className="text-button" onClick={() => { setReviewingSubmissionId(item.id); setReviewComment(''); setError(''); }}>Review</button></td></tr>)}
        {!pendingForSelected.length && <tr><td colSpan={5} className="empty-cell">No submissions awaiting review.</td></tr>}
      </tbody></table></div>
    </article>}

    {mayReview && reviewingSubmission && <article className="panel management-form-panel">
      <div className="panel-heading"><div><h2>Review · {reviewingSubmission.surveyName}</h2><p>{reviewingSubmission.employeeName} · Revision #{reviewingSubmission.revisionNumber}</p></div><button className="ghost-button" onClick={() => setReviewingSubmissionId(null)}>Close</button></div>
      <div className="table-wrap"><table><thead><tr><th>Question</th><th>Answer</th></tr></thead><tbody>{reviewingSubmission.answers.map(answer => <tr key={answer.questionId}><td><strong>{answer.prompt}</strong><small>{answer.questionKey} · {answer.type}</small></td><td>{answerValue(answer.valueJson)}</td></tr>)}</tbody></table></div>
      <div className="compact-form"><label><span>Review comment</span><textarea value={reviewComment} onChange={event => setReviewComment(event.target.value)} maxLength={2000} placeholder="Required when rejecting; optional when approving." /></label><div className="form-actions"><button className="primary-button" disabled={busy} onClick={() => void reviewSubmission('Approve')}>Approve</button><button className="ghost-button" disabled={busy} onClick={() => void reviewSubmission('Reject')}>Reject</button></div></div>
    </article>}
  </>;
}
