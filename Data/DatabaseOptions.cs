using Npgsql;

namespace BankAdmin.Data;

/// <summary>
/// PostgreSQL connection settings for the BankOS multi-tenant database (the same DB the
/// Laravel app uses, hosted on the VPS). The app talks to the database directly instead of
/// going through the Laravel REST API.
///
/// Architecture (stancl/tenancy):
///   • Central / "super admin" database → tables: tenants, domains, tenant_configs, …
///     (its name is <see cref="CentralDb"/>, e.g. "bankos_central")
///   • One database per bank (tenant) → tables: users, accounts, transactions, audit_logs, pqrs, …
///     Tenant database name = <see cref="TenantDbPrefix"/> + tenantId
///     e.g. tenant id "test-bank"  →  database "tenant_test-bank".
///
/// The property names match the `Database` section keys in appsettings.json exactly
/// (Host, Port, User, Password, CentralDb, TenantDbPrefix, DomainSuffix).
/// </summary>
public sealed class DatabaseOptions
{
    /// <summary>VPS hostname or IP where PostgreSQL is reachable.</summary>
    public string Host { get; set; } = "127.0.0.1";

    public string Port { get; set; } = "5432";

    /// <summary>PostgreSQL user (maps to the connection-string "Username").</summary>
    public string User { get; set; } = "postgres";

    public string Password { get; set; } = "postgres";

    /// <summary>Name of the central (super admin) database that holds `tenants`, `tenant_configs`, …</summary>
    public string CentralDb { get; set; } = "bankos_central";

    /// <summary>Prefix for per-tenant database names. Tenant DB = TenantDbPrefix + tenantId.</summary>
    public string TenantDbPrefix { get; set; } = "tenant_";

    /// <summary>
    /// Public domain suffix of each bank (e.g. ".bank.os"). This is the tenant's web domain,
    /// NOT part of the database name (the DB is just <see cref="TenantDbPrefix"/> + tenantId).
    /// Kept here for completeness / display.
    /// </summary>
    public string DomainSuffix { get; set; } = ".bank.os";

    // ── Optional (not in the basic config block; sensible defaults) ──────────────

    /// <summary>SSL mode: Disable | Allow | Prefer | Require | VerifyCA | VerifyFull.</summary>
    public string SslMode { get; set; } = "Prefer";

    /// <summary>Accept self-signed certificates (typical on a VPS). Keep true unless you manage a CA.</summary>
    public bool TrustServerCertificate { get; set; } = true;

    public int ConnectTimeoutSeconds { get; set; } = 15;

    public int CommandTimeoutSeconds { get; set; } = 30;

    // ── Derived values ───────────────────────────────────────────────────────────

    /// <summary>Database name for a specific bank, e.g. "tenant_test-bank".</summary>
    public string TenantDatabaseName(string tenantId) => TenantDbPrefix + tenantId;

    /// <summary>Public domain for a specific bank, e.g. "test-bank.bank.os" (display only).</summary>
    public string TenantDomain(string tenantId) => tenantId + DomainSuffix;

    /// <summary>Connection string for the central (super admin) database.</summary>
    public string CentralConnectionString() => Build(CentralDb);

    /// <summary>Connection string for a specific bank's (tenant) database.</summary>
    public string TenantConnectionString(string tenantId) => Build(TenantDatabaseName(tenantId));

    private string Build(string database)
    {
        var csb = new NpgsqlConnectionStringBuilder
        {
            Host = Host,
            Port = int.TryParse(Port, out var p) ? p : 5432,
            Database = database,
            Username = User,
            Password = Password,
            SslMode = ParseSsl(SslMode),
            TrustServerCertificate = TrustServerCertificate,
            Timeout = ConnectTimeoutSeconds,
            CommandTimeout = CommandTimeoutSeconds,
            Pooling = true,
            // Keep the pool modest; an admin panel is low-concurrency.
            MinPoolSize = 0,
            MaxPoolSize = 20,
            // Match the Laravel tenant connection (search_path = public).
            SearchPath = "public",
            ApplicationName = "BankOSAdmin",
        };
        return csb.ConnectionString;
    }

    private static SslMode ParseSsl(string value) =>
        Enum.TryParse<SslMode>(value, ignoreCase: true, out var mode) ? mode : Npgsql.SslMode.Prefer;
}
