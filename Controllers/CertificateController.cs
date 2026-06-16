using BankAdmin.Models;
using BankAdmin.Services;
using Microsoft.AspNetCore.Mvc;

namespace BankAdmin.Controllers;

/// <summary>
/// Public self-service certificate. A client proves their identity with their own
/// BankOS credentials; the certificate is both downloaded and emailed to their
/// registered address. No admin session is involved.
/// </summary>
public class CertificateController : Controller
{
    private readonly BankAdminApiService _api;
    private readonly EmailService _email;
    private readonly PdfService _pdf;

    public CertificateController(BankAdminApiService api, EmailService email, PdfService pdf)
    {
        _api = api; _email = email; _pdf = pdf;
    }

    [HttpGet]
    public async Task<IActionResult> Request()
    {
        ViewBag.Banks = await _api.GetBanksAsync();
        return View(new CertificateRequestViewModel());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Request(CertificateRequestViewModel vm)
    {
        var banks = await _api.GetBanksAsync();
        ViewBag.Banks = banks;

        if (string.IsNullOrWhiteSpace(vm.TenantId) || string.IsNullOrWhiteSpace(vm.Email) || string.IsNullOrWhiteSpace(vm.Password))
        {
            ViewBag.Error = "Selecciona tu banco e ingresa tu correo y contraseña.";
            return View(vm);
        }

        // Authenticate as the client itself (secure: only the account owner can request it).
        var (auth, err) = await _api.LoginAsync(vm.TenantId, vm.Email, vm.Password);
        if (err != null || auth == null)
        {
            ViewBag.Error = err ?? "Credenciales inválidas.";
            return View(vm);
        }

        // With the client's own token, /accounts returns only their accounts.
        var (accounts, accErr) = await _api.GetAccountsAsync(auth.Token, vm.TenantId);
        if (accErr != null)
        {
            ViewBag.Error = accErr;
            return View(vm);
        }

        var bank = banks.FirstOrDefault(b => b.Id == vm.TenantId);
        var tenantName = bank?.Name ?? vm.TenantId;

        var pdf = _pdf.GenerateClientCertificate(tenantName, auth.User, accounts, issuedBy: "Solicitud del titular");
        var safe = (auth.User.Name ?? "cliente").Replace(" ", "-").ToLowerInvariant();
        var fileName = $"certificado-{safe}-{DateTime.Now:yyyyMMdd}.pdf";

        // Email a copy to the registered address (from the authenticated profile, never an arbitrary one).
        _ = _email.SendCertificateAsync(auth.User.Email, auth.User.Name, tenantName, pdf, fileName);

        TempData["Emailed"] = auth.User.Email;
        return File(pdf, "application/pdf", fileName);
    }
}
