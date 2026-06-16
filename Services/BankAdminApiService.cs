using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BankAdmin.Models;

namespace BankAdmin.Services;

/// <summary>
/// Talks to the BankOS (Laravel) tenant API as a bank administrator.
/// Auth is JWT Bearer + the X-Tenant-ID header on every request. Tokens are per-session,
/// so headers are attached per request (the typed HttpClient instance is shared).
/// </summary>
public class BankAdminApiService
{
    private readonly HttpClient _http;
    private readonly ILogger<BankAdminApiService> _logger;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public BankAdminApiService(HttpClient http, IConfiguration config, ILogger<BankAdminApiService> logger)
    {
        _http = http;
        _logger = logger;
        var baseUrl = config["BankOS:ApiBaseUrl"] ?? "https://bank-os.duckdns.org";
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(60);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    // ── Request plumbing ─────────────────────────────────────────────────────

    private async Task<(JsonElement? Root, string? Error)> SendAsync(
        HttpMethod method, string path, string? token, string? tenantId,
        object? body = null, bool idempotent = false)
    {
        try
        {
            using var req = new HttpRequestMessage(method, path);
            if (!string.IsNullOrEmpty(token))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (!string.IsNullOrEmpty(tenantId))
                req.Headers.Add("X-Tenant-ID", tenantId);
            req.Headers.Add("X-Correlation-ID", Guid.NewGuid().ToString());
            if (idempotent)
                req.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            if (body != null)
                req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            var resp = await _http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
                return (null, ExtractError(json, (int)resp.StatusCode));

            if (string.IsNullOrWhiteSpace(json)) return (null, null);
            using var doc = JsonDocument.Parse(json);
            return (doc.RootElement.Clone(), null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "API call failed: {method} {path}", method, path);
            return (null, $"No se pudo conectar con la API: {ex.Message}");
        }
    }

    private static JsonElement DataOf(JsonElement root) =>
        root.TryGetProperty("data", out var d) ? d : root;

    // ── Auth ─────────────────────────────────────────────────────────────────

    public async Task<(AuthResult? Auth, string? Error)> LoginAsync(string tenantId, string email, string password)
    {
        var (root, err) = await SendAsync(HttpMethod.Post, "api/v1/auth/login", null, tenantId,
            new { email, password });
        if (err != null || root is null) return (null, err ?? "Respuesta vacía del servidor.");

        var data = DataOf(root.Value);
        var token = GetStr(data, "token");
        if (string.IsNullOrEmpty(token)) return (null, "No se recibió un token de acceso.");

        var userEl = data.TryGetProperty("user", out var u) ? u : default;
        var auth = new AuthResult
        {
            Token = token,
            User = new UserModel
            {
                Id = GetStr(userEl, "id"),
                Name = GetStr(userEl, "name"),
                Email = GetStr(userEl, "email"),
                Role = GetStr(userEl, "role", fallback: "cliente"),
            }
        };
        return (auth, null);
    }

    public async Task<string?> ChangePasswordAsync(string token, string tenantId,
        string currentPassword, string newPassword)
    {
        var (_, err) = await SendAsync(HttpMethod.Patch, "api/v1/auth/me/password", token, tenantId,
            new { current_password = currentPassword, password = newPassword, password_confirmation = newPassword });
        return err;
    }

    // ── Banks (public) ─────────────────────────────────────────────────────────

    public async Task<List<BankModel>> GetBanksAsync()
    {
        var (root, err) = await SendAsync(HttpMethod.Get, "api/v1/banks", null, null);
        var result = new List<BankModel>();
        if (err != null || root is null) return result;
        var data = DataOf(root.Value);
        if (data.ValueKind == JsonValueKind.Array)
            foreach (var b in data.EnumerateArray())
                result.Add(new BankModel { Id = GetStr(b, "id"), Name = GetStr(b, "name") });
        return result;
    }

    // ── Users (clients) ────────────────────────────────────────────────────────

    public async Task<(List<UserModel> Users, string? Error)> GetUsersAsync(string token, string tenantId)
    {
        var (root, err) = await SendAsync(HttpMethod.Get, "api/v1/users?per_page=200", token, tenantId);
        var list = new List<UserModel>();
        if (err != null || root is null) return (list, err);
        var data = DataOf(root.Value);
        if (data.ValueKind == JsonValueKind.Array)
            foreach (var u in data.EnumerateArray()) list.Add(ParseUser(u));
        return (list, null);
    }

    public async Task<(UserModel? User, string? Error)> GetUserAsync(string token, string tenantId, string id)
    {
        var (root, err) = await SendAsync(HttpMethod.Get, $"api/v1/users/{id}", token, tenantId);
        if (err != null || root is null) return (null, err);
        return (ParseUser(DataOf(root.Value)), null);
    }

    public async Task<(UserModel? User, string? Error)> CreateUserAsync(string token, string tenantId, CreateClientViewModel vm)
    {
        var (root, err) = await SendAsync(HttpMethod.Post, "api/v1/users", token, tenantId, new
        {
            name = vm.Name,
            email = vm.Email,
            password = vm.Password,
            password_confirmation = vm.Password,
            role = vm.Role,
        });
        if (err != null || root is null) return (null, err);
        return (ParseUser(DataOf(root.Value)), null);
    }

    public async Task<string?> UpdateUserAsync(string token, string tenantId, EditClientViewModel vm)
    {
        var payload = new Dictionary<string, object?>
        {
            ["name"] = vm.Name,
            ["email"] = vm.Email,
            ["role"] = vm.Role,
        };
        if (!string.IsNullOrWhiteSpace(vm.NewPassword))
        {
            payload["password"] = vm.NewPassword;
            payload["password_confirmation"] = vm.NewPassword;
        }
        var (_, err) = await SendAsync(HttpMethod.Put, $"api/v1/users/{vm.Id}", token, tenantId, payload);
        return err;
    }

    /// <summary>Toggles user status; returns the new status (or null + error).</summary>
    public async Task<(string? NewStatus, string? Error)> ToggleUserStatusAsync(string token, string tenantId, string id)
    {
        var (root, err) = await SendAsync(HttpMethod.Patch, $"api/v1/users/{id}/status", token, tenantId);
        if (err != null || root is null) return (null, err);
        return (GetStr(DataOf(root.Value), "status", fallback: "active"), null);
    }

    // ── Accounts ────────────────────────────────────────────────────────────────

    public async Task<(List<AccountModel> Accounts, string? Error)> GetAccountsAsync(string token, string tenantId)
    {
        var (root, err) = await SendAsync(HttpMethod.Get, "api/v1/accounts?per_page=200", token, tenantId);
        var list = new List<AccountModel>();
        if (err != null || root is null) return (list, err);
        var data = DataOf(root.Value);
        if (data.ValueKind == JsonValueKind.Array)
            foreach (var a in data.EnumerateArray()) list.Add(ParseAccount(a));
        return (list, null);
    }

    public async Task<(AccountModel? Account, string? Error)> GetAccountAsync(string token, string tenantId, string id)
    {
        var (root, err) = await SendAsync(HttpMethod.Get, $"api/v1/accounts/{id}", token, tenantId);
        if (err != null || root is null) return (null, err);
        return (ParseAccount(DataOf(root.Value)), null);
    }

    public async Task<(AccountModel? Account, string? Error)> CreateAccountAsync(string token, string tenantId, CreateAccountViewModel vm)
    {
        var (root, err) = await SendAsync(HttpMethod.Post, "api/v1/accounts", token, tenantId, new
        {
            user_id = vm.UserId,
            account_number = vm.AccountNumber,
            currency = vm.Currency,
            initial_balance = vm.InitialBalance,
        });
        if (err != null || root is null) return (null, err);
        return (ParseAccount(DataOf(root.Value)), null);
    }

    public async Task<string?> UpdateAccountAsync(string token, string tenantId, EditAccountViewModel vm)
    {
        var (_, err) = await SendAsync(HttpMethod.Put, $"api/v1/accounts/{vm.Id}", token, tenantId, new
        {
            currency = vm.Currency,
            balance = vm.Balance,
            status = vm.Status,
        });
        return err;
    }

    /// <summary>Sets account status (active|inactive|blocked) with an optional reason.</summary>
    public async Task<string?> UpdateAccountStatusAsync(string token, string tenantId, string id, string status, string? reason = null)
    {
        var (_, err) = await SendAsync(HttpMethod.Patch, $"api/v1/accounts/{id}/status", token, tenantId,
            new { status, reason });
        return err;
    }

    // ── Transactions ──────────────────────────────────────────────────────────

    public async Task<(List<TransactionModel> Tx, string? Error)> GetTransactionsAsync(
        string token, string tenantId, string? type = null, string? accountId = null, int perPage = 100)
    {
        var qs = new List<string> { $"per_page={perPage}" };
        if (!string.IsNullOrWhiteSpace(type)) qs.Add($"type={Uri.EscapeDataString(type)}");
        if (!string.IsNullOrWhiteSpace(accountId)) qs.Add($"account_id={Uri.EscapeDataString(accountId)}");
        var (root, err) = await SendAsync(HttpMethod.Get, $"api/v1/transactions?{string.Join("&", qs)}", token, tenantId);
        var list = new List<TransactionModel>();
        if (err != null || root is null) return (list, err);
        var data = DataOf(root.Value);
        if (data.ValueKind == JsonValueKind.Array)
            foreach (var t in data.EnumerateArray()) list.Add(ParseTransaction(t));
        return (list, null);
    }

    public async Task<(TransactionModel? Tx, string? Error)> GetTransactionAsync(string token, string tenantId, string id)
    {
        var (root, err) = await SendAsync(HttpMethod.Get, $"api/v1/transactions/{id}", token, tenantId);
        if (err != null || root is null) return (null, err);
        return (ParseTransaction(DataOf(root.Value)), null);
    }

    public async Task<(TransactionModel? Tx, string? Error)> DepositAsync(string token, string tenantId, DepositViewModel vm)
    {
        var (root, err) = await SendAsync(HttpMethod.Post, "api/v1/transactions/deposit", token, tenantId, new
        {
            account_id = vm.AccountId,
            amount = vm.Amount,
            currency = vm.Currency,
            description = vm.Description,
        }, idempotent: true);
        if (err != null || root is null) return (null, err);
        return (ParseTransaction(DataOf(root.Value)), null);
    }

    public async Task<(TransactionModel? Tx, string? Error)> TransferAsync(string token, string tenantId, TransferViewModel vm)
    {
        var (root, err) = await SendAsync(HttpMethod.Post, "api/v1/transactions/transfer", token, tenantId, new
        {
            source_account_id = vm.SourceAccountId,
            destination_account_id = vm.DestinationAccountId,
            amount = vm.Amount,
            currency = vm.Currency,
            description = vm.Description,
        }, idempotent: true);
        if (err != null || root is null) return (null, err);
        return (ParseTransaction(DataOf(root.Value)), null);
    }

    // ── PQRS ──────────────────────────────────────────────────────────────────

    public async Task<(List<PqrsModel> Items, string? Error)> GetPqrsAsync(string token, string tenantId)
    {
        var (root, err) = await SendAsync(HttpMethod.Get, "api/v1/pqrs?per_page=200", token, tenantId);
        var list = new List<PqrsModel>();
        if (err != null || root is null) return (list, err);
        var data = DataOf(root.Value);
        if (data.ValueKind == JsonValueKind.Array)
            foreach (var p in data.EnumerateArray()) list.Add(ParsePqrs(p));
        return (list, null);
    }

    public async Task<string?> RespondPqrsAsync(string token, string tenantId, string id, string response)
    {
        var (_, err) = await SendAsync(HttpMethod.Patch, $"api/v1/pqrs/{id}/respond", token, tenantId,
            new { response });
        return err;
    }

    public async Task<string?> UpdatePqrsStatusAsync(string token, string tenantId, string id, string status)
    {
        var (_, err) = await SendAsync(HttpMethod.Patch, $"api/v1/pqrs/{id}/status", token, tenantId,
            new { status });
        return err;
    }

    // ── Audit ────────────────────────────────────────────────────────────────

    public async Task<(List<AuditLogModel> Logs, string? Error)> GetAuditLogsAsync(string token, string tenantId, int perPage = 100)
    {
        var (root, err) = await SendAsync(HttpMethod.Get, $"api/v1/audit/logs?per_page={perPage}", token, tenantId);
        var list = new List<AuditLogModel>();
        if (err != null || root is null) return (list, err);
        var data = DataOf(root.Value);
        if (data.ValueKind == JsonValueKind.Array)
            foreach (var l in data.EnumerateArray()) list.Add(ParseAudit(l));
        return (list, null);
    }

    // ── Config ───────────────────────────────────────────────────────────────

    public async Task<(TenantConfigModel? Config, string? Error)> GetConfigAsync(string token, string tenantId)
    {
        var (root, err) = await SendAsync(HttpMethod.Get, "api/v1/config", token, tenantId);
        if (err != null || root is null) return (null, err);
        return (ParseConfig(DataOf(root.Value)), null);
    }

    public async Task<string?> UpdateConfigAsync(string token, string tenantId, ConfigViewModel vm)
    {
        var (_, err) = await SendAsync(HttpMethod.Patch, $"api/v1/tenants/{tenantId}/config", token, tenantId, new
        {
            max_transaction_amount = vm.MaxTransactionAmount,
            transfer_fee_type = vm.TransferFeeType,
            transfer_fee_value = vm.TransferFeeValue,
            webhook_url = string.IsNullOrWhiteSpace(vm.WebhookUrl) ? null : vm.WebhookUrl,
        });
        return err;
    }

    // ── Parsers ──────────────────────────────────────────────────────────────

    private static UserModel ParseUser(JsonElement e) => new()
    {
        Id = GetStr(e, "id"),
        Name = GetStr(e, "name"),
        Email = GetStr(e, "email"),
        Role = GetStr(e, "role", fallback: "cliente"),
        Status = GetStr(e, "status", fallback: "active"),
        CreatedAt = GetDate(e, "created_at"),
    };

    private static AccountModel ParseAccount(JsonElement e) => new()
    {
        Id = GetStr(e, "id"),
        AccountNumber = GetStr(e, "account_number"),
        UserId = GetStr(e, "user_id"),
        Balance = GetDec(e, "balance"),
        Currency = GetStr(e, "currency", fallback: "COP"),
        Status = GetStr(e, "status", fallback: "active"),
        CreatedAt = GetDate(e, "created_at"),
        UpdatedAt = GetDate(e, "updated_at"),
    };

    private static TransactionModel ParseTransaction(JsonElement e) => new()
    {
        Id = GetStr(e, "id"),
        Type = GetStr(e, "type"),
        Status = GetStr(e, "status"),
        AccountId = NullIfEmpty(GetStr(e, "account_id")),
        DestinationAccountId = NullIfEmpty(GetStr(e, "destination_account_id")),
        Amount = GetDec(e, "amount"),
        ConvertedAmount = GetDecN(e, "converted_amount"),
        Currency = GetStr(e, "currency"),
        DestinationCurrency = NullIfEmpty(GetStr(e, "destination_currency")),
        ExchangeRate = GetDecN(e, "exchange_rate"),
        Fee = GetDec(e, "fee"),
        FeeType = NullIfEmpty(GetStr(e, "fee_type")),
        BalanceAfter = GetDec(e, "balance_after"),
        Description = NullIfEmpty(GetStr(e, "description")),
        CreatedAt = GetDate(e, "created_at"),
    };

    private static PqrsModel ParsePqrs(JsonElement e)
    {
        var p = new PqrsModel
        {
            Id = GetStr(e, "id"),
            UserId = GetStr(e, "user_id"),
            Type = GetStr(e, "type", fallback: "pregunta"),
            Subject = GetStr(e, "subject"),
            Message = GetStr(e, "message"),
            Status = GetStr(e, "status", fallback: "pendiente"),
            AdminResponse = NullIfEmpty(GetStr(e, "admin_response")),
            CreatedAt = GetDate(e, "created_at"),
        };
        if (e.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object)
        {
            p.UserName = GetStr(u, "name");
            p.UserEmail = GetStr(u, "email");
        }
        return p;
    }

    private static AuditLogModel ParseAudit(JsonElement e) => new()
    {
        Action = GetStr(e, "action"),
        PerformedByUserId = NullIfEmpty(GetStr(e, "performed_by_user_id")),
        TargetType = NullIfEmpty(GetStr(e, "target_type")),
        TargetId = NullIfEmpty(GetStr(e, "target_id")),
        IpAddress = NullIfEmpty(GetStr(e, "ip_address")),
        CreatedAt = GetDate(e, "created_at"),
    };

    private static TenantConfigModel ParseConfig(JsonElement e)
    {
        var c = new TenantConfigModel
        {
            Currency = GetStr(e, "currency", fallback: "COP"),
            MaxTransactionAmount = GetDec(e, "max_transaction_amount"),
            TransferFeeType = GetStr(e, "transfer_fee_type", fallback: "percentage"),
            TransferFeeValue = GetDec(e, "transfer_fee_value"),
            WebhookUrl = NullIfEmpty(GetStr(e, "webhook_url")),
        };
        if (e.TryGetProperty("exchange_rates", out var rates) && rates.ValueKind == JsonValueKind.Object)
        {
            c.ExchangeRates = new();
            foreach (var r in rates.EnumerateObject())
                if (r.Value.ValueKind == JsonValueKind.Number) c.ExchangeRates[r.Name] = r.Value.GetDecimal();
        }
        return c;
    }

    // ── Error extraction (Spanish) ───────────────────────────────────────────

    private static string ExtractError(string json, int status)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err) && err.TryGetProperty("message", out var m))
                return m.GetString() ?? $"HTTP {status}";
            if (root.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Object)
            {
                var parts = new List<string>();
                foreach (var f in errs.EnumerateObject())
                    if (f.Value.ValueKind == JsonValueKind.Array)
                        foreach (var v in f.Value.EnumerateArray()) parts.Add(v.GetString() ?? "");
                if (parts.Count > 0) return string.Join(" · ", parts);
            }
            if (root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
                return msg.GetString() ?? $"HTTP {status}";
        }
        catch { /* not json */ }
        return status switch
        {
            401 => "Credenciales inválidas o sesión expirada (401).",
            403 => "No tienes permiso para esta acción (403).",
            404 => "Recurso no encontrado (404).",
            409 => "Operación en conflicto, intenta de nuevo (409).",
            422 => "Datos inválidos (422).",
            _ => $"Error del servidor (HTTP {status})."
        };
    }

    // ── JSON helpers ─────────────────────────────────────────────────────────

    private static string GetStr(JsonElement el, string name, string fallback = "")
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v))
        {
            if (v.ValueKind == JsonValueKind.String) return v.GetString() ?? fallback;
            if (v.ValueKind == JsonValueKind.Number) return v.ToString();
        }
        return fallback;
    }

    private static decimal GetDec(JsonElement el, string name)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v))
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
            if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), out var ds)) return ds;
        }
        return 0;
    }

    private static decimal? GetDecN(JsonElement el, string name)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v))
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
            if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), out var ds)) return ds;
        }
        return null;
    }

    private static DateTime? GetDate(JsonElement el, string name)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) &&
            v.ValueKind == JsonValueKind.String && DateTime.TryParse(v.GetString(), out var dt))
            return dt;
        return null;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
