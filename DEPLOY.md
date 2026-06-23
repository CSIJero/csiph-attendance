# Deployment guide

This file collects everything you need to do **outside the codebase** to get
the app running on Render. The repository is already set up with the
`Dockerfile`, `.dockerignore`, `appsettings.Production.json`, and
`render.yaml` Render needs.

---

## 1. Install Git for Windows (one-time, if not already installed)

```powershell
winget install --id Git.Git -e --source winget
```

After the installer finishes, **close and reopen any PowerShell window** so
`git` is on `PATH`. Verify:

```powershell
git --version
```

## 2. Configure your git identity (one-time)

```powershell
git config --global user.name  "Your Name"
git config --global user.email "you@example.com"
```

## 3. Push this folder to your GitHub repo

> Replace the email/name above with whatever you want shown on the commits.

```powershell
cd C:\Code\Attendance_Monitoring

git init
git branch -M main

# Make sure the local DB and the .bak file never make it into the push.
git status                # double-check attendance.db is NOT listed.

git add -A
git commit -m "Initial commit: attendance monitoring app"

git remote add origin https://github.com/csiattendancemonitor-dot/csiph-attendance.git
git push -u origin main
```

The first push will pop up the **Git Credential Manager** in your browser
asking you to sign in to GitHub. Once you do, the credentials are cached
and future pushes are silent.

If GitHub refuses the push because the repo already has commits on it
(e.g. an auto-generated README), pull and merge first:

```powershell
git pull origin main --allow-unrelated-histories
git push -u origin main
```

## 4. Create the Render service

1. Go to <https://render.com> and sign in with GitHub.
2. Authorize Render to read your repos when prompted.
3. Click **New +** → **Blueprint**.
4. Pick `csiattendancemonitor-dot/csiph-attendance` from the list.
5. Render reads `render.yaml`, shows you the planned service +
   `data` disk, then click **Apply**.
6. Wait ~5 minutes for the first build.

## 5. Set the SMTP credentials in Render

Production uses SMTP, not Outlook Interop (which is Windows-only).

In the Render dashboard, open the `csiph-attendance` service →
**Environment** tab → set:

| Key                | Value                                  |
| ------------------ | -------------------------------------- |
| `Email__Username`  | Brevo / SendGrid / Mailgun login       |
| `Email__Password`  | API key or SMTP password               |

Recommended free SMTP relay: **Brevo** (`smtp-relay.brevo.com`, 300
emails/day on the free plan).

Click **Save changes** — the service auto-redeploys.

## 6. First-run sanity check

1. Open `https://csiph-attendance.onrender.com`.
2. Log in as `admin` / `Admin123!`.
3. Immediately go to **Users → admin → Edit / change password** and pick a
   strong one (the seeded credentials are public knowledge from this repo).
4. Approve any pending registrations.
5. Test the email pipeline from **Diagnostics → /Diagnostics/SendTestEmail
   ?to=you@example.com**.

## 7. Optional — custom domain (`csi-attendance.is-a.dev`)

Once `csiph-attendance.onrender.com` is live:

1. Fork <https://github.com/is-a-dev/register>.
2. Add `domains/csi-attendance.json`:
   ```json
   {
     "owner": { "username": "csiattendancemonitor-dot", "email": "csi.attendance.monitor@gmail.com" },
     "record": { "CNAME": "csiph-attendance.onrender.com" }
   }
   ```
3. Open a PR. Once merged, add the custom domain in Render's dashboard
   (Service → **Settings → Custom Domains**).

---

## Notes

- The free Render plan **sleeps after 15 minutes** of no traffic and takes
  ~30 s to wake. Use a free uptime monitor like UptimeRobot if you need it
  always-on.
- The persistent disk holds your SQLite database. Don't delete the service
  unless you've backed it up.
- If you outgrow SQLite, switch the `ConnectionStrings__DefaultConnection`
  env var to a Postgres URL and update `Program.cs` to use Npgsql.

---

