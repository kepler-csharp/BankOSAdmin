using BankAdmin.Models;
using BankAdmin.Services;
using Microsoft.AspNetCore.Mvc;

namespace BankAdmin.Controllers;

public class AccountsController : AdminControllerBase
{
    private readonly BankAdminApiService _api;
    private readonly EmailService _email;
    private readonly PdfService _pdf;

    public AccountsController(BankAdminApiService api, EmailService email, PdfService pdf)
    {
        _api = api; _email = email; _pdf = pdf;
    }

    public async Task<IActionResult> Index()
    {
        if (NotAuthed(out var r)) return r;

        var (accounts, err) = await _api.GetAccountsAsync(Token!, TenantId!);
        if (err != null) TempData["Error"] = err;
        await ResolveOwners(accounts);

        return View(accounts);
    }

    // ── Create ───────────────────────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> Create()
    {
        if (NotAuthed(out var r)) return r;
        await LoadClients();
        return View(new CreateAccountViewModel());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateAccountViewModel vm)
    {
        if (NotAuthed(out var r)) return r;
        await LoadClients();

        if (string.IsNullOrWhiteSpace(vm.AccountNumber) || string.IsNullOrWhiteSpace(vm.UserId))
        {
            ViewBag.Error = "Indica el número de cuenta y el titular.";
            return View(vm);
        }

        var (account, err) = await _api.CreateAccountAsync(Token!, TenantId!, vm);
        if (err != null) { ViewBag.Error = err; return View(vm); }

        var (owner, _) = await _api.GetUserAsync(Token!, TenantId!, vm.UserId);
        if (owner != null)
            _ = _email.SendAccountCreatedAsync(owner.Email, owner.Name, vm.AccountNumber, vm.Currency, vm.InitialBalance);

        TempData["Success"] = $"Cuenta {vm.AccountNumber} creada. Se notificó al titular por correo.";
        return RedirectToAction("Index");
    }

    // ── Edit (money-sensitive) ──────────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> Edit(string id)
    {
        if (NotAuthed(out var r)) return r;
        var (account, err) = await _api.GetAccountAsync(Token!, TenantId!, id);
        if (err != null || account == null) { TempData["Error"] = err ?? "Cuenta no encontrada."; return RedirectToAction("Index"); }

        var (owner, _) = await _api.GetUserAsync(Token!, TenantId!, account.UserId);

        return View(new EditAccountViewModel
        {
            Id = account.Id,
            AccountNumber = account.AccountNumber,
            Currency = account.Currency,
            Balance = account.Balance,
            CurrentBalance = account.Balance,
            Status = account.Status == "active" ? "active" : "inactive",
            OwnerEmail = owner?.Email,
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(EditAccountViewModel vm)
    {
        if (NotAuthed(out var r)) return r;

        var (old, _) = await _api.GetAccountAsync(Token!, TenantId!, vm.Id);
        var err = await _api.UpdateAccountAsync(Token!, TenantId!, vm);
        if (err != null) { ViewBag.Error = err; return View(vm); }

        if (old != null)
        {
            var (owner, _) = await _api.GetUserAsync(Token!, TenantId!, old.UserId);
            var changes = new Dictionary<string, (string, string)>();
            if (!string.Equals(old.Currency, vm.Currency, StringComparison.OrdinalIgnoreCase))
                changes["Moneda"] = (old.Currency, vm.Currency);
            if (old.Balance != vm.Balance)
                changes["Saldo"] = ($"{old.Balance:N2}", $"{vm.Balance:N2}");
            if (!string.Equals(old.Status, vm.Status, StringComparison.OrdinalIgnoreCase))
                changes["Estado"] = (StatusLabel(old.Status), StatusLabel(vm.Status));

            if (owner != null && changes.Count > 0)
                _ = _email.SendAccountUpdatedAsync(owner.Email, owner.Name, old.AccountNumber, changes, old.Balance > 0);
        }

        TempData["Success"] = $"Cuenta {vm.AccountNumber} actualizada. El titular fue notificado.";
        return RedirectToAction("Index");
    }

    // ── Toggle status ──────────────────────────────────────────────────────────

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleStatus(string id, string? reason)
    {
        if (NotAuthed(out var r)) return r;

        var (account, getErr) = await _api.GetAccountAsync(Token!, TenantId!, id);
        if (getErr != null || account == null) { TempData["Error"] = getErr ?? "Cuenta no encontrada."; return RedirectToAction("Index"); }

        var newStatus = account.IsActive ? "inactive" : "active";
        var err = await _api.UpdateAccountStatusAsync(Token!, TenantId!, id, newStatus, reason);
        if (err != null) { TempData["Error"] = err; return RedirectToAction("Index"); }

        var (owner, _) = await _api.GetUserAsync(Token!, TenantId!, account.UserId);
        var active = newStatus == "active";
        if (owner != null)
            _ = _email.SendAccountStatusAsync(owner.Email, owner.Name, account.AccountNumber, active, account.Balance, account.Currency);

        TempData["Success"] = active
            ? $"Cuenta {account.AccountNumber} reactivada. El titular fue notificado."
            : $"Cuenta {account.AccountNumber} desactivada. El titular fue notificado.";
        return RedirectToAction("Index");
    }

    // ── Certificate (PDF) ────────────────────────────────────────────────────

    public async Task<IActionResult> Certificate(string id)
    {
        if (NotAuthed(out var r)) return r;

        var (account, err) = await _api.GetAccountAsync(Token!, TenantId!, id);
        if (err != null || account == null) { TempData["Error"] = err ?? "Cuenta no encontrada."; return RedirectToAction("Index"); }

        var (owner, _) = await _api.GetUserAsync(Token!, TenantId!, account.UserId);
        var client = owner ?? new UserModel { Id = account.UserId, Name = "Titular", Email = "" };

        var pdf = _pdf.GenerateClientCertificate(TenantName ?? TenantId!, client, new List<AccountModel> { account }, issuedBy: UserName);
        return File(pdf, "application/pdf", $"certificado-cuenta-{account.AccountNumber}-{DateTime.Now:yyyyMMdd}.pdf");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task LoadClients()
    {
        var (users, _) = await _api.GetUsersAsync(Token!, TenantId!);
        ViewBag.Clients = users.OrderBy(u => u.Name).ToList();
    }

    private async Task ResolveOwners(List<AccountModel> accounts)
    {
        var (users, _) = await _api.GetUsersAsync(Token!, TenantId!);
        var byId = users.ToDictionary(u => u.Id, u => u);
        foreach (var a in accounts)
            if (byId.TryGetValue(a.UserId, out var u)) { a.OwnerName = u.Name; a.OwnerEmail = u.Email; }
    }

    private static string StatusLabel(string s) => s switch
    {
        "active" => "Activa",
        "inactive" => "Inactiva",
        "blocked" => "Bloqueada",
        _ => s
    };
}
