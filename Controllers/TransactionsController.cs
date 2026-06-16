using BankAdmin.Models;
using BankAdmin.Services;
using Microsoft.AspNetCore.Mvc;

namespace BankAdmin.Controllers;

public class TransactionsController : AdminControllerBase
{
    private readonly BankAdminApiService _api;

    public TransactionsController(BankAdminApiService api) => _api = api;

    public async Task<IActionResult> Index(string? type)
    {
        if (NotAuthed(out var r)) return r;

        var (tx, err) = await _api.GetTransactionsAsync(Token!, TenantId!, type: type, perPage: 200);
        if (err != null) TempData["Error"] = err;

        var (accounts, _) = await _api.GetAccountsAsync(Token!, TenantId!);
        ViewBag.AccountNumbers = accounts.ToDictionary(a => a.Id, a => a.AccountNumber);
        ViewBag.Filter = type ?? "";
        return View(tx);
    }

    public async Task<IActionResult> Detail(string id)
    {
        if (NotAuthed(out var r)) return r;
        var (tx, err) = await _api.GetTransactionAsync(Token!, TenantId!, id);
        if (err != null || tx == null) { TempData["Error"] = err ?? "Transacción no encontrada."; return RedirectToAction("Index"); }

        var (accounts, _) = await _api.GetAccountsAsync(Token!, TenantId!);
        ViewBag.AccountNumbers = accounts.ToDictionary(a => a.Id, a => a.AccountNumber);
        return View(tx);
    }

    // ── Deposit ───────────────────────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> Deposit()
    {
        if (NotAuthed(out var r)) return r;
        await LoadAccounts();
        return View(new DepositViewModel { Currency = await CurrencyAsync() });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Deposit(DepositViewModel vm)
    {
        if (NotAuthed(out var r)) return r;
        await LoadAccounts();

        if (string.IsNullOrWhiteSpace(vm.AccountId) || vm.Amount <= 0)
        {
            ViewBag.Error = "Selecciona la cuenta e ingresa un monto válido.";
            return View(vm);
        }

        var (tx, err) = await _api.DepositAsync(Token!, TenantId!, vm);
        if (err != null) { ViewBag.Error = err; return View(vm); }

        TempData["Success"] = $"Depósito de {vm.Amount:N2} {vm.Currency} realizado correctamente.";
        return RedirectToAction("Index");
    }

    // ── Transfer ─────────────────────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> Transfer()
    {
        if (NotAuthed(out var r)) return r;
        await LoadAccounts();
        return View(new TransferViewModel { Currency = await CurrencyAsync() });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Transfer(TransferViewModel vm)
    {
        if (NotAuthed(out var r)) return r;
        await LoadAccounts();

        if (string.IsNullOrWhiteSpace(vm.SourceAccountId) || string.IsNullOrWhiteSpace(vm.DestinationAccountId) || vm.Amount <= 0)
        {
            ViewBag.Error = "Selecciona las cuentas origen y destino e ingresa un monto válido.";
            return View(vm);
        }
        if (vm.SourceAccountId == vm.DestinationAccountId)
        {
            ViewBag.Error = "La cuenta origen y destino no pueden ser la misma.";
            return View(vm);
        }

        var (tx, err) = await _api.TransferAsync(Token!, TenantId!, vm);
        if (err != null) { ViewBag.Error = err; return View(vm); }

        TempData["Success"] = $"Transferencia de {vm.Amount:N2} {vm.Currency} realizada correctamente.";
        return RedirectToAction("Index");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task LoadAccounts()
    {
        var (accounts, _) = await _api.GetAccountsAsync(Token!, TenantId!);
        var (users, _) = await _api.GetUsersAsync(Token!, TenantId!);
        var byId = users.ToDictionary(u => u.Id, u => u);
        foreach (var a in accounts)
            if (byId.TryGetValue(a.UserId, out var u)) a.OwnerName = u.Name;
        ViewBag.Accounts = accounts.Where(a => a.IsActive).ToList();
    }

    private async Task<string> CurrencyAsync()
    {
        var (config, _) = await _api.GetConfigAsync(Token!, TenantId!);
        return config?.Currency ?? "COP";
    }
}
