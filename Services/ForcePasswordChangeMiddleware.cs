using System.Security.Claims;
using AttendanceMonitoring.Data;
using Microsoft.EntityFrameworkCore;

namespace AttendanceMonitoring.Services;

/// <summary>
/// When the signed-in user has <c>MustChangePassword=true</c>, redirect every
/// request (other than the change-password page itself, logout, and static
/// assets) to <c>/Account/ChangePassword</c>. The user can't escape the
/// prompt until they pick a new password.
/// </summary>
public class ForcePasswordChangeMiddleware
{
    private readonly RequestDelegate _next;

    public ForcePasswordChangeMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx, AppDbContext db)
    {
        // Anonymous traffic, the change-password endpoints themselves, the
        // logout endpoint and lightweight pings are always allowed through.
        var path = ctx.Request.Path.Value ?? string.Empty;
        if (ctx.User?.Identity?.IsAuthenticated != true
            || path.StartsWith("/Account/ChangePassword", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/Account/Logout", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            await _next(ctx);
            return;
        }

        var raw = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(raw, out var userId))
        {
            await _next(ctx);
            return;
        }

        var mustChange = await db.Users
            .Where(u => u.Id == userId)
            .Select(u => (bool?)u.MustChangePassword)
            .FirstOrDefaultAsync();

        if (mustChange == true)
        {
            ctx.Response.Redirect("/Account/ChangePassword");
            return;
        }

        await _next(ctx);
    }
}
