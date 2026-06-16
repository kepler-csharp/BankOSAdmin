using BankAdmin.Models;
using BankAdmin.Services;
using Microsoft.AspNetCore.Mvc;

namespace BankAdmin.Controllers;

public class AuthController : Controller
{
    private readonly BankAdminApiService _api;

    public AuthController(BankAdminApiService api) => _api = api;

    [HttpGet]
    public async Task<IActionResult> Login()
    {
        if (!string.IsNullOrEmpty(HttpContext.Session.GetString("Token")))
            return RedirectToAction("Index", "Dashboard");

        ViewBag.Banks = await _api.GetBanksAsync();
        return View(new LoginViewModel());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel vm)
    {
        ViewBag.Banks = await _api.GetBanksAsync();

        if (string.IsNullOrWhiteSpace(vm.TenantId) || string.IsNullOrWhiteSpace(vm.Email) || string.IsNullOrWhiteSpace(vm.Password))
        {
            ViewBag.Error = "Selecciona tu banco e ingresa correo y contraseña.";
            return View(vm);
        }

        var (auth, err) = await _api.LoginAsync(vm.TenantId, vm.Email, vm.Password);
        if (err != null || auth == null)
        {
            ViewBag.Error = err ?? "No se pudo iniciar sesión.";
            return View(vm);
        }

        if (!string.Equals(auth.User.Role, "administrador", StringComparison.OrdinalIgnoreCase))
        {
            ViewBag.Error = "Esta cuenta no es de administrador. Si eres cliente y deseas tu certificado, usa la opción «Solicitar certificado».";
            return View(vm);
        }

        var bank = ((List<BankModel>)ViewBag.Banks).FirstOrDefault(b => b.Id == vm.TenantId);

        HttpContext.Session.SetString("Token", auth.Token);
        HttpContext.Session.SetString("TenantId", vm.TenantId);
        HttpContext.Session.SetString("TenantName", bank?.Name ?? vm.TenantId);
        HttpContext.Session.SetString("UserId", auth.User.Id);
        HttpContext.Session.SetString("UserName", auth.User.Name);
        HttpContext.Session.SetString("UserEmail", auth.User.Email);
        HttpContext.Session.SetString("UserRole", auth.User.Role);

        return RedirectToAction("Index", "Dashboard");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Logout()
    {
        HttpContext.Session.Clear();
        return RedirectToAction("Index", "Home");
    }
}
