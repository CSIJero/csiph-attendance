# CPOLAR_CHECKLIST.md - Deployment Verification Checklist

## Phase 1: Pre-Deployment (Local Prep)

- [ ] Clone/ensure project is in `c:\Code\csiph-attendance-main`
- [ ] Run `prepare_cpolar.ps1` to verify setup:
  ```powershell
  .\prepare_cpolar.ps1
  ```
- [ ] Verify Git is initialized and configured:
  ```powershell
  git status
  git config --get remote.origin.url
  ```
- [ ] Verify no `attendance.db` or secrets are committed:
  ```powershell
  git status
  ```

---

## Phase 2: GitHub & Cpolar Account Setup

- [ ] **GitHub**:
  - [ ] Repository created: `https://github.com/YOUR_USERNAME/csiph-attendance`
  - [ ] Code pushed to `main` branch:
    ```powershell
    git push -u origin main
    ```

- [ ] **Cpolar Account**:
  - [ ] Sign up at [https://cpolar.com](https://cpolar.com)
  - [ ] Connect GitHub account (authorize Cpolar app)
  - [ ] GitHub app installed on your repository

- [ ] **Brevo Account** (for emails):
  - [ ] Sign up at [https://www.brevo.com](https://www.brevo.com) (free tier: 300 emails/day)
  - [ ] API key copied from [https://app.brevo.com/settings/keys/api](https://app.brevo.com/settings/keys/api)
  - [ ] Key starts with `xkeysib-` (NOT an SMTP password)

- [ ] **Database** (pick one):
  - [ ] **Option A - SQLite**: Use for testing only
    ```
    ConnectionStrings__DefaultConnection=Data Source=/data/attendance.db
    ```
  - [ ] **Option B - PostgreSQL**: Set up managed service
    - [ ] AWS RDS / Railway.app / DigitalOcean PostgreSQL created
    - [ ] Connection string format verified: `postgres://user:pass@host:port/db`

---

## Phase 3: Cpolar Deployment Creation

### In Cpolar Dashboard:

- [ ] Click **New Deployment** or **Create Service**

- [ ] **Basic Configuration**:
  - [ ] Runtime: `Docker`
  - [ ] Repository: `YOUR_USERNAME/csiph-attendance`
  - [ ] Branch: `main`
  - [ ] Dockerfile path: `./Dockerfile` (default)
  - [ ] Port: `10000`

- [ ] **Build & Deployment**:
  - [ ] Auto-deploy on push: **Enabled**
  - [ ] Health check path: `/Account/Login`

- [ ] **Environment Variables** (set ALL of these):
  ```
  ASPNETCORE_ENVIRONMENT = Production
  ASPNETCORE_URLS = http://0.0.0.0:10000
  ConnectionStrings__DefaultConnection = postgres://user:pass@host:5432/db
  AttendanceMonitoring__RequireSecureCookie = true
  AttendanceMonitoring__OnlineThresholdSeconds = 60
  AttendanceMonitoring__SessionLifetimeHours = 12
  Email__Provider = Brevo
  Email__Enabled = true
  Email__FromAddress = your-email@example.com
  Email__FromName = Attendance Monitor
  Email__Username = xkeysib-your-api-key
  Email__Password = xkeysib-your-api-key
  DOTNET_NOLOGO = true
  DOTNET_PRINT_TELEMETRY_MESSAGE = false
  ```

- [ ] Click **Deploy** (wait 5-10 minutes for Docker build)

---

## Phase 4: Immediate Post-Deployment Tests

### Within 15 minutes of deployment:

- [ ] **Check Cpolar Dashboard**:
  - [ ] Deployment status: **Active** or **Running**
  - [ ] No errors in **Logs** tab

- [ ] **Test the app**:
  - [ ] Open the Cpolar-provided URL (e.g., `https://abc123.cpolar.io`)
  - [ ] Page loads without errors
  - [ ] Login page visible

- [ ] **Test default credentials**:
  - [ ] Username: `admin`
  - [ ] Password: `Admin123!`
  - [ ] Login successful ✓

- [ ] **Change admin password IMMEDIATELY**:
  - [ ] Navigate to **Users** → **admin** → **Edit**
  - [ ] Set a strong new password
  - [ ] Save and log back in with new password

---

## Phase 5: Configuration & Feature Testing

### In the running app:

- [ ] **Users** page:
  - [ ] Check for pending user registrations
  - [ ] Approve/reject as needed

- [ ] **Diagnostics** page (`/Diagnostics`):
  - [ ] Click **Send Test Email**
  - [ ] Check your inbox for test email (may take 1-2 minutes)
  - [ ] If email fails, verify Brevo API key

- [ ] **Settings** page (if applicable):
  - [ ] Verify application settings
  - [ ] Test any online/offline detection features

---

## Phase 6: Ongoing Monitoring

### First 24 hours:

- [ ] Monitor Cpolar dashboard **Logs** for errors
- [ ] Test login from different browsers/devices
- [ ] Test key features (attendance check-in, reports, etc.)
- [ ] Verify no database connection issues

### Ongoing:

- [ ] Set up monitoring/alerts (Cpolar dashboard or external)
- [ ] Enable auto-restart on failure (Cpolar settings)
- [ ] Schedule weekly backups (if using PostgreSQL)
- [ ] Monitor email delivery rates

---

## Troubleshooting During Deployment

### Deployment fails to start:

1. Check **Logs** in Cpolar dashboard
2. Common issues:
   - [ ] Missing `ASPNETCORE_URLS` environment variable
   - [ ] Typo in connection string
   - [ ] PostgreSQL server not accessible

3. To retry:
   - [ ] Fix the issue in Environment Variables
   - [ ] Click **Restart** or **Redeploy**

### Login page loads but can't log in:

1. Check that database initialized:
   - [ ] Logs should show "DbInitializer: Seeding initial user (admin)"
2. Try default credentials: `admin` / `Admin123!`
3. If still fails:
   - [ ] Check database connection string
   - [ ] Restart deployment

### Emails not sending:

1. Test via **Diagnostics → Send Test Email**
2. Check error message
3. Common issues:
   - [ ] Brevo API key is incorrect
   - [ ] Key is SMTP password instead of API key
   - [ ] `Email__Provider` is not set to `Brevo`

---

## Post-Deployment Customization (Optional)

- [ ] **Custom domain**:
  - [ ] In Cpolar dashboard → **Settings** → **Custom Domains**
  - [ ] Add your domain and update DNS records

- [ ] **Auto-restart on failure**:
  - [ ] Cpolar dashboard → **Settings** → enable auto-restart

- [ ] **Environment-specific tweaks**:
  - [ ] Adjust `OnlineThresholdSeconds` if needed
  - [ ] Adjust `SessionLifetimeHours` if needed

---

## Rollback Plan

If deployment breaks:

1. **Quick rollback**:
   - [ ] Cpolar dashboard → **Versions** / **Deployments**
   - [ ] Select last known-good version
   - [ ] Click **Rollback**

2. **If rollback fails**:
   - [ ] Delete deployment and create new one from same branch

---

## Sign-Off

- [ ] Deployment is live and accessible
- [ ] Admin password changed from default
- [ ] Email is working
- [ ] Users can register and log in
- [ ] All key features tested

**Deployed by**: ________________  
**Date**: ________________  
**Cpolar URL**: ________________  
**Status**: ✓ LIVE

---

## Quick Reference Documents

| Document | Purpose |
|----------|---------|
| `CPOLAR_DEPLOY.md` | Full deployment guide with detailed instructions |
| `CPOLAR_QUICK_REF.md` | Quick reference & common errors |
| `.env.cpolar` | Environment variables template |
| `prepare_cpolar.ps1` | Pre-deployment verification script |
| `CPOLAR_CHECKLIST.md` | This file - deployment verification |

---

## Support & Resources

| Resource | URL |
|----------|-----|
| Cpolar Support | https://cpolar.com/support |
| Brevo Docs | https://developers.brevo.com |
| ASP.NET Core Docs | https://learn.microsoft.com/en-us/aspnet/core |
| App Logs | Check Cpolar dashboard → Logs tab |

---

**Good luck with your deployment! 🚀**
