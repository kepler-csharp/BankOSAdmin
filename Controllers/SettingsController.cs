using BankAdmin.Models;
using BankAdmin.Services;
using Microsoft.AspNetCore.Mvc;

namespace BankAdmin.Controllers;

public class SettingsController : AdminControllerBase
{
    private readonly BankAdminApiService _api;

    public SettingsController(BankAdminApiService api) => _api = api;

    public async Task<IActionResult> Index()
    {
        if (NotAuthed(out var r)) return r;
        var (config, err) = await _api.GetConfigAsync(Token!, TenantId!);
        if (err != null) TempData["Error"] = err;

        ViewBag.Config = config ?? new TenantConfigModel();
        return View(new ConfigViewModel
        {
            MaxTransactionAmount = config?.MaxTransactionAmount ?? 0,
            TransferFeeType = config?.TransferFeeType ?? "percentage",
            TransferFeeValue = config?.TransferFeeValue ?? 0,
            WebhookUrl = config?.WebhookUrl,
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveConfig(ConfigViewModel vm)
    {
        if (NotAuthed(out var r)) return r;
        var err = await _api.UpdateConfigAsync(Token!, TenantId!, vm);
        TempData[err == null ? "Success" : "Error"] = err ?? "Configuración actualizada correctamente.";
        return RedirectToAction("Index");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangePassword(ChangePasswordViewModel vm)
    {
        if (NotAuthed(out var r)) return r;

        if (string.IsNullOrWhiteSpace(vm.CurrentPassword) || (vm.NewPassword?.Length ?? 0) < 8)
        {
            TempData["Error"] = "La nueva contraseña debe tener al menos 8 caracteres.";
            return RedirectToAction("Index");
        }
        if (vm.NewPassword != vm.ConfirmPassword)
        {
            TempData["Error"] = "La confirmación no coincide con la nueva contraseña.";
            return RedirectToAction("Index");
        }

        var err = await _api.ChangePasswordAsync(Token!, TenantId!, vm.CurrentPassword, vm.NewPassword!);
        TempData[err == null ? "Success" : "Error"] = err ?? "Contraseña actualizada correctamente.";
        return RedirectToAction("Index");
    }
}
