using AttendanceMonitoring.Data;
using AttendanceMonitoring.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

// One-shot CLI mode: copy a local SQLite attendance.db into a remote Postgres
// (Render free tier). Invoked with `dotnet run -- migrate-sqlite --sqlite ...
// --pg ...`. Returns early so the web host is never started.
if (args.Length >= 1 &&
    string.Equals(args[0], "migrate-sqlite", StringComparison.OrdinalIgnoreCase))
{
    return await AttendanceMonitoring.Tools.SqliteToPostgres.RunAsync(args.Skip(1).ToArray());
}

var builder = WebApplication.CreateBuilder(args);

// ----------------------------------------------------------------------------
// Services
// ----------------------------------------------------------------------------
builder.Services.AddControllersWithViews();

builder.Services.AddDbContext<AppDbContext>(options =>
{
    var raw = builder.Configuration.GetConnectionString("DefaultConnection")
              ?? "Data Source=attendance.db";

    // Render exposes Postgres as a URL ("postgres://user:pass@host:port/db").
    // Detect that form, convert to Npgsql keyword syntax, and switch
    // providers. Anything else (the local "Data Source=..." form) keeps the
    // existing SQLite behavior so dev still works out of the box.
    if (raw.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
        raw.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
    {
        var uri = new Uri(raw);
        var userInfo = uri.UserInfo.Split(':', 2);
        var npgsql =
            $"Host={uri.Host};" +
            $"Port={(uri.Port > 0 ? uri.Port : 5432)};" +
            $"Database={uri.AbsolutePath.TrimStart('/')};" +
            $"Username={Uri.UnescapeDataString(userInfo[0])};" +
            $"Password={Uri.UnescapeDataString(userInfo.Length > 1 ? userInfo[1] : string.Empty)};" +
            "SSL Mode=Require;Trust Server Certificate=true;";
        options.UseNpgsql(npgsql);
    }
    else
    {
        options.UseSqlite(raw);
    }
});

var requireSecureCookie = builder.Configuration
    .GetValue<bool>("AttendanceMonitoring:RequireSecureCookie");
var sessionLifetimeHours = builder.Configuration
    .GetValue("AttendanceMonitoring:SessionLifetimeHours", 12);

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "AttendanceMonitoring.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = requireSecureCookie
            ? CookieSecurePolicy.Always
            : CookieSecurePolicy.SameAsRequest;
        options.LoginPath = "/Account/Login";
        options.LogoutPath = "/Account/Logout";
        options.AccessDeniedPath = "/Home/Forbidden";
        options.ExpireTimeSpan = TimeSpan.FromHours(sessionLifetimeHours);
        options.SlidingExpiration = true;
    });

// Persist the data-protection key ring in the application DB. Render's
// free tier rebuilds the container on every deploy, which would otherwise
// wipe the in-memory key ring and silently log every user out. Storing
// keys alongside the rest of the app data keeps sign-in cookies valid
// across deploys (and across replicas, should we ever scale out).
builder.Services
    .AddDataProtection()
    .SetApplicationName("AttendanceMonitoring")
    .PersistKeysToDbContext<AppDbContext>();

// Antiforgery cookie hardening. Token cookie has to follow the same Secure
// policy as the auth cookie or it gets stripped on cross-site requests in
// modern browsers (SameSite=Strict + Secure is the safe pair in prod).
builder.Services.AddAntiforgery(opts =>
{
    opts.Cookie.HttpOnly = true;
    opts.Cookie.SameSite = SameSiteMode.Lax;
    opts.Cookie.SecurePolicy = requireSecureCookie
        ? CookieSecurePolicy.Always
        : CookieSecurePolicy.SameAsRequest;
});

// Render terminates TLS at the edge and forwards plain HTTP with
// X-Forwarded-Proto / X-Forwarded-For. Without this middleware the app
// sees the request as HTTP and (a) Request.IsHttps returns false so any
// "Secure" cookie policy with SameAsRequest gets stripped, and (b)
// ClientInfo logs the proxy's IP instead of the real client. Clearing
// KnownNetworks / KnownProxies opts us into "trust whatever forwarded
// headers arrive" — safe here because the only public entry point is
// the Render edge.
builder.Services.Configure<ForwardedHeadersOptions>(opts =>
{
    opts.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    opts.KnownNetworks.Clear();
    opts.KnownProxies.Clear();
});

builder.Services.AddAuthorization(options =>
{
    // "admin", "program_manager" and "pm" (Project Manager) all have
    // administrative access. Visibility scope differs per role and is
    // enforced inside controllers via AppController.GetVisibleUsersAsync.
    options.AddPolicy("AdminOnly", policy => policy.RequireRole(
        AttendanceMonitoring.Models.Roles.Admin,
        AttendanceMonitoring.Models.Roles.ProgramManager,
        AttendanceMonitoring.Models.Roles.Pm));
});

