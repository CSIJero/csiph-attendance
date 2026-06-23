# Project Proposal — Attendance Monitoring System

| Field            | Value                                              |
| ---------------- | -------------------------------------------------- |
| Project name     | Attendance Monitoring System                       |
| Document version | 1.0                                                |
| Date             | May 6, 2026                                        |
| Prepared by      | Engineering / IT                                   |
| Status           | Draft for stakeholder review                       |

---

## 1. Executive Summary

The Attendance Monitoring System is an internal web application that
replaces the current Microsoft Forms–based "Offsite Attendance Records"
process with a faster, automated, and auditable solution. Employees clock
in and out from a single dashboard; managers see live presence and daily
attendance in real time. The application is built with a lightweight Flask
+ SQLite stack, runs on commodity hardware, and can be deployed to a
single internal server in under an hour.

**Headline benefits**

- Reduces daily attendance friction from ~6 form clicks to **1 click**.
- Eliminates duplicate / mistimed records — server timestamps are
  authoritative and use Philippine Standard Time (PHT, UTC+8).
- Gives managers a **live "who is online now"** view that the existing MS
  Forms workflow cannot offer.
- Provides a foundation for future HR-related features (overtime,
  schedule management, exception reports).

---

## 2. Background and Problem Statement

Today the team relies on **Offsite Attendance Records_2026** (Microsoft
Forms) for daily clock-in / clock-out events. While simple, it has known
pain points:

| Pain point                                                    | Impact                                                  |
| ------------------------------------------------------------- | ------------------------------------------------------- |
| Employees re-type Employee ID, email, name every clock event  | Friction; errors; ~30s per submit × 2 × N people / day  |
| No real-time visibility for managers                          | Hard to confirm staff are actively working offsite      |
| Forms responses live in a single Excel sheet, hard to query   | Slow reporting; manual roll-ups                         |
| URL pre-fill is **disabled** at the tenant level              | Cannot be auto-filled even with carefully crafted links |
| No notion of a working schedule per employee                  | Late-ins / no-shows are not flagged automatically       |

Our prototype shows that a small in-house tool can address all of these
without losing the data that the existing Excel workbook captures.

---

## 3. Goals and Non-Goals

### 3.1 Goals

1. One-click check-in / check-out for every employee.
2. Live presence dashboard for admins (refreshes every 15 seconds).
3. Per-employee weekly schedule defaulted to **offsite Tue–Thu 09:00–17:00**
   (Mon, Fri, and weekends are deactivated).
4. Attendance history per employee, exportable to Excel/CSV.
5. Role-based access control (Admin vs Employee).
6. All times shown in **Philippine Standard Time** while stored in UTC.

### 3.2 Non-goals (this phase)

- Payroll calculation, leave management, or HRIS integration.
- Mobile native apps (the responsive web UI is sufficient).
- Geo-fenced offsite verification (can be added later via heartbeat
  metadata).

---

## 4. Proposed Solution

A self-hosted Flask web application with three primary surfaces:

1. **Employee dashboard** — personal status, today's schedule, weekly grid,
   recent attendance, large Check-in / Clock-out buttons.
2. **Admin dashboard** — KPI tiles (online / offline / present today /
   total users), live team table with status, today's record, and
   last-seen timestamp.
3. **Schedule editor** — Mon–Sun grid with working/off toggle, start/end
   times, and free-text note. Admins can edit any user's schedule.

A heartbeat ping (`POST /api/heartbeat`) every 20 seconds while the tab is



open keeps `users.last_seen` fresh; a user is "online" if seen within the
last 60 seconds.

### 4.1 High-level architecture

```
┌────────────┐  HTTPS  ┌────────────────────────────┐   SQLite file
│  Browser   │◀───────▶│  Flask app (gunicorn/IIS)  │──────────────▶ attendance.db
└────────────┘         │  - Auth (Flask-Login)      │
                       │  - SQLAlchemy ORM          │
                       │  - JSON status / heartbeat │
                       └────────────────────────────┘
```

Optional: replace SQLite with PostgreSQL (one config change:
`DATABASE_URL`) when the user count exceeds ~50 active responders.

### 4.2 Technology choices

| Concern        | Choice                          | Why                                     |
| -------------- | ------------------------------- | --------------------------------------- |
| Web framework  | Flask 3                         | Minimal, well known, easy to extend     |
| ORM / DB       | SQLAlchemy + SQLite (dev) /     | Zero-ops at small scale; Postgres-ready |
|                | PostgreSQL (production option)  |                                         |
| Auth           | Flask-Login + werkzeug hashes   | Battle-tested; no plaintext passwords   |
| UI             | Jinja templates + vanilla JS    | No build pipeline; auditable            |
| Time handling  | UTC storage + PHT display       | Avoids DST/locale bugs                  |
| Deployment     | Single Python process           | Runs anywhere Python 3.10+ exists       |

---

## 5. Functional Requirements

### 5.1 Authentication & users
- Username/password login with hashed credentials.
- Roles: `admin` and `employee`.
- Admin-only "Add user" page captures Employee ID (E + 6–7 digits),
  full name, email, role, temporary password.

### 5.2 Attendance
- One open record per user per day (PHT date).
- `POST /check-in` opens a record; `POST /check-out` closes it.
- Auto check-out on logout if a record is still open.
- Duration computed in minutes when both timestamps exist.

### 5.3 Schedule
- 7 rows per user, Monday → Sunday.
- Defaults reflect the team's offsite cadence:
  - **Tue / Wed / Thu** → working, 09:00–17:00, note `Offsite`.
  - **Mon / Fri / Sat / Sun** → deactivated (off).
