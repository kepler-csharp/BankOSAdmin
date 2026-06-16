using BankAdmin.Models;
using BankAdmin.Services;
using Microsoft.AspNetCore.Mvc;

namespace BankAdmin.Controllers;

public class DashboardController : AdminControllerBase
{
    private readonly BankAdminApiService _api;

    public DashboardController(BankAdminApiService api) => _api = api;

    public async Task<IActionResult> Index()
    {
        if (NotAuthed(out var r)) return r;

        var (users, _) = await _api.GetUsersAsync(Token!, TenantId!);
        var (accounts, _) = await _api.GetAccountsAsync(Token!, TenantId!);
        var (tx, _) = await _api.GetTransactionsAsync(Token!, TenantId!, perPage: 100);
        var (config, _) = await _api.GetConfigAsync(Token!, TenantId!);

        var stats = new DashboardStats
        {
            Users = users.Count,
            ActiveUsers = users.Count(u => u.IsActive),
            Accounts = accounts.Count,
            ActiveAccounts = accounts.Count(a => a.IsActive),
            Transactions = tx.Count,
            TotalBalance = accounts.Where(a => a.IsActive).Sum(a => a.Balance),
            Currency = config?.Currency ?? "COP",
        };

        // Resolve owner names for the recent-accounts widget
        var byId = users.ToDictionary(u => u.Id, u => u);
        foreach (var a in accounts)
            if (byId.TryGetValue(a.UserId, out var u)) { a.OwnerName = u.Name; a.OwnerEmail = u.Email; }

        ViewBag.Stats = stats;
        ViewBag.RecentTx = tx.Take(8).ToList();
        ViewBag.RecentAccounts = accounts.Take(6).ToList();
        ViewBag.ClientCount = users.Count(u => !u.IsAdmin);
        return View();
    }
}
