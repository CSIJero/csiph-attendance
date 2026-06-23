# Business Proposal — Senior Developer Engagement

**Project:** Attendance Monitoring System (ASP.NET Core 8)
**Engagement type:** Senior Software Developer (lead / sole engineer)
**Document version:** 1.0
**Prepared:** May 26, 2026
**Status:** Draft for stakeholder review

> This proposal is a follow-on to the original [PROPOSAL.md](../PROPOSAL.md)
> (Flask MVP, May 2026). The system has since been rewritten on .NET 8 and
> grown well past MVP scope. This document scopes a senior-developer
> engagement to harden, extend, and operate the platform for production
> rollout and the next 12 months of feature work.

---

## 1. Executive Summary

The Attendance Monitoring System is a production-bound internal web app
replacing the legacy MS Forms-based offsite attendance workflow. It is
currently a single-developer codebase running on Render with a SQLite/
PostgreSQL data layer, cookie auth, and a live presence dashboard. It is
functional, deployed, and in active iteration — but it has now crossed
the threshold where ad-hoc maintenance is no longer sustainable.

We propose engaging a **senior .NET developer** as the technical owner of
the platform to:

1. Stabilize the codebase for production scale (security, observability,
   automated tests, CI/CD, backups, DR).
2. Deliver the prioritized feature backlog (HRIS-adjacent workflows:
   approvals, reporting, integrations, SSO).
3. Provide ongoing operational support and on-call coverage during
   business hours.

The engagement is structured in three phases over **6 months** with an
optional 6-month renewal, billable monthly with clearly defined
deliverables and acceptance criteria.

---

## 2. Current State Assessment

Findings from a code review and architecture scan of the repository.

### 2.1 What is already built (strengths)

| Area               | Status                                                                                                |
| ------------------ | ----------------------------------------------------------------------------------------------------- |
| Platform           | ASP.NET Core 8 MVC, EF Core 8, .NET 8 LTS                                                             |
| Data layer         | Dual provider — SQLite (dev) and PostgreSQL (Render production), runtime switch via connection string |
| Auth               | Cookie auth, PBKDF2-SHA256 (260k iter) password hashing, antiforgery on forms, data-protection in DB  |
| RBAC               | `admin`, `program_manager`, `pm`, `employee` with business-unit scoping; centralized in `Roles.cs`   |
| Core workflows     | Check-in / check-out, schedule editor, leave requests, edit requests, violations, quota resets       |
| Live presence      | 20s heartbeat, 60s offline threshold, `navigator.sendBeacon` on hide/unload, JSON `/api/status`       |
| Schedule engine    | Per-user weekly grid, work types (Onsite/Offsite/Dayoff), cross-midnight shift support, amendments    |
| Late & render rules| `LateCheck` service (PH 30/0 min, IN 60 min grace, Support 8h vs 9h render)                          |
| Notifications      | Multi-provider email (Brevo / SMTP / Outlook Interop), per-event templates, escalation ladder         |
| Reports            | Daily + Weekly HTML views, XLSX export (ClosedXML), CSV export                                        |
| Localisation       | UTC storage, PHT/IST display via `UserClock`, browser-timezone cookie override                        |
| Deployment         | Dockerfile + render.yaml blueprint; one-shot SQLite->Postgres migrator (`migrate-sqlite` CLI)         |
| Source recovery    | Documented local-history recovery procedure (see `/memories/repo/recovery_local_history.md`)          |

**Scale snapshot:** 17 controllers, 11 domain entities, 13 services,
46 C# files (~12.6k LOC), 84 source files including views/JS/CSS.

### 2.2 Gaps, risks, and technical debt