- Admins can edit any user's schedule via `?user_id=`.

### 5.4 Live status
- 15-second auto-refresh of `/api/status` (admin dashboard).
- 20-second heartbeat from authenticated tabs.
- Presence pill on the top bar shows current user's online dot.

---

## 6. Non-Functional Requirements

| Area            | Target                                                        |
| --------------- | ------------------------------------------------------------- |
| Availability    | Business hours, ≥ 99% (single-server is acceptable in v1)     |
| Performance     | Dashboard p95 < 500 ms on 100 active users / 1k records / day |
| Security        | OWASP Top-10 hygiene; CSRF token on POSTs; bcrypt-class hash  |
| Privacy         | Minimal PII (name, email, employee ID, attendance times)      |
| Auditability    | All check-ins/outs are immutable rows                         |
| Backup          | Nightly file copy (SQLite) or pg_dump (PostgreSQL)            |

---

## 7. Security & Compliance

- Passwords stored as `werkzeug.security.generate_password_hash` (PBKDF2).
- Session cookies use `HttpOnly` + `Secure` (when behind HTTPS).
- No external scripts or trackers — all assets are local.
- PII boundary is the SQLite/Postgres database only. Backups should be
  encrypted at rest.
- Production must run behind HTTPS (corporate reverse proxy or IIS/nginx
  termination).

---

## 8. Migration & Rollout Plan

| Phase | Duration   | Activities                                                     |
| ----- | ---------- | -------------------------------------------------------------- |
| 0     | 1 week     | Stakeholder sign-off on this proposal; pick host (VM / IIS)    |
| 1     | 1 week     | Pilot with 1 team (≤ 10 employees) on staging instance         |
| 2     | 2 weeks    | Tune schedules, add reports, write CSV export for HR           |
| 3     | 1 week     | Cut over: disable MS Forms link, redirect employees to new app |
| 4     | Ongoing    | Bug-fix / feature backlog managed in GitHub Issues             |

Historical MS Forms data will be **imported once** via CSV → `attendance`
rows so the new app shows continuity from day 1.

---

## 9. Risks & Mitigations

| Risk                                              | Likelihood | Impact | Mitigation                                                       |
| ------------------------------------------------- | ---------- | ------ | ---------------------------------------------------------------- |
| User adoption — change fatigue                    | Medium     | Medium | Mirror MS Forms fields exactly; 1-page training doc              |
| Single-server outage                              | Low        | High   | Host on monitored VM; nightly DB backups; runbook for restart    |
| Time-zone confusion (UTC vs PHT)                  | Low        | Medium | All UI shows "PHT"; DB rows stored in UTC; tests cover boundary  |
| Scope creep (payroll, leaves)                     | High       | Medium | Non-goals section above; tracked separately                      |
| Employee logs in from multiple tabs / devices     | Medium     | Low    | Heartbeat uses last write wins; one open record per (user, date) |

---

## 10. Cost & Resource Estimate

| Item                                      | Estimate                                             |
| ----------------------------------------- | ---------------------------------------------------- |
| Engineering (build + harden + reports)    | ~4 person-weeks (1 dev)                              |
| Infrastructure                            | 1 small VM (2 vCPU / 4 GB RAM) or existing IIS host  |
| External licenses                         | None — all libraries are open source (BSD/MIT)       |
| Recurring                                 | Backup storage; optional PostgreSQL managed instance |

No SaaS fees. No third-party data processors.

---

## 11. Success Metrics

Measured 30 days after rollout vs. the MS Forms baseline:

1. **Average time to clock in/out** drops from ~30 s to **< 5 s**.
2. **Attendance completeness** (records expected vs. submitted) rises to
   **≥ 99%** (vs. ~92% today).
3. **Manager satisfaction** survey ≥ 4 / 5 on "Can I tell who is working
   right now?".
4. **Zero** reported password / data incidents.

---

## 12. Future Roadmap (Post-MVP)

- Manager-approved adjustments (forgot to clock out, etc.).
- Public holiday calendar applied to schedules.
- Shift swaps and time-off requests.
- Slack / Teams notifications for late check-ins.
- Active Directory / Microsoft Entra SSO.
- REST API for HR systems and payroll exports.

---

## 13. Appendix A — Current Build Snapshot

Repository: `C:\Code\Attendance_Monitoring`

```
app.py                  Flask app, routes, heartbeat + status APIs
models.py               SQLAlchemy models (User, Attendance, ScheduleEntry)
templates/              Jinja2 templates (dashboards, schedule, login, etc.)
static/css/style.css    Dashboard styling
static/js/app.js        Heartbeat + live refresh (PHT formatted)
requirements.txt        Flask 3, Flask-SQLAlchemy, Flask-Login, Werkzeug
```

Default seeded users (created on first run):

| Role     | Username | Password     | Employee ID |
| -------- | -------- | ------------ | ----------- |
| Admin    | admin    | Admin123!    | —           |
| Employee | jdoe     | Password123! | E000001     |

Run locally:

```powershell
cd C:\Code\Attendance_Monitoring
python -m venv .venv
.\.venv\Scripts\Activate.ps1
pip install -r requirements.txt
python app.py
# open http://127.0.0.1:5000
```

---

## 14. Approval

| Role               | Name          | Signature | Date |
| ------------------ | ------------- | --------- | ---- |
| Project sponsor    |               |           |      |
| Engineering lead   |               |           |      |
| HR / People ops    |               |           |      |
| Information sec.   |               |           |      |
