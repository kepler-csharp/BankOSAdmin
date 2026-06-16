using BankAdmin.Models;
using BankAdmin.Services;
using Microsoft.AspNetCore.Mvc;

namespace BankAdmin.Controllers;

public class ClientsController : AdminControllerBase
{
    private readonly BankAdminApiService _api;
    private readonly EmailService _email;
    private readonly PdfService _pdf;

    public ClientsController(BankAdminApiService api, EmailService email, PdfService pdf)
    {
        _api = api; _email = email; _pdf = pdf;
    }

    public async Task<IActionResult> Index()
    {
        if (NotAuthed(out var r)) return r;

        var (users, err) = await _api.GetUsersAsync(Token!, TenantId!);
        if (err != null) TempData["Error"] = err;

        var (accounts, _) = await _api.GetAccountsAsync(Token!, TenantId!);
        var counts = accounts.GroupBy(a => a.UserId).ToDictionary(g => g.Key, g => g.Count());
        foreach (var u in users) u.AccountsCount = counts.TryGetValue(u.Id, out var c) ? c : 0;

        return View(users);
    }

    // ── Create ───────────────────────────────────────────────────────────────

    [HttpGet]
    public IActionResult Create()
    {
        if (NotAuthed(out var r)) return r;
        return View(new CreateClientViewModel());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateClientViewModel vm)
    {
        if (NotAuthed(out var r)) return r;
        if (string.IsNullOrWhiteSpace(vm.Name) || string.IsNullOrWhiteSpace(vm.Email) || (vm.Password?.Length ?? 0) < 8)
        {
            ViewBag.Error = "Completa nombre, correo y una contraseña de al menos 8 caracteres.";
            return View(vm);
        }

        var (user, err) = await _api.CreateUserAsync(Token!, TenantId!, vm);
        if (err != null) { ViewBag.Error = err; return View(vm); }

        // Notify the client with their credentials (sent from this MVC app)
        _ = _email.SendUserCreatedAsync(vm.Email, vm.Name, vm.Email, vm.Password!, vm.Role, TenantName ?? TenantId!);

        TempData["Success"] = $"{(vm.Role == "administrador" ? "Administrador" : "Cliente")} «{vm.Name}» creado. Se notificó por correo a {vm.Email}.";
        return RedirectToAction("Index");
    }

    // ── Edit ───────────────────────────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> Edit(string id)
    {
        if (NotAuthed(out var r)) return r;
        var (user, err) = await _api.GetUserAsync(Token!, TenantId!, id);
        if (err != null || user == null) { TempData["Error"] = err ?? "Cliente no encontrado."; return RedirectToAction("Index"); }

        return View(new EditClientViewModel { Id = user.Id, Name = user.Name, Email = user.Email, Role = user.Role });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(EditClientViewModel vm)
    {
        if (NotAuthed(out var r)) return r;

        var (old, _) = await _api.GetUserAsync(Token!, TenantId!, vm.Id);
        var err = await _api.UpdateUserAsync(Token!, TenantId!, vm);
        if (err != null) { ViewBag.Error = err; return View(vm); }

        if (old != null)
        {
            var changes = new Dictionary<string, (string, string)>();
            if (old.Name != vm.Name) changes["Nombre"] = (old.Name, vm.Name);
            if (old.Email != vm.Email) changes["Correo"] = (old.Email, vm.Email);
            if (!string.Equals(old.Role, vm.Role, StringComparison.OrdinalIgnoreCase))
                changes["Rol"] = (RoleLabel(old.Role), RoleLabel(vm.Role));
            if (!string.IsNullOrWhiteSpace(vm.NewPassword)) changes["Contraseña"] = ("•••••••", "Actualizada");

            if (changes.Count > 0)
                _ = _email.SendUserUpdatedAsync(vm.Email, vm.Name, changes);
        }

        TempData["Success"] = $"Cliente «{vm.Name}» actualizado correctamente.";
        return RedirectToAction("Index");
    }

    // ── Toggle status ──────────────────────────────────────────────────────────

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleStatus(string id, string? name, string? email)
    {
        if (NotAuthed(out var r)) return r;

        var (newStatus, err) = await _api.ToggleUserStatusAsync(Token!, TenantId!, id);
        if (err != null) { TempData["Error"] = err; return RedirectToAction("Index"); }

        var active = string.Equals(newStatus, "active", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(email))
            _ = _email.SendUserStatusAsync(email, name ?? "", active);

        TempData["Success"] = active ? $"Cliente «{name}» reactivado y notificado." : $"Cliente «{name}» desactivado y notificado.";
        return RedirectToAction("Index");
    }

    // ── Certificate (PDF) ────────────────────────────────────────────────────

    public async Task<IActionResult> Certificate(string id)
    {
        if (NotAuthed(out var r)) return r;

        var (user, err) = await _api.GetUserAsync(Token!, TenantId!, id);
        if (err != null || user == null) { TempData["Error"] = err ?? "Cliente no encontrado."; return RedirectToAction("Index"); }

        var (accounts, _) = await _api.GetAccountsAsync(Token!, TenantId!);
        var owned = accounts.Where(a => a.UserId == id).ToList();

        var pdf = _pdf.GenerateClientCertificate(TenantName ?? TenantId!, user, owned, issuedBy: UserName);
        var safe = user.Name.Replace(" ", "-").ToLowerInvariant();
        return File(pdf, "application/pdf", $"certificado-{safe}-{DateTime.Now:yyyyMMdd}.pdf");
    }

    private static string RoleLabel(string role) =>
        string.Equals(role, "administrador", StringComparison.OrdinalIgnoreCase) ? "Administrador" : "Cliente";
}