## Backup-first Postgres migration (Render expired DB)

If your free Render Postgres is nearing deletion, run a full backup first,
verify it, and only then restore into a new free Postgres (for example, Neon
or Supabase).

### Prerequisites

1. Install PostgreSQL client tools (must include `pg_dump`, `pg_restore`,
    `psql`).
2. Collect two full connection URLs:
    - old/source DB URL (Render)
    - new/target DB URL (Neon/Supabase)

### 1. Create and verify backup only (safe, no restore)

```powershell
cd C:\Code\Attendance_Monitoring

# Safer: keep secrets out of command history.
$env:SOURCE_DATABASE_URL = "<OLD_RENDER_DB_URL>"

.\tools\backup_and_migrate_postgres.ps1
```

This creates:
- a custom dump (`.dump`)
- a plain SQL file (`.sql`)
- SHA256 checksums (`.sha256.txt`)

### 2. Restore to new DB (only after backup exists)

```powershell
cd C:\Code\Attendance_Monitoring

$env:SOURCE_DATABASE_URL = "<OLD_RENDER_DB_URL>"
$env:TARGET_DATABASE_URL = "<NEW_DB_URL>"

.\tools\backup_and_migrate_postgres.ps1 \
   -RestoreToTarget \
   -Force
```

Safety behavior:
- script refuses restore without `-Force`
- script always runs backup + verification before restore
- target `public` schema is reset before restore

### 3. Point app to new DB

In Render service environment variables, set:

`ConnectionStrings__DefaultConnection=<NEW_DB_URL>`

Then redeploy and test login + attendance history screens.

---

## IIS deployment on Dev Box (Windows)

Use this when you want the app reachable from your Dev Box over HTTP using
IIS as the front end.

### Prerequisites (one-time)

1. Install .NET 8 Hosting Bundle on the Dev Box:
    - <https://dotnet.microsoft.com/en-us/download/dotnet/8.0>
    - Choose **ASP.NET Core Runtime Hosting Bundle**.
1. Enable IIS + Management Console (if not already enabled):

```powershell
Enable-WindowsOptionalFeature -Online -FeatureName IIS-WebServerRole -All
Enable-WindowsOptionalFeature -Online -FeatureName IIS-ManagementConsole -All
```

1. Open **elevated PowerShell** (Run as Administrator).

### One-command deploy

From repo root:

```powershell
cd C:\Code\Attendance_Monitoring

.\tools\deploy_iis_devbox.ps1 \
   -SiteName "AttendanceMonitoring-DevBox" \
   -AppPoolName "AttendanceMonitoring-DevBox" \
   -PhysicalPath "C:\inetpub\AttendanceMonitoring" \
   -SourceDbPath "C:\Code\Attendance_Monitoring\attendance.db" \
   -Port 8080 \
   -EnvironmentName "Production"
```

Then browse:

`http://localhost:8080/`

### Optional parameters

- Set secure cookie requirement:

```powershell
.\tools\deploy_iis_devbox.ps1 -RequireSecureCookie
```

- Override DB connection string (written as web.config env var):

```powershell
.\tools\deploy_iis_devbox.ps1 \
   -ConnectionString "Data Source=C:\inetpub\AttendanceMonitoring\attendance.db"
```

- Reconfigure host header binding:

```powershell
.\tools\deploy_iis_devbox.ps1 -Port 80 -HostHeader "attendance.devbox.local"
```

- Skip DB migration (deploy code only):

```powershell
.\tools\deploy_iis_devbox.ps1 -SkipDataMigration
```

### Validate deployment

```powershell
Import-Module WebAdministration
Get-Website -Name "AttendanceMonitoring-DevBox"
Get-WebAppPoolState -Name "AttendanceMonitoring-DevBox"
```

If the site fails to start, check:

- Windows Event Viewer  Application log
- IIS logs under `C:\inetpub\logs\LogFiles`
- App files and generated `web.config` in `C:\inetpub\AttendanceMonitoring`
