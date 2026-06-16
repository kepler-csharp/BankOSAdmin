using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using BankAdmin.Models;

namespace BankAdmin.Services;

/// <summary>
/// Transactional emails to clients for the user/account lifecycle (sent from this MVC app, not the API),
/// plus the on-demand client certificate delivery. Email-safe inline styles, BankOs brand.
/// </summary>
public class EmailService
{
    private readonly IConfiguration _config;
    private readonly ILogger<EmailService> _logger;

    public EmailService(IConfiguration config, ILogger<EmailService> logger)
    {
        _config = config;
        _logger = logger;
    }

    private bool Enabled => bool.TryParse(_config["Email:Enabled"], out var e) && e;
    private string From => _config["Email:From"] ?? _config["Email:Username"] ?? "noreply@bankos.com";
    private string FromName => _config["Email:FromName"] ?? "BankOs";
    private string Portal => _config["Branding:PortalUrl"] ?? "http://bank-os.duckdns.org:8080";
    private string Support => _config["Branding:SupportEmail"] ?? "soporte@bankos.com";

    private SmtpClient BuildClient() => new(_config["Email:Host"] ?? "smtp.gmail.com")
    {
        Port = int.Parse(_config["Email:Port"] ?? "587"),
        Credentials = new NetworkCredential(_config["Email:Username"], _config["Email:Password"]),
        EnableSsl = true
    };

    private static string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");

    // ── User lifecycle ───────────────────────────────────────────────────────

    public Task SendUserCreatedAsync(string to, string name, string email, string password, string role, string tenant)
    {
        var body = Shell("#22c55e", "CUENTA CREADA", $"¡Bienvenido(a), {Enc(name)}!",
            "Tu cuenta de usuario fue creada en BankOs. Estas son tus credenciales de acceso.",
            $"""
            {Panel("Credenciales de acceso", "#0463fd", $"""
                {Row("Banco", Enc(tenant))}
                {Row("Email", Enc(email))}
                {Row("Contraseña temporal", Mono(Enc(password)), "#7c12fd")}
                {Row("Rol", role == "administrador" ? "Administrador" : "Cliente")}
            """)}
            <p style="color:#475569;font-size:13px;line-height:1.6;margin:22px 0 0">
              <b>Importante:</b> por seguridad, cambia tu contraseña en el primer inicio de sesión y no la compartas con nadie.
            </p>
            """);
        return SendAsync(to, "[BankOs] Tu cuenta ha sido creada", body);
    }

    public Task SendUserUpdatedAsync(string to, string name, Dictionary<string, (string Old, string New)> changes)
    {
        var body = Shell("#0463fd", "CUENTA ACTUALIZADA", "Se actualizó tu cuenta",
            $"Hola {Enc(name)}, se realizaron cambios en tu cuenta de usuario.",
            ChangesPanel(changes));
        return SendAsync(to, "[BankOs] Tu cuenta ha sido actualizada", body);
    }

    public Task SendUserStatusAsync(string to, string name, bool active)
    {
        var body = active
            ? Shell("#22c55e", "CUENTA REACTIVADA", "Tu cuenta fue reactivada",
                $"Hola {Enc(name)}, tu cuenta fue <b>reactivada</b>. Ya puedes iniciar sesión normalmente.", "")
            : Shell("#f59e0b", "CUENTA DESACTIVADA", "Tu cuenta fue desactivada",
                $"Hola {Enc(name)}, tu cuenta fue <b>desactivada</b> temporalmente por el administrador.",
                Note("⏸️ No podrás iniciar sesión mientras tu cuenta esté desactivada. Si crees que es un error, contacta al administrador.", "#f59e0b"));
        return SendAsync(to,
            active ? "[BankOs] Tu cuenta ha sido reactivada" : "[BankOs] Tu cuenta ha sido desactivada", body);
    }

    // ── Account lifecycle ─────────────────────────────────────────────────────

    public Task SendAccountCreatedAsync(string to, string name, string accountNumber, string currency, decimal balance)
    {
        var body = Shell("#22c55e", "CUENTA BANCARIA CREADA", "Tienes una nueva cuenta bancaria",
            $"Hola {Enc(name)}, se creó una nueva cuenta bancaria a tu nombre.",
            Panel("Detalles de la cuenta", "#0463fd", $"""
                {Row("Número de cuenta", Mono(Enc(accountNumber)))}
                {Row("Moneda", currency)}
                {Row("Saldo inicial", $"{balance:N2} {currency}")}
            """));
        return SendAsync(to, "[BankOs] Nueva cuenta bancaria creada", body);
    }

    public Task SendAccountUpdatedAsync(string to, string name, string accountNumber,
        Dictionary<string, (string Old, string New)> changes, bool hasBalance)
    {
        var inner = $"""
            {Panel("Cuenta", "#0463fd", Row("Número de cuenta", Mono(Enc(accountNumber))))}
            {ChangesPanel(changes)}
            {(hasBalance ? Note("💰 Tu cuenta tiene saldo. Este cambio fue realizado por el administrador autorizado. Si no lo reconoces, contáctalo de inmediato.", "#f59e0b") : "")}
            """;
        var body = Shell("#0463fd", "CUENTA MODIFICADA", "Tu cuenta bancaria fue modificada",
            $"Hola {Enc(name)}, tu cuenta bancaria fue modificada.", inner);
        return SendAsync(to, "[BankOs] Tu cuenta bancaria fue modificada", body);
    }

