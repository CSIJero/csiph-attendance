using System;
using System.ComponentModel.DataAnnotations;

namespace AttendanceMonitoring.Models
{
    /// <summary>
    /// Audit log for all attendance edits (manual/admin/user).
    /// </summary>
    public class AttendanceAuditLog
    {
        public int Id { get; set; }
        public int AttendanceId { get; set; }
        public int EditedByUserId { get; set; }
        public DateTime EditedAt { get; set; } = DateTime.UtcNow;
        [MaxLength(32)]
        public string Action { get; set; } = string.Empty; // e.g. "Edit", "ManualEntry", "AdminOverride"
        [MaxLength(500)]
        public string? Reason { get; set; }
        [MaxLength(1000)]
        public string? ChangeSummary { get; set; } // e.g. "CheckIn changed from ... to ..."
    }
}
