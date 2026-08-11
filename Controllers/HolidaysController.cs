using AttendanceMonitoring.Data;
using AttendanceMonitoring.Helpers;
using AttendanceMonitoring.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AttendanceMonitoring.Controllers;

/// <summary>
/// Admin-only management of statutory holidays. Holidays drive two
/// behaviours: (1) <see cref="Services.LateCheck.Evaluate"/> returns
/// NotApplicable on a holiday, (2) the dashboard surfaces a banner so
/// employees know a clock-in is voluntary / on-call coverage.
/// </summary>
[Authorize(Roles = $"{Roles.Admin},{Roles.Operations}")]
[Route("holidays")]
public class HolidaysController : AppController
{
    public HolidaysController(AppDbContext db) : base(db) { }

    [HttpGet("")]
    public async Task<IActionResult> Index(string? country = null)
    {
        var q = Db.Holidays.AsQueryable();
        if (!string.IsNullOrWhiteSpace(country))
            q = q.Where(h => h.Country == country);
        var rows = await q
            .OrderBy(h => h.Date)
            .ThenBy(h => h.Country)
            .Take(500)
            .ToListAsync();
        ViewBag.Filter = country;
        return View(rows);
    }

    [HttpGet("create")]
    public IActionResult Create() => View(new Holiday
    {
        Date = DateOnly.FromDateTime(DateTime.UtcNow),
        Country = "PH",
        Kind = "Regular",
        Name = "",
    });

    [HttpPost("create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreatePost(Holiday model)
    {
        if (!ModelState.IsValid) return View("Create", model);
        // Conflict check on (country, date).
        var exists = await Db.Holidays
            .AnyAsync(h => h.Country == model.Country && h.Date == model.Date);
        if (exists)
        {
            TempData.Flash("A holiday already exists for that country and date.", "warning");
            return View("Create", model);
        }
        model.CreatedAt = DateTime.UtcNow;
        Db.Holidays.Add(model);
        await Db.SaveChangesAsync();
        TempData.Flash($"Added holiday \"{model.Name}\".", "success");
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("edit/{id:int}")]
    public async Task<IActionResult> Edit(int id)
    {
        var row = await Db.Holidays.FindAsync(id);
        if (row is null) return NotFound();
        return View(row);
    }

    [HttpPost("edit/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditPost(int id, Holiday model)
    {
        var row = await Db.Holidays.FindAsync(id);
        if (row is null) return NotFound();
        if (!ModelState.IsValid) return View("Edit", model);
        row.Date = model.Date;
        row.Country = model.Country;
        row.Name = model.Name;
        row.Kind = model.Kind;
        await Db.SaveChangesAsync();
        TempData.Flash("Holiday updated.", "success");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("delete/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var row = await Db.Holidays.FindAsync(id);
        if (row is null) return NotFound();
        Db.Holidays.Remove(row);
        await Db.SaveChangesAsync();
        TempData.Flash("Holiday removed.", "success");
        return RedirectToAction(nameof(Index));
    }
}