| # | Finding                                                                                  | Risk      |
| - | ---------------------------------------------------------------------------------------- | --------- |
| 1 | **No automated tests** — no xUnit/NUnit project; all validation is manual                | High      |
| 2 | **No CI/CD pipeline** beyond Render auto-deploy on `main` push                            | High      |
| 3 | **No migrations** — schema is managed via `EnsureCreated` + ad-hoc `ALTER TABLE`         | High      |
| 4 | **Single point of failure** — one developer, no documented runbook for incidents         | High      |
| 5 | **Backups** — relies on Render disk snapshots; no off-site copy, no restore drill        | High      |
| 6 | **Observability** — no structured logging sink, no APM, no uptime monitor                 | Medium    |
| 7 | **Email reliability** — Outlook Interop path is Windows-only; SMTP/Brevo only on prod   | Medium    |
| 8 | **Secrets management** — relies on Render env vars; no rotation policy                    | Medium    |
| 9 | **Accessibility / i18n** — no WCAG audit, only PH + IN time zones modeled                | Medium    |
|10 | **Mobile UX** — responsive but not optimized; no PWA install or offline check-in queue  | Low       |
|11 | **API surface** — internal only; no documented OpenAPI; no rate limiting on `/api/*`     | Medium    |
|12 | **Audit log** — `AttendanceAuditLog` exists but is not surfaced in any admin UI         | Low       |
|13 | **SSO / identity** — local accounts only; no Entra ID / OIDC integration                 | Medium    |
|14 | **Selfie verification** — fail-open heuristic; no stored evidence, no liveness check     | Medium    |

Each item is small individually; collectively they are the gap between
"works for the pilot team" and "trusted for company-wide HR-grade use."

---

## 3. Why a Senior Developer

The system has reached a stage where the next person on it has to:

- Own architecture decisions (test strategy, migrations, observability,
  identity, multi-tenant scoping) without supervision.
- Refactor live production code without regressing 17 controllers and
  multiple in-flight workflows (leave, edit requests, violations, etc.).
- Handle .NET 8 LTS lifecycle (security patches, .NET 10 upgrade path),
  EF Core migrations, PostgreSQL operations, and Render/Docker deploys.
- Communicate trade-offs to non-engineering stakeholders (HR, IT,
  managers) and translate ambiguous policy into deterministic code.
- Be the on-call escalation point for production incidents.

A mid-level engineer would need 1–2 weeks of ramp-up per area, would
require code review they don't currently have, and would not be able to
own incident response. This is a senior role.

### 3.1 Required profile

- **8+ years** professional software engineering, **4+ years** on ASP.NET
  Core MVC / Web API in production.
- Deep working knowledge of EF Core (migrations, query tuning, raw SQL),
  PostgreSQL, ASP.NET Identity / cookie auth / data protection.
- Practical DevOps: Docker, GitHub Actions (or equivalent), at least one
  PaaS (Render / Azure App Service / Fly.io), structured logging
  (Serilog), APM basics.
- Frontend competence in Razor + vanilla JS; comfortable with HTML/CSS
  and progressive enhancement (no SPA framework required).
- Security mindset: OWASP Top-10, threat modeling, secrets handling,
  PII boundaries, antiforgery, CSP.
- Strong written communication — this proposal, incident reports, and
  user-facing release notes are part of the deliverable.

### 3.2 Nice to have

- Microsoft Entra ID / OpenID Connect integration experience.
- Microsoft Graph (Outlook / Teams) automation.
- Experience operating apps on Render's free tier and migrating off it.
- Accessibility (WCAG 2.1 AA) practice.

---

## 4. Scope of Work

Organised into three phases. Phase 1 is non-negotiable foundation work;
Phases 2 and 3 are the feature backlog and may be re-prioritised at
month-end reviews.

### Phase 1 — Stabilize (Months 1–2)

Goal: production-grade safety net. No new user-facing features.

1. **Test harness** — add xUnit project, cover `LateCheck`, `UserClock`,
   `DbInitializer`, `AttendanceController` check-in/out, `LeaveController`,
   `ReportsController.BuildAsync`, and the `/api/*` endpoints. Target
   ≥ 60% line coverage on `Services/` and `Controllers/`.
2. **EF Core migrations** — replace `EnsureCreated` + ad-hoc `ALTER`
   patches with versioned migrations; document the baseline migration
   and reconcile current production schemas (SQLite + PostgreSQL).
3. **CI/CD** — GitHub Actions: build + test + format check on PR;
   tagged release builds publish a Docker image and deploy to Render.