    public Task SendAccountStatusAsync(string to, string name, string accountNumber, bool active, decimal balance, string currency)
    {
        string inner;
        if (active)
            inner = Panel("Cuenta", "#22c55e", Row("Número de cuenta", Mono(Enc(accountNumber))));
        else
            inner = Panel("Cuenta", "#f59e0b", Row("Número de cuenta", Mono(Enc(accountNumber)))) +
                    (balance > 0
                        ? Note($"💰 Aviso: tu cuenta tiene un saldo de <b>{balance:N2} {currency}</b>. Los fondos están seguros. Contacta al administrador para más información.", "#f59e0b")
                        : Note("No podrás realizar transacciones con esta cuenta mientras esté desactivada.", "#f59e0b"));

        var body = active
            ? Shell("#22c55e", "CUENTA REACTIVADA", "Tu cuenta bancaria fue reactivada",
                $"Hola {Enc(name)}, tu cuenta bancaria <b>{Enc(accountNumber)}</b> fue reactivada y está operativa.", inner)
            : Shell("#f59e0b", "CUENTA DESACTIVADA", "Tu cuenta bancaria fue desactivada",
                $"Hola {Enc(name)}, tu cuenta bancaria <b>{Enc(accountNumber)}</b> fue desactivada temporalmente.", inner);
        return SendAsync(to,
            active ? "[BankOs] Tu cuenta bancaria fue reactivada" : "[BankOs] Tu cuenta bancaria fue desactivada", body);
    }

    // ── Certificate delivery (with PDF attachment) ────────────────────────────

    public async Task SendCertificateAsync(string to, string name, string tenantName, byte[] pdf, string fileName)
    {
        var body = Shell("#7c12fd", "CERTIFICADO", "Tu certificado bancario",
            $"Hola {Enc(name)}, adjuntamos tu certificado bancario de <b>{Enc(tenantName)}</b>, solicitado desde el portal.",
            Note("Este documento fue generado y enviado porque alguien con tus credenciales lo solicitó. Si no fuiste tú, cambia tu contraseña y contacta al banco.", "#7c12fd"));

        if (string.IsNullOrWhiteSpace(to)) return;
        if (!Enabled)
        {
            _logger.LogInformation("[Email disabled] Certificate to {to}", to);
            return;
        }
        try
        {
            using var client = BuildClient();
            using var msg = new MailMessage { From = new MailAddress(From, FromName), Subject = $"[BankOs] Tu certificado bancario · {tenantName}", Body = body, IsBodyHtml = true };
            msg.To.Add(to);
            var ms = new MemoryStream(pdf);
            msg.Attachments.Add(new Attachment(ms, fileName, MediaTypeNames.Application.Pdf));
            await client.SendMailAsync(msg);
            _logger.LogInformation("Certificate emailed to {to}", to);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to email certificate to {to}", to);
        }
    }

    // ── PQRS response (the dead API used to send this) ────────────────────────

    public Task SendPqrsResponseAsync(string to, string name, string type, string subject, string response)
    {
        var typeLabel = type switch
        {
            "queja" => "Queja",
            "reclamo" => "Reclamo",
            "sugerencia" => "Sugerencia",
            _ => "Petición",
        };
        var safeResponse = Enc(response).Replace("\n", "<br>");
        var body = Shell("#0463fd", "RESPUESTA A TU PQRS", "Tu solicitud fue respondida",
            $"Hola {Enc(name)}, el equipo del banco respondió tu {typeLabel.ToLowerInvariant()}.",
            $"""
            {Panel("Tu solicitud", "#7c12fd", $"""
                {Row("Tipo", typeLabel)}
                {Row("Asunto", Enc(subject))}
            """)}
            {Panel("Respuesta del banco", "#22c55e",
                $"<div style=\"color:#0f172a;font-size:14px;line-height:1.7\">{safeResponse}</div>")}
            """);
        return SendAsync(to, $"[BankOs] Respuesta a tu PQRS · {subject}", body);
    }

    // ── Transport ──────────────────────────────────────────────────────────────

    private async Task SendAsync(string to, string subject, string html)
    {
        if (string.IsNullOrWhiteSpace(to)) return;
        if (!Enabled)
        {
            _logger.LogInformation("[Email disabled] Would send to {to}: {subject}", to, subject);
            return;
        }
        try
        {
            using var client = BuildClient();
            using var msg = new MailMessage { From = new MailAddress(From, FromName), Subject = subject, Body = html, IsBodyHtml = true };
            msg.To.Add(to);
            await client.SendMailAsync(msg);
            _logger.LogInformation("Email sent to {to}: {subject}", to, subject);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send email to {to}", to);
        }
    }

