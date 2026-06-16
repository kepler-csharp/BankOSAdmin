using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BankAdmin.Controllers;

/// <summary>Shared session accessors + admin auth guard for the dashboard area.</summary>
public abstract class AdminControllerBase : Controller
{
    protected string? Token => HttpContext.Session.GetString("Token");
    protected string? TenantId => HttpContext.Session.GetString("TenantId");
    protected string? TenantName => HttpContext.Session.GetString("TenantName");
    protected string? UserId => HttpContext.Session.GetString("UserId");
    protected string? UserName => HttpContext.Session.GetString("UserName");
    protected string? UserEmail => HttpContext.Session.GetString("UserEmail");
    protected string? UserRole => HttpContext.Session.GetString("UserRole");

    protected bool IsAuthed =>
        !string.IsNullOrEmpty(Token) && !string.IsNullOrEmpty(TenantId) &&
        string.Equals(UserRole, "administrador", StringComparison.OrdinalIgnoreCase);

    /// <summary>Populate ViewBag with identity for the layout on every admin page.</summary>
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        ViewBag.TenantId = TenantId;
        ViewBag.TenantName = TenantName;
        ViewBag.UserName = UserName;
        ViewBag.UserEmail = UserEmail;
        base.OnActionExecuting(context);
    }

    /// <summary>Returns true (and a redirect) when the admin session is missing/invalid.</summary>
    protected bool NotAuthed(out IActionResult redirect)
    {
        if (IsAuthed) { redirect = null!; return false; }
        redirect = RedirectToAction("Login", "Auth");
        return true;
    }
}