4. **Observability** — Serilog with structured JSON sink; correlate
   request IDs; basic dashboard (Better Stack / Seq / Grafana Cloud free
   tier); uptime monitor pinging `/healthz`.
5. **Backups & DR** — nightly `pg_dump` to off-site object storage (S3 /
   Backblaze B2 / Render disk + offsite copy); written restore runbook;
   one drill executed and signed off.
6. **Security hardening** — Content-Security-Policy, `X-Frame-Options`,
   `X-Content-Type-Options`, rate-limit `/api/heartbeat` and login,
   refresh OWASP dependency scan (`dotnet list package --vulnerable`),
   secrets rotation policy.
7. **Documentation refresh** — single source-of-truth runbook covering
   deploy, rollback, schema change, incident response, on-call.

**Phase 1 acceptance:** all 7 items shipped; deploy is green; restore
drill passes; coverage target met; runbook approved by IT and HR.

### Phase 2 — Extend (Months 3–4)

Goal: deliver the highest-value backlog items.

1. **Entra ID / OIDC SSO** — keep local accounts as fallback; map AD
   groups to roles; document tenant configuration.
2. **Audit log UI** — surface `AttendanceAuditLog` + `AttendanceEditRequest`
   history in an admin-only view; CSV export.
3. **Reporting v2** — monthly summary, late/absent rollups by business
   unit, payroll-friendly CSV (configurable columns).
4. **Approvals inbox** — unified queue for leave, edit, quota-reset,
   schedule-amendment requests with bulk actions and decision audit.
5. **API surface** — OpenAPI/Swagger doc for `/api/*`; API-key auth for
   server-to-server callers (e.g., HRIS); rate limiting; versioning.
6. **Notifications** — Microsoft Teams webhook channel alongside email;
   per-user notification preferences; digest mode.

**Phase 2 acceptance:** each feature has tests, docs, a release note,
and a 1-week pilot with at least one team before general rollout.

### Phase 3 — Operate & Iterate (Months 5–6)

Goal: keep the lights on, respond to backlog, and prepare the platform
for year-two scale.

1. **Performance pass** — query plans on the top-5 hottest endpoints,
   add indexes, cache the dashboard `/api/status` payload (5s TTL).
2. **Mobile/PWA** — installable manifest, offline-tolerant check-in
   queue (local storage replay on reconnect), camera permission UX.
3. **Accessibility audit** — WCAG 2.1 AA pass on Login, Dashboard
   (Admin + Employee), Attendance, Schedule, Reports.
4. **Selfie v2** — store hashed thumbnail evidence (configurable
   retention), add an admin review flow, optional liveness prompt.
5. **Backlog** — at least 6 prioritised tickets per month, sized and
   delivered.
6. **Handover artifacts** — architecture decision records (ADRs),
   updated runbook, knowledge-transfer sessions for IT.

**Phase 3 acceptance:** quarterly review against success metrics
(Section 7); written go/no-go on the optional 6-month renewal.

---

## 5. Engagement Model

| Aspect           | Proposal                                                                          |
| ---------------- | --------------------------------------------------------------------------------- |
| Engagement type  | Fixed-monthly retainer (preferred) or hourly with monthly cap                     |
| Duration         | 6 months, with mutual option to renew for another 6                               |
| Allocation       | 1.0 FTE (40 hours/week) during Phase 1; negotiable in Phases 2–3                  |
| Time zone        | Philippines business hours (PHT) with overlap for India team                      |
| Location         | Remote; quarterly on-site optional and reimbursed                                 |
| Tools owned by   | Client owns repo, Render account, domain, email tenancy, all secrets             |
| IP ownership     | All work-for-hire; assigned to client on payment                                  |
| Confidentiality  | NDA at engagement start                                                           |
| Liability        | Capped at three months of fees                                                    |

### 5.1 Working agreement

- **Sprint cadence:** 2-week sprints, demo on the last Friday, retro the
  following Monday.
- **Reporting:** weekly written status (Sent EOW Friday) covering
  shipped, in-progress, blocked, risks.
