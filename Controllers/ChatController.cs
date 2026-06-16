using System.Text;
using System.Text.Json;
using BankAdmin.Models;
using BankAdmin.Services;
using Microsoft.AspNetCore.Mvc;

namespace BankAdmin.Controllers;

public class ChatController : AdminControllerBase
{
    private readonly BankAdminApiService _api;
    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ChatController> _logger;

    public ChatController(BankAdminApiService api, IConfiguration config, IHttpClientFactory httpFactory, ILogger<ChatController> logger)
    {
        _api = api; _config = config; _httpFactory = httpFactory; _logger = logger;
    }

    public IActionResult Index()
    {
        if (NotAuthed(out var r)) return r;
        return View();
    }

    public record ChatRequest(string Message, List<ChatMessage>? History);

    [HttpPost]
    public async Task<IActionResult> Send([FromBody] ChatRequest req)
    {
        if (!IsAuthed) return Json(new { reply = "Tu sesión expiró. Vuelve a iniciar sesión." });
        if (string.IsNullOrWhiteSpace(req.Message)) return Json(new { reply = "Escribe una pregunta." });

        var apiKey = _config["OpenAI:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.StartsWith("sk-tu"))
            return Json(new { reply = "El asistente no está configurado. Agrega tu clave de OpenAI en appsettings.json (OpenAI:ApiKey)." });

        var context = await BuildContextAsync();

        var messages = new List<object>
        {
            new { role = "system", content =
                $"Eres el analista de datos del banco «{TenantName}» dentro de la plataforma BankOs. " +
                "Respondes en español, de forma clara y concisa, SOLO sobre los datos de este banco: clientes, cuentas y transacciones. " +
                "Puedes calcular totales, promedios, distribuciones, detectar cuentas con saldos altos o movimientos inusuales y sugerir acciones. " +
                "Si te preguntan algo fuera de este ámbito, indícalo amablemente. No inventes datos que no estén en el contexto.\n\n" +
                "DATOS ACTUALES DEL BANCO:\n" + context }
        };
        foreach (var m in (req.History ?? new()).TakeLast(10))
            messages.Add(new { role = m.Role == "assistant" ? "assistant" : "user", content = m.Content });
        messages.Add(new { role = "user", content = req.Message });

        try
        {
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(60);
            using var httpReq = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
            httpReq.Headers.Add("Authorization", $"Bearer {apiKey}");
            var payload = new
            {
                model = _config["OpenAI:Model"] ?? "gpt-4o-mini",
                messages,
                temperature = 0.3,
                max_tokens = 600,
            };
            httpReq.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var resp = await http.SendAsync(httpReq);
            var json = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("OpenAI error {code}: {body}", resp.StatusCode, json);
                return Json(new { reply = "El asistente no está disponible en este momento. Intenta más tarde." });
            }

            using var doc = JsonDocument.Parse(json);
            var reply = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            return Json(new { reply = reply ?? "Sin respuesta." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chat error");
            return Json(new { reply = "Ocurrió un error al consultar al asistente." });
        }
    }

    /// <summary>Compact, privacy-conscious snapshot of the bank for the model.</summary>
    private async Task<string> BuildContextAsync()
    {
        var (users, _) = await _api.GetUsersAsync(Token!, TenantId!);
        var (accounts, _) = await _api.GetAccountsAsync(Token!, TenantId!);
        var (tx, _) = await _api.GetTransactionsAsync(Token!, TenantId!, perPage: 200);
        var (config, _) = await _api.GetConfigAsync(Token!, TenantId!);
        var cur = config?.Currency ?? "COP";

        var sb = new StringBuilder();
        sb.AppendLine($"Moneda base: {cur}. Límite por transacción: {config?.MaxTransactionAmount:N0}. " +
                      $"Comisión: {(config?.TransferFeeType == "percentage" ? $"{config?.TransferFeeValue}%" : $"{config?.TransferFeeValue:N0} fija")}.");
        sb.AppendLine($"Clientes: {users.Count(u => !u.IsAdmin)} (activos {users.Count(u => u.IsActive && !u.IsAdmin)}). Administradores: {users.Count(u => u.IsAdmin)}.");
        sb.AppendLine($"Cuentas: {accounts.Count} (activas {accounts.Count(a => a.IsActive)}, inactivas {accounts.Count(a => !a.IsActive)}).");

        foreach (var g in accounts.Where(a => a.IsActive).GroupBy(a => a.Currency))
            sb.AppendLine($"  Saldo total {g.Key}: {g.Sum(a => a.Balance):N2} (en {g.Count()} cuentas).");

        sb.AppendLine($"Transacciones (últimas {tx.Count}):");
        foreach (var g in tx.GroupBy(t => t.Type))
            sb.AppendLine($"  {g.Key}: {g.Count()} operaciones, monto total {g.Sum(t => t.Amount):N2}, comisiones {g.Sum(t => t.Fee):N2}.");
        sb.AppendLine($"  Exitosas: {tx.Count(t => t.Status == "success")}, fallidas: {tx.Count(t => t.Status == "failed")}.");

        var top = accounts.OrderByDescending(a => a.Balance).Take(5)
            .Select(a => $"{a.AccountNumber} ({a.Currency}): {a.Balance:N2}");
        sb.AppendLine("Top 5 cuentas por saldo: " + string.Join("; ", top));

        sb.AppendLine("Movimientos recientes:");
        foreach (var t in tx.Take(15))
            sb.AppendLine($"  {t.CreatedAt:yyyy-MM-dd HH:mm} · {t.Type} · {t.Amount:N2} {t.Currency} · {t.Status}");

        return sb.ToString();
    }
}
