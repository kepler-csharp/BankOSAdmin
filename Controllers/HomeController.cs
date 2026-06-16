using System.Diagnostics;
using BankAdmin.Services;
using Microsoft.AspNetCore.Mvc;

namespace BankAdmin.Controllers;

public class HomeController : Controller
{
    private readonly BankAdminApiService _api;

    public HomeController(BankAdminApiService api) => _api = api;

    public async Task<IActionResult> Index()
    {
        var banks = await _api.GetBanksAsync();
        ViewBag.BankCount = banks.Count;
        return View();
    }

    public IActionResult Privacy() => View();

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        ViewBag.RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;
        return View();
    }
}
