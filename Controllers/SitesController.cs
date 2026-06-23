using AttendanceMonitoring.Data;
using AttendanceMonitoring.Helpers;
using AttendanceMonitoring.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AttendanceMonitoring.Controllers;

/// <summary>
/// Admin-only management of geofenced office sites. Sites are matched
/// at check-in by <see cref="Services.GeofenceCheck.EvaluateAsync"/>:
/// the user's coordinates must fall within
/// <see cref="Site.RadiusMeters"/> of one active site.
/// </summary>
[Authorize(Roles = Roles.Admin)]
[Route("sites")]
public class SitesController : AppController
{
    public SitesController(AppDbContext db) : base(db) { }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var rows = await Db.Sites
            .OrderByDescending(s => s.IsActive)
            .ThenBy(s => s.BusinessUnit)
            .ThenBy(s => s.Name)
            .ToListAsync();
        return View(rows);
    }

    [HttpGet("create")]
    public IActionResult Create() => View(new Site
    {
        RadiusMeters = 150,
        IsActive = true,
        Name = "",
    });

    [HttpPost("create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreatePost(Site model)
    {
        Normalise(model);
        if (!ModelState.IsValid) return View("Create", model);
        model.CreatedAt = DateTime.UtcNow;
        model.UpdatedAt = DateTime.UtcNow;
        Db.Sites.Add(model);
        await Db.SaveChangesAsync();
        TempData.Flash($"Added site \"{model.Name}\".", "success");
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("edit/{id:int}")]
    public async Task<IActionResult> Edit(int id)
    {
        var row = await Db.Sites.FindAsync(id);
        if (row is null) return NotFound();
        return View(row);
    }

    [HttpPost("edit/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditPost(int id, Site model)
    {
        var row = await Db.Sites.FindAsync(id);
        if (row is null) return NotFound();
        Normalise(model);
        if (!ModelState.IsValid) return View("Edit", model);
        row.Name = model.Name;
        row.Latitude = model.Latitude;
        row.Longitude = model.Longitude;
        row.RadiusMeters = model.RadiusMeters;
        row.BusinessUnit = model.BusinessUnit;
        row.Notes = model.Notes;
        row.IsActive = model.IsActive;
        row.UpdatedAt = DateTime.UtcNow;
        await Db.SaveChangesAsync();
        TempData.Flash("Site updated.", "success");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("delete/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var row = await Db.Sites.FindAsync(id);
        if (row is null) return NotFound();
        Db.Sites.Remove(row);
        await Db.SaveChangesAsync();
        TempData.Flash("Site removed.", "success");
        return RedirectToAction(nameof(Index));
    }

    private static void Normalise(Site model)
    {
        // Snap any unrealistic radius back to a sensible band.
        if (model.RadiusMeters < 10) model.RadiusMeters = 10;
        if (model.RadiusMeters > 5000) model.RadiusMeters = 5000;
        if (model.Latitude is < -90 or > 90)
            model.Latitude = 0;
        if (model.Longitude is < -180 or > 180)
            model.Longitude = 0;
        model.Name = (model.Name ?? "").Trim();
        if (model.BusinessUnit is not null)
            model.BusinessUnit = string.IsNullOrWhiteSpace(model.BusinessUnit)
                ? null : model.BusinessUnit.Trim();
    }
}