// HttpContextAccessor so views can resolve the current user without ceremony.
builder.Services.AddHttpContextAccessor();

// Email + offline-during-shift notifier.
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("Email"));
builder.Services.Configure<NotifierOptions>(builder.Configuration.GetSection("Notifier"));

// Pick the email backend based on Email:Provider in appsettings.json.
//   "Brevo"   - Brevo transactional HTTP API (works on Render free tier
//               where outbound SMTP ports are blocked).
//   "Outlook" - automate the locally-installed Outlook desktop client.
//   "Smtp"    - traditional SMTP (default, used when key is missing).
var emailProvider = builder.Configuration.GetValue<string>("Email:Provider") ?? "Smtp";
if (string.Equals(emailProvider, "Brevo", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddHttpClient();
    builder.Services.AddSingleton<IEmailSender, BrevoEmailSender>();
}
else if (string.Equals(emailProvider, "Outlook", StringComparison.OrdinalIgnoreCase)
    && OperatingSystem.IsWindows())
{
    builder.Services.AddSingleton<IEmailSender, OutlookInteropEmailSender>();
}
else
{
    builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
}

builder.Services.AddHostedService<OfflineNotifierService>();
builder.Services.AddHostedService<AttendanceMonitoring.Services.LeaveAccrualService>();

var app = builder.Build();

// Wire the static UserClock helper to the HttpContextAccessor so that
// formatters everywhere can read the viewer's browser timezone (set by
// a small client-side bootstrap on every page) without each call site
// having to thread an HttpContext through.
UserClock.Accessor = app.Services.GetRequiredService<IHttpContextAccessor>();

// ----------------------------------------------------------------------------
// Pipeline
// ----------------------------------------------------------------------------

// ForwardedHeaders MUST run before anything that inspects scheme / IP
// (auth, antiforgery, ClientInfo, HSTS). Render's edge sends
// X-Forwarded-Proto=https and we want Request.IsHttps to reflect that.
app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // HSTS — tells browsers to refuse plain HTTP for this host for the
    // next year. Safe once the site is exclusively served over TLS
    // (Render's free tier already enforces HTTPS at the edge).
    app.UseHsts();
}

// Lightweight static security headers. Kept conservative — no CSP yet,
// since the admin pages embed inline <script>/<style> blocks that a
// strict CSP would break. The headers below are universally safe.
app.Use(async (ctx, next) =>
{
    var headers = ctx.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    if (!headers.ContainsKey("X-Frame-Options"))
    {
        headers["X-Frame-Options"] = "SAMEORIGIN";
    }
    await next();
});

app.UseStaticFiles();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

// Lightweight request logger so we can SEE in Render logs whether a
// POST actually reached the origin or got rejected upstream. Logs only
// requests under /api/ and /schedule to keep noise down. Routed through
// ILogger so production environments can mute it via Logging:LogLevel.
{
    var reqLogger = app.Services
        .GetRequiredService<ILoggerFactory>()
        .CreateLogger("RequestTrace");
    app.Use(async (ctx, next) =>
    {
        var p = ctx.Request.Path.Value ?? "";
        var logIt = p.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
                 || p.StartsWith("/schedule", StringComparison.OrdinalIgnoreCase);
        if (logIt && reqLogger.IsEnabled(LogLevel.Debug))
        {
            reqLogger.LogDebug(
                "[REQ] {Method} {Path}{Query} ct={ContentType} auth={IsAuth}",
                ctx.Request.Method, p, ctx.Request.QueryString,
                ctx.Request.ContentType, ctx.User?.Identity?.IsAuthenticated);
        }
        await next();
        if (logIt && reqLogger.IsEnabled(LogLevel.Debug))
        {
            reqLogger.LogDebug(
                "[RES] {Method} {Path} -> {Status}",
                ctx.Request.Method, p, ctx.Response.StatusCode);
        }
    });
}

// After auth runs we know who the user is. If an admin has flipped
// MustChangePassword on them, force-redirect every page to the
// change-password form until they pick a new one.
app.UseMiddleware<AttendanceMonitoring.Services.ForcePasswordChangeMiddleware>();

app.UseStatusCodePagesWithReExecute("/Home/Status/{0}");

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// ----------------------------------------------------------------------------
// Database init / seed
// ----------------------------------------------------------------------------
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
    await DbInitializer.InitializeAsync(db);
    await RuntimeConfig.LoadFromDbAsync(db, config);
}

app.Run();
return 0;