    // ── Building blocks (BankOs brand) ────────────────────────────────────────

    private string ChangesPanel(Dictionary<string, (string Old, string New)> changes)
    {
        if (changes.Count == 0) return "";
        var rows = string.Concat(changes.Select(c => $"""
            <tr>
              <td style="padding:8px 0;color:#64748b;font-size:13px;width:40%">{Enc(c.Key)}</td>
              <td style="padding:8px 0;font-size:13px">
                <span style="color:#ef4444;text-decoration:line-through">{Enc(c.Value.Old)}</span>
                &nbsp;→&nbsp;
                <span style="color:#16a34a;font-weight:600">{Enc(c.Value.New)}</span>
              </td>
            </tr>
            """));
        return Panel("Cambios realizados", "#0463fd",
            $"<table style='width:100%;border-collapse:collapse'>{rows}</table>");
    }

    private string Shell(string accent, string badge, string heading, string intro, string inner) => $$"""
        <!DOCTYPE html>
        <html lang="es"><head><meta charset="UTF-8"><meta name="viewport" content="width=device-width,initial-scale=1"></head>
        <body style="margin:0;padding:32px 16px;background:#eef1f8;font-family:'Segoe UI',-apple-system,BlinkMacSystemFont,Roboto,Helvetica,Arial,sans-serif">
          <table role="presentation" width="100%" cellpadding="0" cellspacing="0"><tr><td align="center">
            <table role="presentation" width="600" cellpadding="0" cellspacing="0" style="max-width:600px;width:100%">
              <tr><td style="background:#0c1f6e;background:linear-gradient(135deg,#0a1652 0%,#0c1f6e 55%,#221a8c 100%);border-radius:18px 18px 0 0;padding:34px 40px 0">
                <div style="font-size:25px;font-weight:800;letter-spacing:-0.5px">
                  <span style="color:#ffffff">Bank</span><span style="color:#a855f7">O</span><span style="color:#34d399">s</span>
                </div>
                <div style="color:#9bb4ff;font-size:12px;margin-top:2px">Tu banco, en una sola plataforma.</div>
                <table role="presentation" cellpadding="0" cellspacing="0" style="margin:18px 0 0"><tr>
                  <td style="background:rgba(255,255,255,0.14);border:1px solid rgba(255,255,255,0.2);border-radius:50px;padding:5px 14px;color:#ffffff;font-size:11px;font-weight:700;letter-spacing:1px">{{badge}}</td>
                </tr></table>
                <div style="height:32px"></div>
              </td></tr>
              <tr><td style="height:5px;background:{{accent}};background:linear-gradient(90deg,#0c1f6e,#7c12fd 42%,#0463fd 62%,#00a8e8 80%,#22c55e)"></td></tr>
              <tr><td style="background:#ffffff;border-radius:0 0 18px 18px;padding:36px 40px;box-shadow:0 16px 48px rgba(12,31,110,0.10)">
                <h1 style="color:#0f172a;font-size:21px;font-weight:800;margin:0 0 10px">{{heading}}</h1>
                <p style="color:#475569;font-size:14px;line-height:1.6;margin:0 0 8px">{{intro}}</p>
                {{inner}}
                <hr style="border:none;border-top:1px solid #e8edf6;margin:28px 0 16px">
                <p style="color:#94a3b8;font-size:11px;line-height:1.6;margin:0">
                  BankOs · Banca digital multi-tenant · <a href="{{Portal}}" style="color:#0463fd;text-decoration:none">Abrir portal</a><br>
                  Mensaje automático. Si no reconoces esta actividad, contacta a <a href="mailto:{{Support}}" style="color:#0463fd;text-decoration:none">{{Support}}</a>.
                </p>
              </td></tr>
            </table>
          </td></tr></table>
        </body></html>
        """;

    private static string Panel(string title, string color, string content) => $"""
        <div style="background:#f6f8fc;border:1px solid #e8edf6;border-left:4px solid {color};border-radius:12px;padding:18px 20px;margin:20px 0">
          <div style="color:#475569;font-size:11px;font-weight:700;text-transform:uppercase;letter-spacing:1px;margin:0 0 12px">{title}</div>
          {content}
        </div>
        """;

    private static string Row(string label, string value, string? valueColor = null) => $"""
        <table role="presentation" width="100%" cellpadding="0" cellspacing="0"><tr>
          <td style="padding:4px 0;color:#64748b;font-size:13px;width:42%">{label}</td>
          <td style="padding:4px 0;font-size:13px;font-weight:600;color:{valueColor ?? "#0f172a"}">{value}</td>
        </tr></table>
        """;

    private static string Note(string html, string color) => $"""
        <div style="background:#fffbeb;border:1px solid {color};border-radius:12px;padding:16px 18px;margin:20px 0;color:#78350f;font-size:13px;line-height:1.6">{html}</div>
        """;

    private static string Mono(string v) =>
        $"<span style=\"font-family:'Courier New',monospace;background:#eef2ff;color:#312e81;padding:2px 7px;border-radius:5px\">{v}</span>";
}
