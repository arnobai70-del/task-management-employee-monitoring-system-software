export interface PagedResponse<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
}

export interface DashboardOverview {
  generatedAtUtc: string;
  from: string;
  to: string;
  workforce: { activeEmployees: number; activeDepartments: number };
  attendance: {
    sessions: number;
    distinctEmployees: number;
    completedSessions: number;
    openSessions: number;
    lateSessions: number;
    earlyLeaveSessions: number;
    totalBreakMinutes: number;
  };
  projects: { planning: number; active: number; onHold: number; completed: number; archived: number; overdue: number };
  tasks: {
    open: number;
    toDo: number;
    inProgress: number;
    blocked: number;
    done: number;
    cancelled: number;
    overdue: number;
    unassignedOpen: number;
    completedInPeriod: number;
  };
  surveys: {
    publishedForms: number;
    activeAssignments: number;
    pendingReview: number;
    approvedAssignments: number;
    rejectedAssignments: number;
    overdueAssignments: number;
    submittedInPeriod: number;
    reviewedInPeriod: number;
  };
}

export interface AttendanceDailyMetric {
  workDate: string;
  sessions: number;
  distinctEmployees: number;
  completedSessions: number;
  openSessions: number;
  lateSessions: number;
  earlyLeaveSessions: number;
  totalBreakMinutes: number;
}

export interface EmployeeWorkload {
  employeeId: string;
  employeeCode: string;
  fullName: string;
  departmentName: string | null;
  openTasks: number;
  urgentOpenTasks: number;
  overdueTasks: number;
  activeSurveyAssignments: number;
  overdueSurveyAssignments: number;
  totalOpenItems: number;
}

export interface Employee {
  id: string;
  employeeCode: string;
  fullName: string;
  email: string;
  jobTitle: string;
  employmentType: string;
  isActive: boolean;
  departmentName: string | null;
  supervisorName: string | null;
  roles: Array<{ id: string; name: string; isActive: boolean }>;
}

export interface WorkSession {
  id: string;
  employeeId: string;
  employeeName: string;
  shiftId: string;
  shiftName: string;
  workDate: string;
  scheduledStartUtc: string;
  scheduledEndUtc: string;
  startedAtUtc: string;
  endedAtUtc: string | null;
  lateMinutes: number;
  earlyLeaveMinutes: number | null;
  totalBreakMinutes: number;
}

export interface Project {
  id: string;
  code: string;
  name: string;
  status: string;
  startDate: string | null;
  dueDate: string | null;
  activeMemberCount: number;
  openTaskCount: number;
}

export interface ProjectTask {
  id: string;
  projectId: string;
  projectCode: string;
  projectName: string;
  title: string;
  status: string;
  priority: string;
  assigneeEmployeeId: string | null;
  assigneeName: string | null;
  dueDate: string | null;
  completedAtUtc: string | null;
  commentCount: number;
}

export interface SurveyForm {
  id: string;
  projectId: string;
  projectCode: string;
  projectName: string;
  code: string;
  name: string;
  status: string;
  questionCount: number;
  assignmentCount: number;
}

export interface AuditLog {
  id: string;
  actorUserId: string | null;
  actorEmail: string | null;
  action: string;
  targetType: string;
  targetId: string | null;
  metadataJson: string | null;
  ipAddress: string | null;
  userAgent: string | null;
  createdAtUtc: string;
}