- **Code review:** at least one client-side reviewer required to merge
  to `main`. If unavailable, paired review with a designated nominee.
- **On-call:** Mon–Fri 09:00–18:00 PHT, 1-hour SLA acknowledge,
  best-effort resolution; out-of-hours best-effort only.
- **Change control:** any schema change or destructive migration
  requires written sign-off from client lead before execution.

---

## 6. Investment

The numbers below are **illustrative placeholders** sized for a
mid-senior PH-market rate. They are not a binding quote until both
parties countersign Section 11. Replace with the agreed figures during
negotiation.

| Line item                                | Quantity | Rate           | Subtotal      |
| ---------------------------------------- | -------- | -------------- | ------------- |
| Senior developer (1.0 FTE)               | 6 months | PHP _xxx,xxx_ /mo | PHP _x,xxx,xxx_ |
| Tooling (Better Stack / Sentry / backup) | 6 months | PHP _x,xxx_ /mo   | PHP _xx,xxx_   |
| Infra delta (Render Standard tier, DB)   | 6 months | PHP _x,xxx_ /mo   | PHP _xx,xxx_   |
| Contingency (10%)                        | —        | —              | PHP _xx,xxx_   |
| **Total (Phase 1–3, 6 months)**          | —        | —              | **PHP _x,xxx,xxx_** |

Hourly alternative: PHP _x,xxx_ /hr, capped at 160 hrs/month.

**Payment terms:** monthly in arrears, net-15, against an accepted
status report. First month invoiced on engagement start (50% deposit
optional for the first month only).

**Exclusions (not in the price above):**
- Render / database / email / domain hosting fees (paid directly by
  client).
- Third-party licenses (Entra ID, Teams webhooks, etc.).
- Travel and accommodation for on-site visits.

---

## 7. Success Metrics

Measured at the end of each phase against a baseline captured in week 1.

| KPI                                          | Baseline (current) | Target (end of engagement) |
| -------------------------------------------- | ------------------ | -------------------------- |
| Automated test coverage (`Services/`)        | 0%                 | ≥ 70%                      |
| Mean time to restore from backup             | Unknown            | ≤ 30 min, drilled monthly  |
| Production deploy frequency                  | Ad-hoc on `main`   | ≥ 2/week via CI            |
| Mean time to acknowledge incident            | Best-effort        | ≤ 1 hour (business hours)  |
| Login success rate                           | Not measured       | ≥ 99.5%                    |
| Dashboard p95 latency (admin team table)     | Not measured       | < 500 ms at 100 users      |
| OWASP scan critical/high findings            | Unknown            | 0 open                     |
| Stakeholder satisfaction (quarterly survey)  | n/a                | ≥ 4 / 5                    |

Failure to meet two or more targets at a phase boundary triggers a
joint root-cause review and a corrective-action plan before the next
phase begins.

---

## 8. Risks and Mitigations

| Risk                                                  | Likelihood | Impact | Mitigation                                                  |
| ----------------------------------------------------- | ---------- | ------ | ----------------------------------------------------------- |
| Schema migration breaks live data                     | Medium     | High   | All migrations rehearsed on a Postgres dump + restore drill |
| Render free tier limits hit during scale-up           | Medium     | Medium | Capacity review in Phase 1; budgeted upgrade path           |
| Single-developer bus factor                           | Medium     | High   | ADRs + runbook in repo; quarterly KT session with client    |
| Scope creep into payroll / HRIS                       | High       | Medium | Section 4 acceptance gates; backlog grooming at sprint end  |
| Outlook Interop email fails on Linux production       | Already realised | Medium | Confirm SMTP path is the only one used in prod; remove Interop in Phase 2 |
| Selfie evidence raises privacy concerns               | Medium     | Medium | DPIA before Phase 3 v2 rollout; opt-in by business unit     |
| Key personnel attrition (developer or client lead)    | Low        | High   | 30-day handover clause; all work in repo, no local-only state |

---

## 9. Assumptions and Dependencies

The proposal assumes the client provides:

