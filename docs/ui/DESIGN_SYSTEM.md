# Admin Web Design System (Phase 5 draft)

Status: **DRAFT** — direction and inventory only. No UI code has been changed yet. Gap G-02 / G-13 / G-18.

## 1. Current state (baseline `c38dca3`)
- Native CSS: `src/AdminWeb/src/styles.css` (132 lines), `admin-experience.css` (698 lines), `management.css`.
- Tokens on `:root`: `--nav #101827`, `--surface #fff`, `--line #dfe6ef`, `--muted #667085`, `--accent #2563eb`, `--danger #b42318`, `--success #027a48`, `--warning #b54708`; body `#f3f6fb`; font stack Inter/system.
- Sidebar 250 px fixed, navy, two-letter badges (`DB`, `EM`, `AT`, …) from `App.tsx:39-56`, brand mark "TM" on blue.
- Single JS bundle 567 KB raw / 144 KB gzip; CSS 23 KB.
- No dark theme; focus ring exists (`outline: 3px solid rgba(37,99,235,.28)`).

## 2. Route inventory (must all survive the redesign)
| Group (proposed) | Route | Permission |
| --- | --- | --- |
| Overview | `/dashboard` | reports.read |
| Overview | `/operations` | reports.read |
| Workforce | `/employees` | employees.read |
| Workforce | `/departments` | departments.read |
| Attendance | `/shifts` | shifts.read |
| Attendance | `/attendance` | attendance.read |
| Work | `/projects` | projects.read |
| Work | `/tasks` | tasks.read |
| Work | `/website-work` | tasks.read |
| Work | `/follow-ups` | tasks.manage |
| Work | `/surveys` | surveys.read |
| Monitoring | Presence panel (dashboard) | presence.read |
| Reports | `/productivity` | reports.read |
| Reports | `/sla-analytics` | reports.read |
| Operations | `/access/rdp`, `/access/ip`, `/access/websites` | access.assignments.read |
| Settings / Security | `/security-alerts` | audit.read |
| Settings / Security | `/audit-logs` | audit.read |
| Public | `/login`, `/forbidden`, `*` | — |

## 3. Proposed tokens (from the master plan, to be confirmed against owner brand)
| Token | Light | Dark | Use |
| --- | --- | --- | --- |
| `--bg` | `#F7F9FA` | `#0F1A26` | page |
| `--surface` | `#FFFFFF` | `#152233` | cards, tables |
| `--ink` | `#152233` | `#E8EEF3` | primary text |
| `--muted` | `#5B6B7B` | `#9AA8B6` | secondary text (≥4.5:1 to be verified) |
| `--accent` | `#087F76` | `#3BB3A8` | primary actions, active nav |
| `--warning` | `#D69B36` | `#E7B45C` | warnings (never text on white without dark ink) |
| `--danger` | `#B42318` | `#F0776B` | errors |
| `--radius` | 12 px (cards), 8 px (controls) | same | |
| spacing | 8 px grid (4 for dense tables) | same | |
| motion | 120–180 ms ease-out; disabled under `prefers-reduced-motion` | same | |

Theme: Light / Dark / Follow system; choice stored per device (`localStorage`, wrapped in try/catch).

## 4. Components to build (reusable, no UI library)
Button, IconButton, Input, Select, Textarea, Badge/StatusPill, Table (sortable header, pagination, empty/error/loading rows), Toast, Modal (single level), EmptyState, Skeleton, Tabs, PageHeader, FilterBar.

## 5. Semantics rules (G-08)
- "Connected" = heartbeat within threshold. "On shift" = open attendance session. "On break" = open break. "Submitted"/"Approved" = Website Work review states.
- Never label presence as "productive". Rename the current `InProgress → "Working"` mapping (`PresencePanel.tsx:125`) to a term that states what is measured.

## 6. Acceptance (Gate 5)
- Every route above reachable for the same roles as today (automated RBAC route test).
- 320/375/768/1024/1440 px screenshots without overlap; keyboard-only navigation; visible focus; WCAG 2.2 AA contrast checked with a tool.
- Initial JS payload not larger than baseline (144 KB gzip); route-level code splitting target < 100 KB gzip initial.
