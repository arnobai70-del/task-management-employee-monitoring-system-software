# Project Completion Audit

This file is the single source of truth for completion percentage. The percentage must not be changed ad hoc.

## Scoring rule

The product is divided into 25 equal delivery domains. Each domain is worth 4 percentage points.

A domain is counted complete only when its applicable backend/data/UI or client behavior, authorization, validation, tests/CI and operational documentation are present. Partial work does not receive fractional credit in the headline percentage; it remains pending until its completion gate is met.

## Baseline before Production Release Control Center

At main `a88c2f2256ecad65137e919483e5d2765883700d`, 23 of 25 delivery domains were complete: **92%**.

| # | Delivery domain | Status after PR #41 is merged |
|---|---|---|
| 1 | Backend/.NET/PostgreSQL foundation and migrations | Complete |
| 2 | Authentication, refresh-token rotation, lockout and RBAC | Complete |
| 3 | Employee/department/role administration | Complete |
| 4 | Shift and attendance workflows | Complete |
| 5 | Project and task management | Complete |
| 6 | Website-work workflow, review, follow-up and SLA tooling | Complete |
| 7 | Survey/field-work employee workflow | Complete |
| 8 | Reporting and management dashboards | Complete |
| 9 | RDP/IP/website access assignment controls | Complete |
| 10 | Realtime presence and durable notifications | Complete |
| 11 | Transparent approved monitoring telemetry and retention | Complete |
| 12 | Permission-aware Admin Web | Complete |
| 13 | Employee Windows Desktop | Complete |
| 14 | Visible Windows Service health agent | Complete |
| 15 | Employee updater, signed package verification and rollback | Complete |
| 16 | Centralized employee-agent update enrollment/rollouts | Complete |
| 17 | Production operations health and incident management | Complete |
| 18 | Production Docker/HTTPS topology and secret-file deployment path | Complete |
| 19 | PostgreSQL backup/restore and production acceptance automation | Complete |
| 20 | Production security hardening and permission audit | Complete |
| 21 | Security observability, integrity checks and compliance export | Complete |
| 22 | Automated security alerting and escalation | Complete |
| 23 | CI quality gates including Windows lifecycle acceptance | Complete |
| 24 | Centralized production release/deployment control plane | Complete by PR #41; counts after merge |
| 25 | Real-environment signed production launch/operator acceptance | Pending |

## Domain 24 evidence

PR #41 satisfies the centralized production release/deployment control gate with:

- permission-gated immutable release candidate registration;
- explicit signed-artifact and publisher-fingerprint verification attestations;
- explicit approval before promotion;
- promotion gates covering API/database health, backup freshness, critical operations/security signals, active agent rollouts and version progression;
- observed stable release/deployment readiness in the Admin Operations console;
- exact live `release.json` SHA-256 and metadata verification;
- exact runtime package size/SHA-256 verification;
- archived publisher fingerprint/certificate verification;
- durable audit-backed approval, promotion, deployment, supersede, rollback and withdrawal history;
- an operator-owned archived-release rollback helper that preserves the API's read-only update-host boundary;
- backend regression tests for approval verification, blocked health gates, tampered package rejection, deployment verification, supersede and rollback restoration;
- production and operator documentation.

The domain counts only after PR #41's final exact head passes the full CI gate and is merged to `main`.

**Post-merge verified codebase completion: 24 / 25 = 96%.**

## Remaining domain 25 gate

The final 4% is real-environment acceptance and cannot be truthfully completed by repository code or CI alone. It requires organization-owned inputs and evidence such as:

- real DNS and trusted public TLS;
- real production infrastructure and storage;
- organization code-signing private key/certificate;
- signed production release workflow execution;
- independent publisher-fingerprint verification;
- successful pilot install/update/rollback on real office hardware/network;
- production backup retention/off-site storage policy;
- final business/security approval for employee monitoring deployment.

Repository-side execution support for this gate is provided by:

- `scripts/windows/collect-production-pilot-evidence.ps1` — verifies the real installed pilot version, Authenticode publisher identity, Windows Service health/recovery, updater task, signed-package enforcement, production URLs and explicit workflow/update/rollback/disclosure attestations;
- `scripts/production/accept-real-production-launch.sh` — verifies real DNS/HTTPS reachability, production server acceptance, exact published bytes against the signed bundle, independent publisher fingerprint, independent backup-copy hash equality, pilot evidence, and required approval references;
- `docs/real-production-launch-acceptance.md` — the final operator runbook;
- `.github/workflows/production-launch-tooling.yml` — CI parsing/fail-closed validation for the acceptance tooling without pretending to provide real production evidence.

Domain 25 moves from Pending to Complete only when `accept-real-production-launch.sh` succeeds against the real environment and emits a retained `TaskMonitoringRealProductionLaunchAcceptance` JSON evidence file plus SHA-256. The non-secret evidence reference and acceptance timestamp must then be recorded here.

The project reaches **100%** only after those real-environment gates are executed and recorded.

## Change-control rule

Future percentage reports must cite this checklist. A domain may move from Pending to Complete only after its implementation is merged to `main` with the applicable fresh CI gates green. If a new mandatory project requirement is added, this rubric must be revised explicitly before quoting a new percentage.
