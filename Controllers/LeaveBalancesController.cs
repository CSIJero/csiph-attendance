using AttendanceMonitoring.Data;
using AttendanceMonitoring.Helpers;
using AttendanceMonitoring.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AttendanceMonitoring.Controllers;

/// <summary>
/// Admin view of leave balances populated by
/// <see cref="Services.LeaveAccrualService"/>. Allows manual one-off
/// adjustments (e.g. converting a forfeited day to a special grant).
/// </summary>
[Authorize(Roles = Roles.Admin)]
[Route("leave-balances")]
public class LeaveBalancesController : AppController
{
    public LeaveBalancesController(AppDbContext db) : base(db) { }

    [HttpGet("")]
    public async Task<IActionResult> Index(int? userId = null, string? bu = null)
    {
        var q = Db.LeaveBalances
            .Include(b => b.User)
            .AsQueryable();
        if (userId is not null)
            q = q.Where(b => b.UserId == userId.Value);
        if (!string.IsNullOrWhiteSpace(bu))
            q = q.Where(b => b.User!.BusinessUnit == bu);
        var rows = await q
            .OrderBy(b => b.User!.BusinessUnit)
            .ThenBy(b => b.User!.FullName)
            .ThenBy(b => b.LeaveType)
            .Take(1000)
            .ToListAsync();

        ViewBag.UserFilter = userId;
        ViewBag.BuFilter = bu;
        return View(rows);
    }

    [HttpPost("adjust/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Adjust(int id, [FromForm] decimal delta)
    {
        var row = await Db.LeaveBalances.FindAsync(id);
        if (row is null) return NotFound();
        row.HoursRemaining = Math.Max(0, row.HoursRemaining + delta);
        row.UpdatedAt = DateTime.UtcNow;
        await Db.SaveChangesAsync();
        TempData.Flash($"Adjusted balance by {delta:+0.00;-0.00;0.00} h.", "success");
        return RedirectToAction(nameof(Index));
    }
}
