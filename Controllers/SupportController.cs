using BankAdmin.Models;
using BankAdmin.Services;
using Microsoft.AspNetCore.Mvc;

namespace BankAdmin.Controllers;

public class SupportController : AdminControllerBase
{
    private readonly BankAdminApiService _api;
    private readonly EmailService _email;

    public SupportController(BankAdminApiService api, EmailService email)
    {
        _api = api;
        _email = email;
    }

    // ── PQRS ──────────────────────────────────────────────────────────────────

    public async Task<IActionResult> Pqrs()
    {
        if (NotAuthed(out var r)) return r;
        var (items, err) = await _api.GetPqrsAsync(Token!, TenantId!);
        if (err != null) TempData["Error"] = err;
        return View(items);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Respond(string id, string response)
    {
        if (NotAuthed(out var r)) return r;
        if (string.IsNullOrWhiteSpace(response) || response.Trim().Length < 5)
        {
            TempData["Error"] = "La respuesta debe tener al menos 5 caracteres.";
            return RedirectToAction("Pqrs");
        }

        // The Laravel API used to email the client; we now do it from here.
        var err = await _api.RespondPqrsAsync(Token!, TenantId!, id, response);
        if (err == null)
        {
            // Look up the ticket to notify the client by email.
            var (items, _) = await _api.GetPqrsAsync(Token!, TenantId!);
            var pqrs = items.FirstOrDefault(p => p.Id == id);
            if (pqrs != null && !string.IsNullOrWhiteSpace(pqrs.UserEmail))
                _ = _email.SendPqrsResponseAsync(pqrs.UserEmail!, pqrs.UserName ?? "", pqrs.Type, pqrs.Subject, response);
        }

        TempData[err == null ? "Success" : "Error"] = err ?? "Respuesta enviada al cliente por correo.";
        return RedirectToAction("Pqrs");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkInReview(string id)
    {
        if (NotAuthed(out var r)) return r;
        var err = await _api.UpdatePqrsStatusAsync(Token!, TenantId!, id, "en_revision");
        TempData[err == null ? "Success" : "Error"] = err ?? "PQRS marcada en revisión.";
        return RedirectToAction("Pqrs");
    }

    // ── Audit ────────────────────────────────────────────────────────────────

    public async Task<IActionResult> Audit()
    {
        if (NotAuthed(out var r)) return r;
        var (logs, err) = await _api.GetAuditLogsAsync(Token!, TenantId!, perPage: 150);
        if (err != null) TempData["Error"] = err;

        var (users, _) = await _api.GetUsersAsync(Token!, TenantId!);
        var byId = users.ToDictionary(u => u.Id, u => u.Name);
        foreach (var l in logs)
            if (l.PerformedByUserId != null && byId.TryGetValue(l.PerformedByUserId, out var n)) l.PerformedByName = n;

        return View(logs);
    }
}
