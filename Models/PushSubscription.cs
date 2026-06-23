using System.ComponentModel.DataAnnotations;

namespace AttendanceMonitoring.Models;

/// <summary>
/// A Web Push subscription registered by a user's browser. The PWA
/// service worker calls <c>PushManager.subscribe()</c> and posts the
/// resulting endpoint + p256dh + auth keys to <c>/api/push/subscribe</c>.
/// Push notifications are sent server-side via the VAPID protocol;
/// the actual transmission is wired up in a future iteration but the
/// table is here so subscribers can be collected without breaking
/// changes later.
/// </summary>
public class PushSubscription
{
    public int Id { get; set; }

    public int UserId { get; set; }
    public User? User { get; set; }

    /// <summary>Unique browser endpoint URL produced by PushManager.subscribe().</summary>
    [Required, MaxLength(500)]
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Base64-URL public key from the subscription (p256dh).</summary>
    [Required, MaxLength(256)]
    public string P256dh { get; set; } = string.Empty;

    /// <summary>Base64-URL auth secret from the subscription.</summary>
    [Required, MaxLength(64)]
    public string Auth { get; set; } = string.Empty;

    /// <summary>Short user-agent label so admins can tell devices apart.</summary>
    [MaxLength(256)]
    public string? UserAgent { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
}
