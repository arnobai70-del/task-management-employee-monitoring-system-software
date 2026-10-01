# Project Completion Audit

This file is the single source of truth for completion percentage. The percentage must not be changed ad hoc.

## Scoring rule

The product is divided into 25 equal delivery domains. Each domain is worth 4 percentage points.

A domain is counted complete only when its applicable backend/data/UI or client behavior, authorization, validation, tests/CI and operational documentation are present. Partial work does not receive fractional credit in the headline percentage; it remains pending until its completion gate is met.

## Verified baseline at main `a88c2f2256ecad65137e919483e5d2765883700d`

| # | Delivery domain | Status |
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
| 24 | Centralized production release/deployment control plane | Pending |
| 25 | Real-environment signed production launch/operator acceptance | Pending |

**Verified codebase completion baseline: 23 / 25 = 92%.**

## Remaining domain 24 gate

The centralized production release/deployment control plane is complete only when the repository provides a permission-gated release registry/control API and Admin UI that can:

- register immutable release candidates and verification metadata;
- require explicit approval before promotion;
- expose current published release and deployment readiness;
- correlate production health, backup freshness and agent rollout readiness;
- record promotion, deployment verification, rollback decision and release history in the audit trail;
- prevent unsafe promotion when required gates are not satisfied;
- avoid arbitrary remote-command execution and preserve the existing read-only update-host trust boundary;
- include regression tests and documentation.

Completing this domain moves the codebase score to **24 / 25 = 96%**.

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

The project reaches **100%** only after those real-environment gates are executed and recorded.

## Change-control rule

Future percentage reports must cite this checklist. A domain may move from Pending to Complete only after its implementation is merged to `main` with the applicable fresh CI gates green. If a new mandatory project requirement is added, this rubric must be revised explicitly before quoting a new percentage.
