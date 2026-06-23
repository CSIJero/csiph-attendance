# Attendance Monitoring

ASP.NET Core 8 MVC + EF Core (SQLite) attendance monitoring app with a live
dashboard that shows who is currently online, who has checked in for the day,
and full attendance history. All times are displayed in **Philippine Standard
Time** (UTC+8).

## Features

- Cookie-based login / logout with PBKDF2 password hashes
- `admin` and `employee` roles; admins can add new users
- One-click **check-in / check-out** with daily attendance log
- **Live "online" status** powered by client heartbeats (20 s interval, 60 s
  threshold)
- Auto-refreshing admin dashboard with KPIs (online, offline, present today,
  total users)
- Per-user weekly schedule (offsite Tue–Thu 09:00–17:00 by default; Mon, Fri, and weekends off)
- Attendance history with date filters and CSV export
- Immediate offline signal on tab hide / browser close / Windows lock
- JSON API: `POST /api/heartbeat`, `POST /api/offline`, `GET /api/status`

## Quick start (Windows / PowerShell)

```powershell
cd C:\Code\Attendance_Monitoring
dotnet restore
dotnet run
```

Then open <http://127.0.0.1:5000>.

The SQLite database `attendance.db` is created next to the executable on
first run, the schema is created with `EnsureCreated`, and a couple of demo
users are seeded.

## Deploying to Render (free tier)

This repo ships with a `Dockerfile` and `render.yaml` so the whole stack can
be brought up by clicking through Render's UI — no credit card required.

1. **Push this repo to GitHub** (see [DEPLOY.md](DEPLOY.md) for the exact
   git commands).
2. Sign in at <https://render.com> with your GitHub account.
3. **New → Blueprint** → select the `csiph-attendance` repo → Render reads
   `render.yaml`, provisions the web service + the 1 GB persistent disk
   for SQLite, and starts the first build.
4. In the service's **Environment** tab, fill in the two `sync: false`
   variables with your SMTP credentials:
   - `Email__Username` → SMTP login (e.g. Brevo API user)
   - `Email__Password` → SMTP password / API key
5. First deploy takes 3–5 minutes; once it's `Live`, open the
   `https://csiph-attendance.onrender.com` URL and log in with the seeded
   admin (`admin` / `Admin123!`) and change the password immediately.

> **Heads-up:** The Windows-only `OutlookInteropEmailSender` is bypassed in
> production because `appsettings.Production.json` sets `Email:Provider` to
> `Smtp`. Make sure your SMTP relay (Brevo, SendGrid, Mailgun, etc.) is
> configured before any user accounts are approved — the offline-notifier
> service runs every minute and will try to send mail.

### Default credentials (created on first run)

| Role     | Username | Password      | Employee ID |
| -------- | -------- | ------------- | ----------- |
| Admin    | admin    | Admin123!     | —           |
| Employee | jdoe     | Password123!  | E000001     |

## How "online" detection works

While a user is logged in, the browser pings `POST /api/heartbeat` every 20
seconds. The server stores the timestamp in `users.LastSeen`. A user is
considered **online** if their last heartbeat is within the last 60 seconds
(see `AttendanceMonitoring:OnlineThresholdSeconds` in
[appsettings.json](appsettings.json)).

When the tab is hidden, closed, or the workstation is locked, `app.js` fires a
`POST /api/offline` (via `navigator.sendBeacon` for unloads) which clears
`LastSeen` so the dashboard updates within one refresh tick instead of waiting
for the 60 s threshold.

## Project layout

```
AttendanceMonitoring.csproj   Project file (net8.0, EF Core SQLite)
Program.cs                    Pipeline + service registration + DB seed
appsettings.json              Connection string & app options
Properties/launchSettings.json Dev URLs

Controllers/                  MVC controllers
  AccountController.cs        Login / Logout
  AttendanceController.cs     History + CSV export + Check-in / Check-out
  DashboardController.cs      Admin & employee dashboards
  ScheduleController.cs       Weekly schedule editor
  UsersController.cs          Admin: add user
  ApiController.cs            JSON heartbeat / offline / status
  HomeController.cs           Root + error pages

Data/
  AppDbContext.cs             EF Core DbContext
  DbInitializer.cs            EnsureCreated + admin/demo seed

Models/
  User.cs / Attendance.cs / ScheduleEntry.cs / Constants.cs

ViewModels/ViewModels.cs      Strongly-typed view models

Services/
  PhTime.cs                   PHT formatting helpers
  PasswordHasher.cs           PBKDF2-SHA256

Helpers/
  FlashExtensions.cs          TempData-backed flash messages

Views/
  Shared/_Layout.cshtml       Base layout + flashes + heartbeat script
  Account/Login.cshtml
  Dashboard/Admin.cshtml
  Dashboard/Employee.cshtml
  Schedule/Index.cshtml
  Attendance/Index.cshtml
  Users/New.cshtml
  Shared/Error.cshtml

wwwroot/
  css/style.css
  js/app.js
```

## Configuration

| Key                                              | Default                       | Notes                                                              |
| ------------------------------------------------ | ----------------------------- | ------------------------------------------------------------------ |
| `ConnectionStrings:DefaultConnection`            | `Data Source=attendance.db`   | Any provider supported by EF Core (swap to PostgreSQL/SQL Server)  |
| `AttendanceMonitoring:OnlineThresholdSeconds`    | `60`                          | Seconds since last heartbeat to count as online                    |
| `AttendanceMonitoring:SessionLifetimeHours`      | `12`                          | Auth cookie lifetime                                               |
| `AttendanceMonitoring:RequireSecureCookie`       | `false`                       | Set `true` behind HTTPS in production                              |

Override any of these via environment variables, for example:

```powershell
$env:AttendanceMonitoring__RequireSecureCookie = "true"
$env:ConnectionStrings__DefaultConnection = "Data Source=C:\data\attendance.db"
dotnet run
```

## Build & publish

```powershell
dotnet build -c Release
dotnet publish -c Release -o .\publish
.\publish\AttendanceMonitoring.exe
```