- Access to the GitHub repository and Render account on day 1.
- A designated business owner (HR / IT) for sign-offs.
- A code reviewer with at least merge-blocking authority on PRs.
- A test/staging Render environment in addition to production.
- Test PostgreSQL credentials and a sanitised data snapshot.
- An Entra ID tenant administrator available for the SSO milestone.
- A nominated alternate developer for at least 4 hours/week of pairing
  from month 3 onward to start absorbing context (handover insurance).

---

## 10. Out of Scope

The following are explicitly out of scope and would be quoted
separately if requested:

- Payroll calculation, leave-balance accruals, or salary integration.
- Native iOS / Android applications.
- Biometric hardware integration (fingerprint, face liveness vendor).
- Migration off ASP.NET Core to another stack.
- Building a full HRIS or replacing existing HR tools.
- Multi-tenant SaaS productisation of the platform.

---

## 11. Approval

| Role                | Name | Signature | Date |
| ------------------- | ---- | --------- | ---- |
| Project sponsor     |      |           |      |
| Engineering / IT lead |    |           |      |
| HR / People Ops     |      |           |      |
| Information security |     |           |      |
| Senior developer (contractor) | |     |      |

> Countersigning Section 11 constitutes acceptance of Sections 1–10
> at the rates filled into Section 6. The engagement begins on the
> first business day following the latest signature date.

---

## Appendix A — Repository Snapshot (as scanned)

- Solution: `Attendance_Monitoring.sln` / `AttendanceMonitoring.csproj`
  (net8.0, nullable on, implicit usings on).
- Key packages: `Microsoft.EntityFrameworkCore.Sqlite 8.0.10`,
  `Npgsql.EntityFrameworkCore.PostgreSQL 8.0.10`, `MailKit 4.16`,
  `ClosedXML 0.104.2`, `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore 8.0.10`,
  pinned `System.Security.Cryptography.Xml 8.0.3` (CVE remediation).
- 17 controllers: Account, Api, App, Attendance, Dashboard,
  Diagnostics, Home, Leave, Profile, QuotaReset, Reports, Roles,
  Schedule, Settings, Users, Violations, Weekly.
- 11 entities: User, Attendance, AttendanceAuditLog,
  AttendanceEditRequest, LeaveRequest, NotificationLog,
  QuotaResetRequest, RoleDefinition, ScheduleAmendment, ScheduleEntry,
  Constants.
- 13 services: BrevoEmailSender, ClientInfo, EmailOptions,
  ForcePasswordChangeMiddleware, IEmailSender, LateCheck,
  OfflineNotifierService, OutlookInteropEmailSender, PasswordHasher,
  PhTime, SmtpEmailSender, TeamsSyncService, UserClock.
- Deployment artefacts: `Dockerfile`, `render.yaml`,
  `tools/SqliteToPostgres.cs` (one-shot CLI migrator invoked via
  `dotnet run -- migrate-sqlite ...`).
- Existing docs: `README.md`, `DEPLOY.md`, `USER_MANUAL.md`,
  `PROPOSAL.md` (legacy Flask MVP), `docs/PROD_TEST_REPORT_2026-05-21.md`.

## Appendix B — Suggested First 30 Days

| Day  | Activity                                                                   |
| ---- | -------------------------------------------------------------------------- |
| 1    | Repo + Render + Render Postgres access; read-only walkthrough              |
| 2    | Pair with current developer; capture verbal runbook into `docs/RUNBOOK.md` |
| 3–4  | Stand up xUnit project + GitHub Actions CI; first 3 unit tests merged      |
| 5–7  | Baseline Serilog + `/healthz` + uptime monitor                             |
| 8–10 | Spike + decision on EF Core migration baseline                             |
| 11–15| Migrate baseline applied to staging; restore drill rehearsed               |
| 16–20| Auth/cookie/CSP hardening; OWASP scan; close all High findings             |
| 21–25| First production deploy via CI; rollback rehearsal                         |
| 26–30| Phase 1 mid-point demo + stakeholder check-in                              |

---

*Prepared by: Engineering*
*Last updated: May 26, 2026*
