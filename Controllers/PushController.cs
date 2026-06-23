using AttendanceMonitoring.Data;
using AttendanceMonitoring.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AttendanceMonitoring.Controllers;

/// <summary>
/// Web Push subscription registry. The browser side calls
/// <c>navigator.serviceWorker.ready.then(r =&gt; r.pushManager.subscribe(…))</c>
/// and POSTs the resulting <see cref="PushSubscriptionPayload"/> here.
/// We persist it so a future server-side broadcaster (VAPID-signed
/// notifications) can target the right endpoints.
/// </summary>
[ApiController]
[Authorize]
[Route("api/push")]
public class PushController : AppController
{
    public PushController(AppDbContext db) : base(db) { }

    /// <summary>
    /// Returns the configured VAPID public key (base64-url) so the
    /// browser can include it in <c>pushManager.subscribe</c>. Empty
    /// string when push is not configured yet.
    /// </summary>
    [HttpGet("vapid-public-key")]
    [AllowAnonymous]
    public IActionResult VapidPublicKey([FromServices] IConfiguration cfg)
    {
        var key = cfg["Push:VapidPublicKey"] ?? string.Empty;
        return Ok(new { key });
    }

    [HttpPost("subscribe")]
    public async Task<IActionResult> Subscribe([FromBody] PushSubscriptionPayload payload)
    {
        var meId = CurrentUserId;
        if (meId is null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(payload.Endpoint)
            || string.IsNullOrWhiteSpace(payload.Keys?.P256dh)
            || string.IsNullOrWhiteSpace(payload.Keys?.Auth))
        {
            return BadRequest(new { error = "Missing endpoint or keys." });
        }

        var existing = await Db.PushSubscriptions
            .FirstOrDefaultAsync(p => p.UserId == meId.Value && p.Endpoint == payload.Endpoint);
        if (existing is null)
        {
            Db.PushSubscriptions.Add(new PushSubscription
            {
                UserId = meId.Value,
                Endpoint = payload.Endpoint!,
                P256dh = payload.Keys.P256dh!,
                Auth = payload.Keys.Auth!,
                UserAgent = Request.Headers.UserAgent.ToString(),
                CreatedAt = DateTime.UtcNow,
            });
        }
        else
        {
            existing.P256dh = payload.Keys.P256dh!;
            existing.Auth = payload.Keys.Auth!;
            existing.LastUsedAt = DateTime.UtcNow;
        }
        await Db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    [HttpPost("unsubscribe")]
    public async Task<IActionResult> Unsubscribe([FromBody] PushSubscriptionPayload payload)
    {
        var meId = CurrentUserId;
        if (meId is null) return Unauthorized();
        var row = await Db.PushSubscriptions
            .FirstOrDefaultAsync(p => p.UserId == meId.Value && p.Endpoint == payload.Endpoint);
        if (row is not null)
        {
            Db.PushSubscriptions.Remove(row);
            await Db.SaveChangesAsync();
        }
        return Ok(new { ok = true });
    }

    public class PushSubscriptionPayload
    {
        public string? Endpoint { get; set; }
        public PushKeys? Keys { get; set; }
    }

    public class PushKeys
    {
        public string? P256dh { get; set; }
        public string? Auth { get; set; }
    }
}
