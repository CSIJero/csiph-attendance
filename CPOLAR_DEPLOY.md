# Cpolar Deployment Guide

This guide explains how to deploy the **Attendance Monitoring** ASP.NET Core 8.0 application to **Cpolar**.

---

## Prerequisites

1. **Cpolar Account** – Sign up at [cpolar.com](https://cpolar.com)
2. **GitHub Repository** – This project pushed to GitHub (see DEPLOY.md steps 1–3)
3. **Docker** – Cpolar can build and deploy from Docker; the `Dockerfile` is already configured
4. **PostgreSQL (optional)** – For production; SQLite works for testing

---

## Deployment Steps

### 1. Prepare Your GitHub Repository

If not already done, push your code to GitHub:

```powershell
cd C:\Code\csiph-attendance-main
git init
git branch -M main
git add -A
git commit -m "Initial commit: attendance monitoring app"
git remote add origin https://github.com/YOUR_USERNAME/csiph-attendance.git
git push -u origin main
```

### 2. Create a Cpolar Account & Connect GitHub

1. Go to [cpolar.com](https://cpolar.com) and sign up.
2. Connect your GitHub account (Cpolar will ask for authorization).
3. Install the **Cpolar GitHub App** on your repository.

### 3. Create a New Deployment on Cpolar

#### Option A: Docker Deployment (Recommended)

1. In the Cpolar dashboard, click **New Deployment** or **New Service**.
2. Select **Docker** as the runtime.
3. Configure:
   - **Repository**: `YOUR_USERNAME/csiph-attendance`
   - **Branch**: `main`
   - **Dockerfile**: (use default `./Dockerfile` — already present)
   - **Port**: `10000` (exposed in the Dockerfile)
   - **Auto Deploy**: Enable (redeploys on push)

4. Click **Deploy**.

#### Option B: Direct .NET Runtime (if available)

1. Select **.NET** runtime.
2. **Startup Command**: `dotnet AttendanceMonitoring.dll`
3. Configure environment variables (see below).

---

### 4. Configure Environment Variables

Once deployment is created, go to **Environment Variables** and set:

| Variable                              | Value                                      | Notes                                      |
| ------------------------------------- | ------------------------------------------ | ------------------------------------------ |
| `ASPNETCORE_ENVIRONMENT`              | `Production`                               | Required                                   |
| `ASPNETCORE_URLS`                     | `http://0.0.0.0:10000`                     | Required (must bind to 0.0.0.0)            |
| `ConnectionStrings__DefaultConnection` | (see Database Setup below)                 | SQLite or PostgreSQL connection string     |
| `AttendanceMonitoring__RequireSecureCookie` | `true`                                | Enforces HTTPS cookies                     |
| `AttendanceMonitoring__OnlineThresholdSeconds` | `600`                           | 10 minutes (adjust as needed)               |
| `AttendanceMonitoring__SessionLifetimeHours` | `12`                              | 12-hour sessions (adjust as needed)        |
| `Email__Provider`                     | `Brevo`                                    | HTTP API (no SMTP block issues)             |
| `Email__Enabled`                      | `true`                                     | Enable email functionality                 |
| `Email__FromAddress`                  | `your-email@gmail.com`                     | Sender address                             |
| `Email__FromName`                     | `Attendance Monitor`                       | Display name                               |
| `Email__Username`                     | `your-brevo-api-key`                       | Get from [app.brevo.com](https://app.brevo.com/settings/keys/api) |
| `Email__Password`                     | `your-brevo-api-key`                       | Same as `Email__Username` for Brevo        |

---

### 5. Database Setup

#### Option A: SQLite (Testing/Demo)

Use the default SQLite file:

```
ConnectionStrings__DefaultConnection = "Data Source=/data/attendance.db"
```

⚠️ **Note**: SQLite is not suitable for production or concurrent users. The file will be local to the container.

#### Option B: PostgreSQL (Recommended for Production)

1. **Set up PostgreSQL** (on your own server, managed service like AWS RDS, or Cpolar-managed if available).
2. **Get the connection string** in the format:
   ```
   postgres://username:password@host:port/database
   ```

3. **Set the environment variable**:
   ```
   ConnectionStrings__DefaultConnection = "postgres://user:pass@pg.example.com:5432/attendance_db"
   ```

4. **The app will auto-detect** the `postgres://` prefix and switch to Npgsql (see `Program.cs`).

---

### 6. Enable HTTPS & Custom Domain (Optional)

1. **Cpolar-provided domain**: After deployment, Cpolar provides a public HTTPS URL (e.g., `https://abc123.cpolar.io`).
2. **Custom domain**: In Cpolar dashboard → **Settings** → **Custom Domains**, add your own domain and update DNS records.

---

### 7. First-Time Setup

Once the deployment is live:

1. **Open the app**: Go to your Cpolar-provided URL.
2. **Log in**: Use the default credentials:
   - **Username**: `admin`
   - **Password**: `Admin123!`

3. **⚠️ Change the admin password immediately**:
   - Navigate to **Users** → **admin** → **Edit**.
   - Set a strong password.

4. **Test email**:
   - Go to **Diagnostics** → test email functionality.
   - Verify Brevo credentials if emails fail.

5. **Approve pending registrations**:
   - Check **Users** for any pending user registrations.

---

### 8. Monitoring & Logs

- **Cpolar Dashboard**: View real-time logs, deployment status, and resource usage.
- **Error tracking**: Check the **Logs** tab if the app fails to start.
- **Common issues**:
  - Port binding errors → ensure `ASPNETCORE_URLS=http://0.0.0.0:10000`
  - Database connection errors → verify `ConnectionStrings__DefaultConnection`
  - Email failures → test Brevo API key in **Diagnostics**

---

### 9. Auto-Restart & Health Checks

- **Health Check Path**: Configure to `/Account/Login` (public endpoint that doesn't require authentication).
- **Auto-restart**: Cpolar can restart failed deployments; enable in settings.

---

## Troubleshooting

### "Port already in use" error
- Ensure `ASPNETCORE_URLS=http://0.0.0.0:10000` is set.
- Cpolar routes external traffic to port 10000 inside the container.

### Database connection errors
- Verify the PostgreSQL connection string or SQLite path.
- For PostgreSQL, ensure the database and user exist.
- For SQLite, ensure the `/data/` or `/tmp/` directory is writable.

### Emails not sending
- Confirm Brevo API key is correct (get from [app.brevo.com](https://app.brevo.com/settings/keys/api)).
- Test via **Diagnostics** → **Send Test Email**.

### Cold start is slow
- First request after idle may take 30–60 seconds (Docker container startup).
- Subsequent requests are fast.

---

## Rolling Back a Deployment

If a deployment breaks:

1. In Cpolar dashboard, go to **Deployments** or **Versions**.
2. Select a previous stable version.
3. Click **Rollback**.

---

## Next Steps

- Set up monitoring/alerting (Cpolar dashboard or third-party tools).
- Enable automatic daily backups if using PostgreSQL.
- Document your Cpolar deployment URL for team access.
- Configure custom domain (optional but recommended).

---

## Additional Resources

- [Cpolar Documentation](https://cpolar.com/docs)
- [Brevo Email API](https://developers.brevo.com)
- [ASP.NET Core Deployment](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy)
