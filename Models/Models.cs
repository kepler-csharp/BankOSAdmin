namespace BankAdmin.Models;

// ── Core API entities ─────────────────────────────────────────────────────────

/// <summary>A bank (tenant) as exposed by the public /banks endpoint.</summary>
public class BankModel
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>A user (client or administrator) inside the tenant.</summary>
public class UserModel
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Role { get; set; } = "cliente"; // administrador | cliente
    public string Status { get; set; } = "active"; // active | inactive
    public DateTime? CreatedAt { get; set; }

    public bool IsActive => string.Equals(Status, "active", StringComparison.OrdinalIgnoreCase);
    public bool IsAdmin => string.Equals(Role, "administrador", StringComparison.OrdinalIgnoreCase);
    public int AccountsCount { get; set; }
}

/// <summary>A bank account belonging to a user.</summary>
public class AccountModel
{
    public string Id { get; set; } = "";
    public string AccountNumber { get; set; } = "";
    public string UserId { get; set; } = "";
    public decimal Balance { get; set; }
    public string Currency { get; set; } = "COP";
    public string Status { get; set; } = "active"; // active | inactive | blocked
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    // Resolved client-side for display/notification
    public string? OwnerName { get; set; }
    public string? OwnerEmail { get; set; }

    public bool IsActive => string.Equals(Status, "active", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A financial transaction (deposit / withdrawal / transfer).</summary>
public class TransactionModel
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";   // deposit | withdrawal | transfer
    public string Status { get; set; } = ""; // success | failed | pending
    public string? AccountId { get; set; }
    public string? DestinationAccountId { get; set; }
    public decimal Amount { get; set; }
    public decimal? ConvertedAmount { get; set; }
    public string Currency { get; set; } = "";
    public string? DestinationCurrency { get; set; }
    public decimal? ExchangeRate { get; set; }
    public decimal Fee { get; set; }
    public string? FeeType { get; set; }
    public decimal BalanceAfter { get; set; }
    public string? Description { get; set; }
    public DateTime? CreatedAt { get; set; }
}

/// <summary>A PQRS ticket (pregunta/queja/reclamo/sugerencia).</summary>
public class PqrsModel
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Type { get; set; } = "pregunta";
    public string Subject { get; set; } = "";
    public string Message { get; set; } = "";
    public string Status { get; set; } = "pendiente"; // pendiente | en_revision | resuelto
    public string? AdminResponse { get; set; }
    public DateTime? CreatedAt { get; set; }
    public string? UserName { get; set; }
    public string? UserEmail { get; set; }
}

/// <summary>An audit log entry.</summary>
public class AuditLogModel
{
    public string Action { get; set; } = "";
    public string? PerformedByUserId { get; set; }
    public string? PerformedByName { get; set; }
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }
    public string? IpAddress { get; set; }
    public DateTime? CreatedAt { get; set; }
}

/// <summary>Tenant financial configuration.</summary>
public class TenantConfigModel
{
    public string Currency { get; set; } = "COP";
    public decimal MaxTransactionAmount { get; set; }
    public string TransferFeeType { get; set; } = "percentage";
    public decimal TransferFeeValue { get; set; }
    public string? WebhookUrl { get; set; }
    public Dictionary<string, decimal>? ExchangeRates { get; set; }
}

/// <summary>Aggregated KPIs for the overview.</summary>
public class DashboardStats
{
    public int Users { get; set; }
    public int ActiveUsers { get; set; }
    public int Accounts { get; set; }
    public int ActiveAccounts { get; set; }
    public int Transactions { get; set; }
    public decimal TotalBalance { get; set; }
    public string Currency { get; set; } = "COP";
}

// ── Auth ───────────────────────────────────────────────────────────────────────

public class AuthResult
{
    public string Token { get; set; } = "";
    public UserModel User { get; set; } = new();
}

// ── View models ────────────────────────────────────────────────────────────────

public class LoginViewModel
{
    public string TenantId { get; set; } = "";
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
}

public class CreateClientViewModel
{
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
    public string Role { get; set; } = "cliente"; // cliente | administrador
}

public class EditClientViewModel
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Role { get; set; } = "cliente";
    public string? NewPassword { get; set; }
}

public class CreateAccountViewModel
{
    public string AccountNumber { get; set; } = "";
    public string Currency { get; set; } = "COP";
    public string UserId { get; set; } = "";
    public decimal InitialBalance { get; set; }
}

public class EditAccountViewModel
{
    public string Id { get; set; } = "";
    public string AccountNumber { get; set; } = "";
    public string Currency { get; set; } = "COP";
    public decimal Balance { get; set; }
    public string Status { get; set; } = "active";
    public decimal CurrentBalance { get; set; }
    public string? OwnerEmail { get; set; }
}

public class DepositViewModel
{
    public string AccountId { get; set; } = "";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "COP";
    public string? Description { get; set; }
}

public class TransferViewModel
{
    public string SourceAccountId { get; set; } = "";
    public string DestinationAccountId { get; set; } = "";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "COP";
    public string? Description { get; set; }
}

public class PqrsRespondViewModel
{
    public string Id { get; set; } = "";
    public string Response { get; set; } = "";
}

public class ConfigViewModel
{
    public decimal MaxTransactionAmount { get; set; }
    public string TransferFeeType { get; set; } = "percentage";
    public decimal TransferFeeValue { get; set; }
    public string? WebhookUrl { get; set; }
}

public class ChangePasswordViewModel
{
    public string CurrentPassword { get; set; } = "";
    public string NewPassword { get; set; } = "";
    public string ConfirmPassword { get; set; } = "";
}

public class CertificateRequestViewModel
{
    public string TenantId { get; set; } = "";
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
}

// ── Chatbot ──────────────────────────────────────────────────────────────────

public class ChatMessage
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
}
