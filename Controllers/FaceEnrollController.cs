using AttendanceMonitoring.Data;
using AttendanceMonitoring.Helpers;
using AttendanceMonitoring.Services;
using AttendanceMonitoring.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AttendanceMonitoring.Controllers;

/// <summary>
/// Self-service face enrollment. Users capture a single reference
/// selfie which is hashed via <see cref="FaceHash.Compute"/> and
/// stored on their account. Subsequent check-in selfies are compared
/// against this reference to flag potential proxy clock-ins.
/// </summary>
[Authorize]
[Route("profile/face")]
public class FaceEnrollController : AppController
{
    public FaceEnrollController(AttendanceMonitoring.Data.AppDbContext db) : base(db) { }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var me = await GetCurrentUserAsync();
        if (me is null) return Challenge();

        // Pull the most recent check-in selfies so the user can pick
        // one as the enrollment reference instead of capturing a fresh
        // photo. We trust any selfie that made it onto an attendance
        // row (passed the in-browser face-presence check at submit
        // time) so it's a reasonable seed.
        var recent = await Db.Attendances
            .Where(a => a.UserId == me.Id && a.CheckInPhoto != null && a.CheckInPhoto != "")
            .OrderByDescending(a => a.CheckIn)
            .Take(12)
            .Select(a => new FaceEnrollHistoryItem
            {
                AttendanceId = a.Id,
                CheckIn = a.CheckIn,
                WorkDate = a.WorkDate,
                FaceMatchStatus = a.FaceMatchStatus,
                Photo = a.CheckInPhoto!,
            })
            .ToListAsync();

        return View(new FaceEnrollVm
        {
            IsEnrolled = !string.IsNullOrEmpty(me.FaceHash),
            EnrolledAt = me.FaceEnrolledAt,
            RecentSelfies = recent,
        });
    }

    [HttpPost("enroll")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Enroll([FromForm] string? photo)
    {
        var me = await GetCurrentUserAsync();
        if (me is null) return Challenge();

        if (string.IsNullOrWhiteSpace(photo))
        {
            TempData.Flash("A photo is required to enroll.", "error");
            return RedirectToAction(nameof(Index));
        }

        var hash = FaceHash.Compute(photo);
        if (hash is null)
        {
            TempData.Flash(
                "Could not analyse the photo. Please retake in better lighting.",
                "error");
            return RedirectToAction(nameof(Index));
        }

        me.FaceHash = hash;
        me.FaceEnrolledAt = DateTime.UtcNow;
        await Db.SaveChangesAsync();
        TempData.Flash("Face profile updated.", "success");
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Enroll using the selfie from a previous attendance row. The
    /// row must belong to the current user — never trust the supplied
    /// ID without an ownership check.
    /// </summary>
    [HttpPost("enroll-from-history")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EnrollFromHistory([FromForm] int attendanceId)
    {
        var me = await GetCurrentUserAsync();
        if (me is null) return Challenge();

        var row = await Db.Attendances
            .FirstOrDefaultAsync(a => a.Id == attendanceId && a.UserId == me.Id);
        if (row is null || string.IsNullOrEmpty(row.CheckInPhoto))
        {
            TempData.Flash("That check-in row was not found or has no selfie.", "error");
            return RedirectToAction(nameof(Index));
        }

        var hash = FaceHash.Compute(row.CheckInPhoto);
        if (hash is null)
        {
            TempData.Flash(
                "Could not analyse that selfie. Please pick another or capture a new photo.",
                "error");
            return RedirectToAction(nameof(Index));
        }

        me.FaceHash = hash;
        me.FaceEnrolledAt = DateTime.UtcNow;
        await Db.SaveChangesAsync();
        TempData.Flash(
            $"Face profile enrolled from your {row.WorkDate:yyyy-MM-dd} check-in selfie.",
            "success");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("clear")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Clear()
    {
        var me = await GetCurrentUserAsync();
        if (me is null) return Challenge();
        me.FaceHash = null;
        me.FaceEnrolledAt = null;
        await Db.SaveChangesAsync();
        TempData.Flash("Face profile cleared.", "success");
        return RedirectToAction(nameof(Index));
    }
}
