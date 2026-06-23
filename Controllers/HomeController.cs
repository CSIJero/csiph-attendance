using AttendanceMonitoring.Data;
using AttendanceMonitoring.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace AttendanceMonitoring.Controllers;

public class HomeController : AppController
{
    public HomeController(AppDbContext db) : base(db) { }

    [HttpGet("/")]
    public IActionResult Index()
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToAction("Index", "Dashboard");
        return RedirectToAction("Login", "Account");
    }

    [HttpGet("/Home/Forbidden")]
    public IActionResult Forbidden()
    {
        Response.StatusCode = 403;
        return View("Error", new ErrorViewModel { Code = 403, Message = "Forbidden" });
    }

    // Anonymous build marker so we can verify which commit Render is
    // actually running without logging in. Bump the constant below in
    // any commit you want to be able to identify on the live server.
    [HttpGet("/_version")]
    public IActionResult Version()
    {
        var asm = typeof(HomeController).Assembly;
        var info = asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? "(none)";
        var built = System.IO.File.GetLastWriteTimeUtc(asm.Location).ToString("u");
        var marker = "save-ajax-2026-05-19";   // <-- update when shipping
        return Content(
            $"marker   = {marker}\nbuilt    = {built}\nasm.ver  = {info}\n",
            "text/plain");
    }
    [HttpGet("/Home/Error")]
    public IActionResult Error()
    {
        Response.StatusCode = 500;
        return View("Error", new ErrorViewModel { Code = 500, Message = "Something went wrong." });
    }

    [HttpGet("/Home/Status/{code:int}")]
    public IActionResult Status(int code)
    {
        var message = code switch
        {
            400 => "Bad Request",
            401 => "Sign in required",
            403 => "Forbidden",
            404 => "Not Found",
            _ => "An error occurred.",
        };
        Response.StatusCode = code;
        return View("Error", new ErrorViewModel { Code = code, Message = message });
    }
}
