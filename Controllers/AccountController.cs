using System.Security.Claims;
using System.Text.RegularExpressions;
using AttendanceMonitoring.Data;
using AttendanceMonitoring.Helpers;
using AttendanceMonitoring.Models;
using AttendanceMonitoring.Services;
using AttendanceMonitoring.ViewModels;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AttendanceMonitoring.Controllers;

public class AccountController : AppController
{
    public AccountController(AppDbContext db) : base(db) { }

    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToAction("Index", "Dashboard");
        ViewData["ReturnUrl"] = returnUrl;
        return View(new LoginViewModel());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel vm, string? returnUrl = null)
    {
        var username = (vm.Username ?? string.Empty).Trim();
        var password = vm.Password ?? string.Empty;

        var user = await Db.Users.FirstOrDefaultAsync(u => u.Username == username);
        if (user is null || !PasswordHasher.Verify(password, user.PasswordHash))
        {
            TempData.Flash("Invalid username or password.", "danger");
            ViewData["ReturnUrl"] = returnUrl;
            return View(vm);
        }

        // Reject sign-in until an admin has approved the account. The
        // approval flag is flipped from the Users tab on the admin side.
        if (!user.Approved)
        {
            TempData.Flash(
                "Your account is still awaiting administrator approval. "
                + "You'll be able to sign in once it's reviewed.",
                "warning");
            ViewData["ReturnUrl"] = returnUrl;
            return View(vm);
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new("FullName", user.FullName),
            new(ClaimTypes.Role, user.Role),
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });

        // Record the workstation (IP + best-effort computer name) the user
        // signed in from. Hostname is null when reverse DNS isn't available.
        var (ip, host) = await ClientInfo.ResolveAsync(HttpContext);
        user.LastLoginIp = ip;
        user.LastLoginHost = host;
        // Geo-resolve the public IP so the admin Team status can show
        // "City, Country" instead of the raw hostname. Best-effort: null
        // when the IP is private/loopback or the upstream service fails.
        user.LastLoginLocation = await ClientInfo.ResolveLocationAsync(ip);
        // Mark this value as the IP-based fallback. The dashboard JS will
        // try to upgrade it to a GPS-based reading on next page load by
        // calling navigator.geolocation → POST /api/location/set, which
        // flips the source to "gps". Reset the coords so a stale GPS
        // reading from a previous login doesn't bleed into this session.
        user.LastLoginLocationSource = "ip";
        user.LastLoginLatitude = null;
        user.LastLoginLongitude = null;
        // User-Agent fingerprint: helps differentiate two users who share
        // a public IP because they're behind the same office wifi.
        // Browsers do NOT expose the local Windows username or machine
        // name; capturing the UA is the most we can do server-side.
        user.LastLoginAgent = ClientInfo.GetAgentLabel(HttpContext);
        user.LastLoginAt = DateTime.UtcNow;
        user.LastSeen = DateTime.UtcNow;
        // Login implies the user is in front of the workstation; clear any
        // stale "PC was locked" stamp from the previous session so the
        // offline-notifier doesn't immediately reuse it.
        user.PresenceState = "online";
        user.OfflineSince = null;
        await Db.SaveChangesAsync();

        TempData.Flash($"Welcome back, {user.FullName}!", "success");

        // Force a password change before the user can navigate anywhere
        // else. This is the admin-reset path — they got a temp password
        // from their admin and have to pick a new one now.
        if (user.MustChangePassword)
        {
            TempData.Flash(
                "Please choose a new password to replace your temporary one.",
                "info");
            return RedirectToAction(nameof(ChangePassword));
        }

        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            return Redirect(returnUrl);
        return RedirectToAction("Index", "Dashboard");
    }

    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        var user = await GetCurrentUserAsync();
        if (user is not null)
        {
            // Logout marks the user offline (clears LastSeen) but does NOT
            // close their attendance row. Forgetting to click "Check out"
            // before signing out would otherwise silently end their shift,
            // which the team agreed should never happen automatically.
            user.LastSeen = null;
            user.PresenceState = "offline";
            user.OfflineSince ??= DateTime.UtcNow;
            await Db.SaveChangesAsync();
        }

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        TempData.Flash("You have been logged out.", "info");
        return RedirectToAction(nameof(Login));
    }

    // ----- Public registration ---------------------------------------------
    // Anyone can register, but the account is created as a plain
    // employee and stays in the "pending approval" queue until an
    // administrator reviews it from the Users tab. The role is
    // ignored on the public form — promotion happens via Edit user.

    [HttpGet]
    [AllowAnonymous]
    public IActionResult Register()
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToAction("Index", "Dashboard");
        // Public form has no manager / job-title dropdowns — those are
        // admin-only fields set from the Users tab after approval.
        return View(new NewUserViewModel
        {
            BusinessUnitChoices = RuntimeConfig.GetBusinessUnits(),
        });
    }

    /// <summary>
    /// Approved PMs (and admins) shown in the "Assigned to" picklist on
    /// the Users-Create / Users-Edit forms. Sorted by name for stable
    /// display.
    /// </summary>
    private async Task<List<User>> GetManagerChoicesAsync()
    {
        return await Db.Users
            .Where(u => u.Approved
                        && (u.Role == Roles.Pm || u.Role == Roles.Admin))
            .OrderBy(u => u.FullName)
            .ToListAsync();
    }

    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(NewUserViewModel vm)
    {
        // Normalise — every text input is trimmed before we look at
        // it so trailing spaces don't slip past uniqueness checks.
        var username = (vm.Username ?? string.Empty).Trim();
        var email = (vm.Email ?? string.Empty).Trim();
        var fullName = (vm.FullName ?? string.Empty).Trim();
        var employeeId = (vm.EmployeeId ?? string.Empty).Trim();
        // Accept the legacy "(India)" suffix transparently and rewrite to "(IN)".
        var businessUnit = RuntimeConfig.NormaliseBusinessUnit(vm.BusinessUnit) ?? string.Empty;
        // RoleLabelId and ManagerId are admin-only fields (set from the
        // Users tab). The public registration form never offers them, but
        // a crafted POST could still try to spoof a value — drop both here
        // so the new account always starts with no role label and no manager.
        var password = vm.Password ?? string.Empty;

        // Echo cleaned values back so a failed submit keeps the user's input.
        vm.Username = username;
        vm.Email = email;
        vm.FullName = fullName;
        vm.EmployeeId = employeeId;
        vm.BusinessUnit = businessUnit;
        vm.RoleLabelId = null;
        vm.ManagerId = null;
        // Picklists unused on the public form but kept hydrated so any
        // future server-side render path doesn't NPE on a null collection.
        vm.ManagerChoices = new List<User>();
        vm.RoleLabelChoices = new List<RoleDefinition>();
        vm.BusinessUnitChoices = RuntimeConfig.GetBusinessUnits();
        // Never echo passwords back into the markup.
        vm.Password = string.Empty;

        // Collect every problem so the user can fix them all in one
        // round-trip instead of one-at-a-time via flash messages.
        var errors = new Dictionary<string, string>();

        if (string.IsNullOrEmpty(fullName))
            errors["FullName"] = "Full name is required.";
        else if (fullName.Length < 2 || fullName.Length > 120)
            errors["FullName"] = "Full name must be 2–120 characters.";

        if (string.IsNullOrEmpty(username))
            errors["Username"] = "Username is required.";
        else if (!Regex.IsMatch(username, @"^[A-Za-z0-9_.\-]{3,64}$"))
            errors["Username"] =
                "Username must be 3–64 characters using letters, digits, dot, underscore or hyphen.";

        if (string.IsNullOrEmpty(email))
            errors["Email"] = "Email is required.";
        else if (!Regex.IsMatch(email, @"^[^\s@]+@[^\s@]+\.[^\s@]+$"))
            errors["Email"] = "Enter a valid email address.";
        else if (email.Length > 200)
            errors["Email"] = "Email is too long (max 200 characters).";

        if (!string.IsNullOrEmpty(employeeId)
            && !Regex.IsMatch(employeeId, @"^E\d{6,7}$"))
        {
            errors["EmployeeId"] = "Employee ID must be E followed by 6–7 digits (e.g. E000123).";
        }

        if (!string.IsNullOrEmpty(businessUnit) && businessUnit.Length > 120)
            errors["BusinessUnit"] = "Business unit name is too long (max 120 characters).";
        else if (!string.IsNullOrEmpty(businessUnit) && !RuntimeConfig.IsKnownBusinessUnit(businessUnit))
            errors["BusinessUnit"] = "Pick a valid Business Unit from the list.";
        else if (string.IsNullOrEmpty(businessUnit))
            errors["BusinessUnit"] = "Business Unit is required.";

        // RoleLabelId / ManagerId are admin-only — see the field-clear
        // above; we intentionally don't validate them here.

        if (string.IsNullOrEmpty(password))
            errors["Password"] = "Password is required.";
        else if (password.Length < 6)
            errors["Password"] = "Password must be at least 6 characters.";
        else if (password.Length > 200)
            errors["Password"] = "Password is too long.";
        else if (!Regex.IsMatch(password, @"[A-Za-z]") || !Regex.IsMatch(password, @"\d"))
            errors["Password"] = "Password must contain at least one letter and one digit.";

        // Uniqueness checks — only run if the field itself was valid so
        // we don't spam the DB with malformed values.
        if (!errors.ContainsKey("Username")
            && await Db.Users.AnyAsync(u => u.Username == username))
        {
            errors["Username"] = "That username is already taken.";
        }
        if (!errors.ContainsKey("Email")
            && await Db.Users.AnyAsync(u => u.Email == email))
        {
            errors["Email"] = "That email is already registered.";
        }

        if (errors.Count > 0)
        {
            ViewBag.FieldErrors = errors;
            TempData.Flash(
                "Please fix the highlighted fields and try again.",
                "danger");
            return View(vm);
        }

        // Public registration always creates an employee. Admins/PMs
        // are minted via the Edit user screen after approval. RoleLabelId
        // and ManagerId are admin-only and remain null here; an admin
        // sets them later from the Users tab.
        var user = new User
        {
            Username = username,
            Email = email,
            FullName = fullName,
            EmployeeId = string.IsNullOrEmpty(employeeId) ? null : employeeId,
            BusinessUnit = string.IsNullOrEmpty(businessUnit) ? null : businessUnit,
            RoleLabelId = null,
            ManagerId = null,
            Role = Roles.Employee,
            PasswordHash = PasswordHasher.Hash(password),
            CreatedAt = DateTime.UtcNow,
            Approved = false, // Awaits admin approval before login is allowed.
        };
        Db.Users.Add(user);
        await Db.SaveChangesAsync();

        TempData.Flash(
            $"Account '{username}' submitted for review. An administrator will approve it before you can sign in.",
            "info");
        return RedirectToAction(nameof(Login));
    }

    // ----- Forgot / reset password -----------------------------------------
    // Email-based reset is intentionally disabled: the GET page just tells
    // the user to contact their administrator, who can reset the password
    // from the Users tab and hand the user a one-time temporary password.
    // The Reset/ChangePassword endpoints below remain for the temp-password
    // flow.

    [HttpGet]
    [AllowAnonymous]
    public IActionResult ForgotPassword()
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToAction("Index", "Dashboard");
        return View();
    }

    // ----- Change password (authenticated user) ----------------------------
    // Lets any signed-in user pick a new password by confirming the current
    // one. Also the destination the login flow redirects to when an admin
    // has flipped MustChangePassword on a user.

    [HttpGet]
    [Authorize]
    public IActionResult ChangePassword()
    {
        return View(new ChangePasswordViewModel());
    }

    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangePassword(ChangePasswordViewModel vm)
    {
        var current = vm.CurrentPassword ?? string.Empty;
        var next = vm.NewPassword ?? string.Empty;
        var confirm = vm.ConfirmPassword ?? string.Empty;

        // Never echo password values back into the rendered form.
        vm.CurrentPassword = string.Empty;
        vm.NewPassword = string.Empty;
        vm.ConfirmPassword = string.Empty;

        var user = await GetCurrentUserAsync();
        if (user is null) return Challenge();

        var errors = new Dictionary<string, string>();

        if (string.IsNullOrEmpty(current))
            errors["CurrentPassword"] = "Current password is required.";
        else if (!PasswordHasher.Verify(current, user.PasswordHash))
            errors["CurrentPassword"] = "That doesn't match your current password.";

        if (string.IsNullOrEmpty(next))
            errors["NewPassword"] = "New password is required.";
        else if (next.Length < 6)
            errors["NewPassword"] = "Password must be at least 6 characters.";
        else if (next.Length > 200)
            errors["NewPassword"] = "Password is too long.";
        else if (!Regex.IsMatch(next, @"[A-Za-z]") || !Regex.IsMatch(next, @"\d"))
            errors["NewPassword"] = "Password must contain at least one letter and one digit.";
        else if (next == current)
            errors["NewPassword"] = "New password must be different from the current one.";

        if (next != confirm)
            errors["ConfirmPassword"] = "Passwords don't match.";

        if (errors.Count > 0)
        {
            ViewBag.FieldErrors = errors;
            return View(vm);
        }

        user.PasswordHash = PasswordHasher.Hash(next);
        user.MustChangePassword = false;
        user.PasswordResetTokenHash = null;
        user.PasswordResetExpiresAt = null;
        await Db.SaveChangesAsync();

        TempData.Flash("Your password has been updated.", "success");
        return RedirectToAction("Index", "Dashboard");
    }
}
