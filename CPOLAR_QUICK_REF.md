# Cpolar Deployment — Quick Reference

## Pre-Deployment Checklist

- [ ] GitHub repository created and code pushed
- [ ] Cpolar account created and GitHub connected
- [ ] Brevo account created (for email) — [https://www.brevo.com](https://www.brevo.com)
- [ ] Brevo API key copied from [https://app.brevo.com/settings/keys/api](https://app.brevo.com/settings/keys/api)
- [ ] Database ready (PostgreSQL connection string or SQLite path decided)

---

## 5-Minute Deployment Steps

1. **Login to Cpolar** → [https://cpolar.com](https://cpolar.com)
2. **Create New Deployment**
   - Select **Docker** runtime
   - Point to your GitHub repository (`YOUR_USERNAME/csiph-attendance`)
   - Branch: `main`
   - Dockerfile: (default)
3. **Set Environment Variables** (copy from `.env.cpolar`, update placeholders)
4. **Deploy** and wait ~5 min for build
5. **Test login** at your Cpolar URL (default credentials: `admin` / `Admin123!`)
6. **Change admin password** immediately

---

## Common Environment Variable Mistakes

| Mistake | Fix |
|--------|-----|
| `ASPNETCORE_URLS` not set | Must be `http://0.0.0.0:10000` |
| Wrong connection string | For PostgreSQL, must start with `postgres://` |
| SQLite path is wrong | Use `/data/attendance.db` or `/tmp/attendance.db` |
| Brevo key is SMTP password | Use the API key (long `xkeysib-...` string) instead |
| Email not working, but key looks right | Test via **Diagnostics → Send Test Email** |

---

## Default Credentials (CHANGE IMMEDIATELY AFTER FIRST LOGIN)

```
Username: admin
Password: Admin123!
```

**⚠️ These are public. Change them before giving anyone access.**

---

## Database Quick Pick

| Use Case | Choice | Notes |
|----------|--------|-------|
| **Testing / Demo** | SQLite | Simple, no setup, not suitable for production |
| **Production** | PostgreSQL | Scalable, supports concurrent users, requires external service |

For PostgreSQL, try:
- AWS RDS (managed, pay-as-you-go)
- Railway.app (free tier available)
- DigitalOcean (managed PostgreSQL)
- Your own PostgreSQL server

---

## Common Errors & Fixes

### "Container won't start"
1. Check **Logs** tab in Cpolar dashboard
2. Verify `ASPNETCORE_URLS=http://0.0.0.0:10000`
3. Verify `ConnectionStrings__DefaultConnection` is not empty

### "Bad Gateway" / "502 error"
- App likely crashed → check **Logs** tab
- Restart the deployment

### "Cannot connect to database"
- Test connection string (for PostgreSQL, use a tool like `psql` or PgAdmin from your machine)
- For SQLite, ensure path is writable (use `/data/attendance.db`)

### "Emails not sending"
1. Go to **Diagnostics** → **Send Test Email**
2. If it fails, check error logs
3. Verify `Email__Username` and `Email__Password` are your Brevo API key (not SMTP password)
4. Ensure `Email__Provider=Brevo` is set

### "Login page loads but can't log in"
- Try default credentials: `admin` / `Admin123!`
- If that fails, the database may not have initialized
- Check **Logs** for `DbInitializer` messages

---

## Post-Deployment Checklist

- [ ] App loads at your Cpolar URL
- [ ] Can log in with default credentials
- [ ] Admin password changed to a strong one
- [ ] Email test sent successfully (via Diagnostics)
- [ ] User registrations approved (Users tab)

---

## Useful Links

| Resource | URL |
|----------|-----|
| Cpolar Docs | [https://cpolar.com/docs](https://cpolar.com/docs) |
| Brevo API Docs | [https://developers.brevo.com](https://developers.brevo.com) |
| ASP.NET Core Docs | [https://learn.microsoft.com/en-us/aspnet/core](https://learn.microsoft.com/en-us/aspnet/core) |
| Docker Docs | [https://docs.docker.com](https://docs.docker.com) |

---

## Deployment URL Format

Once deployed, your app will be available at:
```
https://<your-deployment-name>.cpolar.io
```

Or with a custom domain (if configured):
```
https://your-custom-domain.com
```

---

## Rolling Back

If deployment breaks:
1. Cpolar dashboard → **Versions** (or **Deployments**)
2. Select a previous stable version
3. Click **Rollback**

---

## Support

For issues:
- **Cpolar Support**: [https://cpolar.com/support](https://cpolar.com/support)
- **Brevo Support**: [https://www.brevo.com/support](https://www.brevo.com/support)
- **App Logs**: Check Cpolar dashboard **Logs** tab

---

## Example: Full Environment Variables for Cpolar

```
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://0.0.0.0:10000
ConnectionStrings__DefaultConnection=postgres://user:pass@db.example.com:5432/attendance
Email__Provider=Brevo
Email__Enabled=true
Email__FromAddress=monitor@example.com
Email__FromName=Attendance
Email__Username=xkeysib-abc123...
Email__Password=xkeysib-abc123...
AttendanceMonitoring__RequireSecureCookie=true
AttendanceMonitoring__OnlineThresholdSeconds=600
AttendanceMonitoring__SessionLifetimeHours=12
```

---

**Questions or issues?** Check the full guide in `CPOLAR_DEPLOY.md`.
