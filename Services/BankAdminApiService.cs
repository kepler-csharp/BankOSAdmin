using System.Text.Json;
using BankAdmin.Data;
using BankAdmin.Models;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BankAdmin.Services;

/// <summary>
/// Data access for the bank-administrator panel.
///
/// IMPORTANT: the Laravel REST API is bypassed. This service talks DIRECTLY to the same
/// PostgreSQL database the Laravel app uses (multi-tenant, stancl/tenancy):
///   • central DB  → tenants, tenant_configs            (see <see cref="DatabaseOptions.CentralDb"/>)
///   • tenant DBs  → users, accounts, transactions, audit_logs, pqrs   (one DB per bank)
///
/// The public method signatures are kept identical to the previous API client, so no
/// controller/view changes are required. Business rules (deposits, transfers, fees, exchange
/// rates, audit logging, PQRS) mirror the Laravel services/controllers exactly.
/// </summary>
public class BankAdminApiService
{
    private readonly DatabaseOptions _db;
    private readonly ILogger<BankAdminApiService> _logger;

    static BankAdminApiService()
    {
        // The schema mixes `timestamp` and `timestamptz`; legacy mode lets Npgsql read/write both
        // without throwing on DateTimeKind. Must be set before any connection is opened.
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        // Map snake_case columns (account_number) to PascalCase properties (AccountNumber).
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public BankAdminApiService(IOptions<DatabaseOptions> db, ILogger<BankAdminApiService> logger)
    {
        _db = db.Value;
        _logger = logger;
    }

    // ── Connection helpers ─────────────────────────────────────────────────────

    private async Task<NpgsqlConnection> OpenTenantAsync(string tenantId)
    {
        var conn = new NpgsqlConnection(_db.TenantConnectionString(tenantId));
        await conn.OpenAsync();
        return conn;
    }

    private async Task<NpgsqlConnection> OpenCentralAsync()
    {
        var conn = new NpgsqlConnection(_db.CentralConnectionString());
        await conn.OpenAsync();
        return conn;
    }

    /// <summary>Turns any DB exception into a friendly Spanish message for the UI.</summary>
    private string Friendly(Exception ex, string context)
    {
        _logger.LogError(ex, "DB operation failed: {context}", context);
        return ex switch
        {
            PostgresException { SqlState: "3D000" } =>
                "La base de datos de este banco no existe en el servidor. Verifica el prefijo de las bases (tenant_) y el identificador del banco.",
            PostgresException { SqlState: "28P01" } =>
                "Usuario o contraseña de la base de datos incorrectos. Revisa la configuración (sección Database).",
            PostgresException { SqlState: "28000" } =>
                "Acceso a la base de datos rechazado. Revisa credenciales/SSL y que el VPS permita la conexión.",
            PostgresException { SqlState: "23505" } =>
                "Ya existe un registro con ese valor único (por ejemplo, correo o número de cuenta).",
            PostgresException pg => $"Error de base de datos ({pg.SqlState}): {pg.MessageText}",
            NpgsqlException =>
                "No se pudo conectar con la base de datos. Verifica host, puerto, credenciales y el acceso de red al VPS.",
            _ => $"Error inesperado: {ex.Message}",
        };
    }

    // ── Auth ─────────────────────────────────────────────────────────────────

    public async Task<(AuthResult? Auth, string? Error)> LoginAsync(string tenantId, string email, string password)
    {
        try
        {
            await using var conn = await OpenTenantAsync(tenantId);
            const string sql = """
                SELECT id::text AS "Id", name AS "Name", email AS "Email",
                       password AS "Password", role AS "Role", status AS "Status"
                FROM users
                WHERE lower(email) = lower(@email)
                LIMIT 1
                """;
            var row = await conn.QuerySingleOrDefaultAsync<LoginRow>(sql, new { email });

            if (row is null || !VerifyPassword(password, row.Password))
                return (null, "Credenciales inválidas. Verifica el correo y la contraseña.");

            var auth = new AuthResult
            {
                Token = SessionToken.Create(row.Id, row.Role, tenantId),
                User = new UserModel
                {
                    Id = row.Id,
                    Name = row.Name,
                    Email = row.Email,
                    Role = string.IsNullOrEmpty(row.Role) ? "cliente" : row.Role,
                    Status = string.IsNullOrEmpty(row.Status) ? "active" : row.Status,
                }
            };
            return (auth, null);
        }
        catch (Exception ex)
        {
            return (null, Friendly(ex, "login"));
        }
    }

    public async Task<string?> ChangePasswordAsync(string token, string tenantId,
        string currentPassword, string newPassword)
    {
        var actor = SessionToken.TryParse(token)?.UserId;
        if (string.IsNullOrEmpty(actor)) return "Tu sesión expiró. Vuelve a iniciar sesión.";

        try
        {
            await using var conn = await OpenTenantAsync(tenantId);
            var currentHash = await conn.ExecuteScalarAsync<string?>(
                "SELECT password FROM users WHERE id = @id::uuid", new { id = actor });

            if (currentHash is null) return "No se encontró tu usuario.";
            if (!VerifyPassword(currentPassword, currentHash))
                return "La contraseña actual no es correcta.";

            await conn.ExecuteAsync(
                "UPDATE users SET password = @pwd, updated_at = now() WHERE id = @id::uuid",
                new { pwd = HashPassword(newPassword), id = actor });
            return null;
        }
        catch (Exception ex)
        {
            return Friendly(ex, "change-password");
        }
    }

    // ── Banks (public) ─────────────────────────────────────────────────────────

    public async Task<List<BankModel>> GetBanksAsync()
    {
        try
        {
            await using var conn = await OpenCentralAsync();
            // tenants.id is a string (e.g. "test-bank"), not a UUID.
            var banks = await conn.QueryAsync<BankModel>(
                "SELECT id AS \"Id\", COALESCE(name, id) AS \"Name\" FROM tenants WHERE status = 'active' ORDER BY name");
            return banks.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetBanks failed");
            return new List<BankModel>();
        }
    }

    // ── Connectivity probe (used by /Home/DbCheck) ──────────────────────────────

    public async Task<DbHealthReport> CheckHealthAsync()
    {
        var report = new DbHealthReport { Host = _db.Host, CentralDatabase = _db.CentralDb };

        List<BankModel> tenants;
        try
        {
            await using var central = await OpenCentralAsync();
            tenants = (await central.QueryAsync<BankModel>(
                "SELECT id AS \"Id\", COALESCE(name, id) AS \"Name\" FROM tenants ORDER BY name")).ToList();
            report.CentralOk = true;
        }
        catch (Exception ex)
        {
            report.CentralError = Friendly(ex, "health-central");
            return report;
        }

        foreach (var t in tenants)
        {
            var th = new TenantHealth
            {
                Id = t.Id,
                Name = t.Name,
                Database = _db.TenantDatabaseName(t.Id),
            };
            try
            {
                await using var conn = await OpenTenantAsync(t.Id);
                th.Users = await conn.ExecuteScalarAsync<int>("SELECT count(*)::int FROM users");
                th.Ok = true;
            }
            catch (Exception ex)
            {
                th.Error = Friendly(ex, "health-tenant");
            }
            report.Tenants.Add(th);
        }

        return report;
    }

    // ── Users (clients) ────────────────────────────────────────────────────────

    public async Task<(List<UserModel> Users, string? Error)> GetUsersAsync(string token, string tenantId)
    {
        try
        {
            await using var conn = await OpenTenantAsync(tenantId);
            const string sql = """
                SELECT id::text AS id, name, email, role, status, created_at
                FROM users
                ORDER BY created_at DESC NULLS LAST, name ASC
                """;
            var users = await conn.QueryAsync<UserModel>(sql);
            return (users.ToList(), null);
        }
        catch (Exception ex)
        {
            return (new List<UserModel>(), Friendly(ex, "get-users"));
        }
    }

    public async Task<(UserModel? User, string? Error)> GetUserAsync(string token, string tenantId, string id)
    {
        try
        {
            await using var conn = await OpenTenantAsync(tenantId);
            const string sql = """
                SELECT id::text AS id, name, email, role, status, created_at
                FROM users WHERE id = @id::uuid LIMIT 1
                """;
            var user = await conn.QuerySingleOrDefaultAsync<UserModel>(sql, new { id });
            return user is null ? (null, "Cliente no encontrado.") : (user, null);
        }
        catch (Exception ex)
        {
            return (null, Friendly(ex, "get-user"));
        }
    }

    public async Task<(UserModel? User, string? Error)> CreateUserAsync(string token, string tenantId, CreateClientViewModel vm)
    {
        var actor = SessionToken.TryParse(token)?.UserId;
        try
        {
            await using var conn = await OpenTenantAsync(tenantId);

            var exists = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM users WHERE lower(email) = lower(@email))", new { email = vm.Email });
            if (exists) return (null, "El correo ya está registrado en este banco.");

            var id = Guid.NewGuid().ToString();
            var role = string.IsNullOrWhiteSpace(vm.Role) ? "cliente" : vm.Role;

            await conn.ExecuteAsync("""
                INSERT INTO users (id, name, email, password, role, status, created_at, updated_at)
                VALUES (@id::uuid, @name, @email, @password, @role, 'active', now(), now())
                """,
                new { id, name = vm.Name, email = vm.Email, password = HashPassword(vm.Password), role });

            await WriteAuditAsync(conn, null, actor, "user.created", "user", id,
                previous: null, next: new { email = vm.Email, role });

            var user = new UserModel
            {
                Id = id,
                Name = vm.Name,
                Email = vm.Email,
                Role = role,
                Status = "active",
                CreatedAt = DateTime.Now,
            };
            return (user, null);
        }
        catch (Exception ex)
        {
            return (null, Friendly(ex, "create-user"));
        }
    }

    public async Task<string?> UpdateUserAsync(string token, string tenantId, EditClientViewModel vm)
    {
        var actor = SessionToken.TryParse(token)?.UserId;
        try
        {
            await using var conn = await OpenTenantAsync(tenantId);

            var old = await conn.QuerySingleOrDefaultAsync<UserModel>(
                "SELECT id::text AS id, name, email, role, status FROM users WHERE id = @id::uuid", new { id = vm.Id });
            if (old is null) return "Cliente no encontrado.";

            // Email uniqueness (excluding self) when it changes.
            if (!string.Equals(old.Email, vm.Email, StringComparison.OrdinalIgnoreCase))
            {
                var taken = await conn.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS(SELECT 1 FROM users WHERE lower(email) = lower(@email) AND id <> @id::uuid)",
                    new { email = vm.Email, id = vm.Id });
                if (taken) return "Ese correo ya está en uso por otro usuario.";
            }

            var sets = new List<string> { "name = @name", "email = @email", "role = @role", "updated_at = now()" };
            object parameters;
            var role = string.IsNullOrWhiteSpace(vm.Role) ? "cliente" : vm.Role;

            if (!string.IsNullOrWhiteSpace(vm.NewPassword))
            {
                sets.Add("password = @password");
                parameters = new { name = vm.Name, email = vm.Email, role, password = HashPassword(vm.NewPassword!), id = vm.Id };
            }
            else
            {
                parameters = new { name = vm.Name, email = vm.Email, role, id = vm.Id };
            }

            await conn.ExecuteAsync($"UPDATE users SET {string.Join(", ", sets)} WHERE id = @id::uuid", parameters);

            await WriteAuditAsync(conn, null, actor, "user.updated", "user", vm.Id,
                previous: new { name = old.Name, email = old.Email, role = old.Role },
                next: new { name = vm.Name, email = vm.Email, role });

            return null;
        }
        catch (Exception ex)
        {
            return Friendly(ex, "update-user");
        }
    }

    public async Task<(string? NewStatus, string? Error)> ToggleUserStatusAsync(string token, string tenantId, string id)
    {
        var actor = SessionToken.TryParse(token)?.UserId;
        try
        {
            await using var conn = await OpenTenantAsync(tenantId);

            var current = await conn.ExecuteScalarAsync<string?>(
                "SELECT COALESCE(status, 'active') FROM users WHERE id = @id::uuid", new { id });
            if (current is null) return (null, "Cliente no encontrado.");

            var newStatus = string.Equals(current, "active", StringComparison.OrdinalIgnoreCase) ? "inactive" : "active";

            await conn.ExecuteAsync(
                "UPDATE users SET status = @status, updated_at = now() WHERE id = @id::uuid",
                new { status = newStatus, id });

            await WriteAuditAsync(conn, null, actor, $"user.{newStatus}", "user", id,
                previous: new { status = current }, next: new { status = newStatus });

            return (newStatus, null);
        }
        catch (Exception ex)
        {
            return (null, Friendly(ex, "toggle-user-status"));
        }
    }

    // ── Accounts ────────────────────────────────────────────────────────────────

    public async Task<(List<AccountModel> Accounts, string? Error)> GetAccountsAsync(string token, string tenantId)
    {
        try
        {
            await using var conn = await OpenTenantAsync(tenantId);

            // Self-service certificate flow authenticates as the client; scope to their accounts.
            var session = SessionToken.TryParse(token);
            var scopeToUser = session is { IsAdmin: false };

            var sql = """
                SELECT id::text AS id, account_number, user_id::text AS user_id, balance,
                       currency, status, created_at, updated_at
                FROM accounts
                WHERE deleted_at IS NULL
                """ + (scopeToUser ? " AND user_id = @uid::uuid" : "") + """
                ORDER BY created_at DESC NULLS LAST
                """;

            var parameters = new DynamicParameters();
            if (scopeToUser) parameters.Add("uid", session!.UserId);

            var accounts = await conn.QueryAsync<AccountModel>(sql, parameters);
            return (accounts.ToList(), null);
        }
        catch (Exception ex)
        {
            return (new List<AccountModel>(), Friendly(ex, "get-accounts"));
        }
    }

    public async Task<(AccountModel? Account, string? Error)> GetAccountAsync(string token, string tenantId, string id)
    {
        try
        {
            await using var conn = await OpenTenantAsync(tenantId);
            const string sql = """
                SELECT id::text AS id, account_number, user_id::text AS user_id, balance,
                       currency, status, created_at, updated_at
                FROM accounts WHERE id = @id::uuid AND deleted_at IS NULL LIMIT 1
                """;
            var account = await conn.QuerySingleOrDefaultAsync<AccountModel>(sql, new { id });
            return account is null ? (null, "Cuenta no encontrada.") : (account, null);
        }
        catch (Exception ex)
        {
            return (null, Friendly(ex, "get-account"));
        }
    }

    public async Task<(AccountModel? Account, string? Error)> CreateAccountAsync(string token, string tenantId, CreateAccountViewModel vm)
    {
        var actor = SessionToken.TryParse(token)?.UserId;
        try
        {
            await using var conn = await OpenTenantAsync(tenantId);

            var dup = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM accounts WHERE account_number = @num)", new { num = vm.AccountNumber });
            if (dup) return (null, "Ya existe una cuenta con ese número.");

            var ownerExists = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM users WHERE id = @uid::uuid)", new { uid = vm.UserId });
            if (!ownerExists) return (null, "El titular seleccionado no existe.");

            var id = Guid.NewGuid().ToString();
            var currency = string.IsNullOrWhiteSpace(vm.Currency) ? "COP" : vm.Currency;

            await conn.ExecuteAsync("""
                INSERT INTO accounts (id, account_number, user_id, balance, currency, status, created_at, updated_at)
                VALUES (@id::uuid, @num, @uid::uuid, @balance, @currency, 'active', now(), now())
                """,
                new { id, num = vm.AccountNumber, uid = vm.UserId, balance = vm.InitialBalance, currency });

            var account = new AccountModel
            {
                Id = id,
                AccountNumber = vm.AccountNumber,
                UserId = vm.UserId,
                Balance = vm.InitialBalance,
                Currency = currency,
                Status = "active",
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now,
            };

            await WriteAuditAsync(conn, null, actor, "account.created", "account", id,
                previous: null, next: AccountState(account));

            return (account, null);
        }
        catch (Exception ex)
        {
            return (null, Friendly(ex, "create-account"));
        }
    }

    public async Task<string?> UpdateAccountAsync(string token, string tenantId, EditAccountViewModel vm)
    {
        var actor = SessionToken.TryParse(token)?.UserId;
        try
        {
            await using var conn = await OpenTenantAsync(tenantId);

            var old = await conn.QuerySingleOrDefaultAsync<AccountModel>("""
                SELECT id::text AS id, account_number, user_id::text AS user_id, balance, currency, status
                FROM accounts WHERE id = @id::uuid AND deleted_at IS NULL
                """, new { id = vm.Id });
            if (old is null) return "Cuenta no encontrada.";

            var status = string.Equals(vm.Status, "active", StringComparison.OrdinalIgnoreCase) ? "active" : "inactive";
            var currency = string.IsNullOrWhiteSpace(vm.Currency) ? old.Currency : vm.Currency;

            await conn.ExecuteAsync("""
                UPDATE accounts SET currency = @currency, balance = @balance, status = @status, updated_at = now()
                WHERE id = @id::uuid
                """,
                new { currency, balance = vm.Balance, status, id = vm.Id });

            await WriteAuditAsync(conn, null, actor, "account.updated", "account", vm.Id,
                previous: new { currency = old.Currency, balance = old.Balance, status = old.Status },
                next: new { currency, balance = vm.Balance, status });

            return null;
        }
        catch (Exception ex)
        {
            return Friendly(ex, "update-account");
        }
    }

    public async Task<string?> UpdateAccountStatusAsync(string token, string tenantId, string id, string status, string? reason = null)
    {
        var actor = SessionToken.TryParse(token)?.UserId;
        try
        {
            await using var conn = await OpenTenantAsync(tenantId);

            var previous = await conn.ExecuteScalarAsync<string?>(
                "SELECT status FROM accounts WHERE id = @id::uuid AND deleted_at IS NULL", new { id });
            if (previous is null) return "Cuenta no encontrada.";

            var normalized = status switch
            {
                "active" => "active",
                "inactive" => "inactive",
                "blocked" => "blocked",
                _ => "inactive",
            };

            await conn.ExecuteAsync(
                "UPDATE accounts SET status = @status, updated_at = now() WHERE id = @id::uuid",
                new { status = normalized, id });

            await WriteAuditAsync(conn, null, actor, "account.status_updated", "account", id,
                previous: new { status = previous }, next: new { status = normalized }, reason: reason);

            return null;
        }
        catch (Exception ex)
        {
            return Friendly(ex, "update-account-status");
        }
    }

    // ── Transactions ──────────────────────────────────────────────────────────

    public async Task<(List<TransactionModel> Tx, string? Error)> GetTransactionsAsync(
        string token, string tenantId, string? type = null, string? accountId = null, int perPage = 100)
    {
        try
        {
            await using var conn = await OpenTenantAsync(tenantId);

            var where = new List<string>();
            if (!string.IsNullOrWhiteSpace(type)) where.Add("type = @type");
            if (!string.IsNullOrWhiteSpace(accountId))
                where.Add("(account_id = @acc::uuid OR destination_account_id = @acc::uuid)");

            var sql = """
                SELECT id::text AS id, type, status,
                       account_id::text AS account_id,
                       destination_account_id::text AS destination_account_id,
                       amount, converted_amount, currency, destination_currency,
                       exchange_rate, fee, fee_type, balance_after, description, created_at
                FROM transactions
                """
                + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
                + " ORDER BY created_at DESC LIMIT @limit";

            var parameters = new DynamicParameters();
            parameters.Add("limit", perPage);
            if (!string.IsNullOrWhiteSpace(type)) parameters.Add("type", type);
            if (!string.IsNullOrWhiteSpace(accountId)) parameters.Add("acc", accountId);

            var tx = await conn.QueryAsync<TransactionModel>(sql, parameters);
            return (tx.ToList(), null);
        }
        catch (Exception ex)
        {
            return (new List<TransactionModel>(), Friendly(ex, "get-transactions"));
        }
    }

    public async Task<(TransactionModel? Tx, string? Error)> GetTransactionAsync(string token, string tenantId, string id)
    {
        try
        {
            await using var conn = await OpenTenantAsync(tenantId);
            const string sql = """
                SELECT id::text AS id, type, status,
                       account_id::text AS account_id,
                       destination_account_id::text AS destination_account_id,
                       amount, converted_amount, currency, destination_currency,
                       exchange_rate, fee, fee_type, balance_after, description, created_at
                FROM transactions WHERE id = @id::uuid LIMIT 1
                """;
            var tx = await conn.QuerySingleOrDefaultAsync<TransactionModel>(sql, new { id });
            return tx is null ? (null, "Transacción no encontrada.") : (tx, null);
        }
        catch (Exception ex)
        {
            return (null, Friendly(ex, "get-transaction"));
        }
    }

    public async Task<(TransactionModel? Tx, string? Error)> DepositAsync(string token, string tenantId, DepositViewModel vm)
    {
        try
        {
            await using var conn = await OpenTenantAsync(tenantId);
            await using var tx = await conn.BeginTransactionAsync();
            try
            {
                var account = await ResolveAndLockAsync(conn, tx, vm.AccountId);
                if (account is null)
                {
                    await tx.RollbackAsync();
                    return (null, "La cuenta indicada no existe o está inactiva.");
                }

                var newBalance = await conn.ExecuteScalarAsync<decimal>(
                    "UPDATE accounts SET balance = balance + @amount, updated_at = now() WHERE id = @id::uuid RETURNING balance",
                    new { amount = vm.Amount, id = account.Id }, tx);

                var txId = Guid.NewGuid().ToString();
                var created = await conn.ExecuteScalarAsync<DateTime>("""
                    INSERT INTO transactions
                        (id, type, status, account_id, amount, converted_amount, currency, fee, fee_type,
                         balance_after, description, idempotency_key, correlation_id, meta, created_at, updated_at)
                    VALUES
                        (@id::uuid, 'deposit', 'success', @acc::uuid, @amount, NULL, @currency, 0, NULL,
                         @balanceAfter, @desc, @idem, @corr, NULL, now(), now())
                    RETURNING created_at
                    """,
                    new
                    {
                        id = txId,
                        acc = account.Id,
                        amount = vm.Amount,
                        currency = account.Currency,
                        balanceAfter = newBalance,
                        desc = string.IsNullOrWhiteSpace(vm.Description) ? null : vm.Description,
                        idem = Guid.NewGuid().ToString(),
                        corr = Guid.NewGuid().ToString(),
                    }, tx);

                await tx.CommitAsync();

                return (new TransactionModel
                {
                    Id = txId,
                    Type = "deposit",
                    Status = "success",
                    AccountId = account.Id,
                    Amount = vm.Amount,
                    Currency = account.Currency,
                    Fee = 0,
                    BalanceAfter = newBalance,
                    Description = vm.Description,
                    CreatedAt = created,
                }, null);
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        }
        catch (Exception ex)
        {
            return (null, Friendly(ex, "deposit"));
        }
    }

    public async Task<(TransactionModel? Tx, string? Error)> TransferAsync(string token, string tenantId, TransferViewModel vm)
    {
        try
        {
            var config = await LoadTenantConfigAsync(tenantId);
            if (config is null) return (null, "No se encontró la configuración del banco.");

            await using var conn = await OpenTenantAsync(tenantId);
            await using var tx = await conn.BeginTransactionAsync();
            try
            {
                var sourceId = await ResolveAccountIdAsync(conn, tx, vm.SourceAccountId);
                var destId = await ResolveAccountIdAsync(conn, tx, vm.DestinationAccountId);
                if (sourceId is null) { await tx.RollbackAsync(); return (null, "La cuenta origen no existe o está inactiva."); }
                if (destId is null) { await tx.RollbackAsync(); return (null, "La cuenta destino no existe o está inactiva."); }
                if (sourceId == destId) { await tx.RollbackAsync(); return (null, "La cuenta origen y destino no pueden ser la misma."); }

                // Lock both rows, always in the same (sorted) order, to avoid deadlocks.
                var firstId = string.CompareOrdinal(sourceId, destId) <= 0 ? sourceId : destId;
                var secondId = firstId == sourceId ? destId : sourceId;
                var lockedFirst = await LockAccountByIdAsync(conn, tx, firstId);
                var lockedSecond = await LockAccountByIdAsync(conn, tx, secondId);

                var source = lockedFirst?.Id == sourceId ? lockedFirst : lockedSecond;
                var dest = lockedFirst?.Id == destId ? lockedFirst : lockedSecond;
                if (source is null) { await tx.RollbackAsync(); return (null, "La cuenta origen no existe o está inactiva."); }
                if (dest is null) { await tx.RollbackAsync(); return (null, "La cuenta destino no existe o está inactiva."); }

                var amount = vm.Amount;

                if (amount > config.MaxTransactionAmount)
                {
                    await tx.RollbackAsync();
                    return (null, $"El monto supera el límite por transacción del banco ({config.MaxTransactionAmount:N2}).");
                }

                var fee = CalculateFee(amount, config);
                var totalDebit = amount + fee;

                if (source.Balance < totalDebit)
                {
                    await tx.RollbackAsync();
                    return (null, $"Fondos insuficientes. Saldo disponible: {source.Balance:N2} {source.Currency}; se requieren {totalDebit:N2} (incluye comisión {fee:N2}).");
                }

                decimal convertedAmount = amount;
                decimal? exchangeRate = null;

                if (!string.Equals(source.Currency, dest.Currency, StringComparison.OrdinalIgnoreCase))
                {
                    var (converted, rate, error) = Convert(amount, source.Currency, dest.Currency, config.ExchangeRates);
                    if (error != null) { await tx.RollbackAsync(); return (null, error); }
                    convertedAmount = converted;
                    exchangeRate = rate;
                }

                var sourceBalanceAfter = await conn.ExecuteScalarAsync<decimal>(
                    "UPDATE accounts SET balance = balance - @debit, updated_at = now() WHERE id = @id::uuid RETURNING balance",
                    new { debit = totalDebit, id = source.Id }, tx);

                var destBalanceAfter = await conn.ExecuteScalarAsync<decimal>(
                    "UPDATE accounts SET balance = balance + @credit, updated_at = now() WHERE id = @id::uuid RETURNING balance",
                    new { credit = convertedAmount, id = dest.Id }, tx);

                var txId = Guid.NewGuid().ToString();
                var meta = JsonSerializer.Serialize(new Dictionary<string, decimal> { ["destination_balance_after"] = destBalanceAfter });

                var created = await conn.ExecuteScalarAsync<DateTime>("""
                    INSERT INTO transactions
                        (id, type, status, account_id, destination_account_id, amount, converted_amount,
                         currency, destination_currency, exchange_rate, fee, fee_type, balance_after,
                         description, idempotency_key, correlation_id, meta, created_at, updated_at)
                    VALUES
                        (@id::uuid, 'transfer', 'success', @src::uuid, @dst::uuid, @amount, @converted,
                         @currency, @destCurrency, @rate, @fee, @feeType, @balanceAfter,
                         @desc, @idem, @corr, @meta::json, now(), now())
                    RETURNING created_at
                    """,
                    new
                    {
                        id = txId,
                        src = source.Id,
                        dst = dest.Id,
                        amount,
                        converted = convertedAmount,
                        currency = source.Currency,
                        destCurrency = dest.Currency,
                        rate = exchangeRate,
                        fee,
                        feeType = config.TransferFeeType,
                        balanceAfter = sourceBalanceAfter,
                        desc = string.IsNullOrWhiteSpace(vm.Description) ? null : vm.Description,
                        idem = Guid.NewGuid().ToString(),
                        corr = Guid.NewGuid().ToString(),
                        meta,
                    }, tx);

                await tx.CommitAsync();

                return (new TransactionModel
                {
                    Id = txId,
                    Type = "transfer",
                    Status = "success",
                    AccountId = source.Id,
                    DestinationAccountId = dest.Id,
                    Amount = amount,
                    ConvertedAmount = convertedAmount,
                    Currency = source.Currency,
                    DestinationCurrency = dest.Currency,
                    ExchangeRate = exchangeRate,
                    Fee = fee,
                    FeeType = config.TransferFeeType,
                    BalanceAfter = sourceBalanceAfter,
                    Description = vm.Description,
                    CreatedAt = created,
                }, null);
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        }
        catch (Exception ex)
        {
            return (null, Friendly(ex, "transfer"));
        }
    }

    // ── PQRS ──────────────────────────────────────────────────────────────────

    public async Task<(List<PqrsModel> Items, string? Error)> GetPqrsAsync(string token, string tenantId)
    {
        try
        {
            await using var conn = await OpenTenantAsync(tenantId);
            const string sql = """
                SELECT p.id::text AS id, p.user_id::text AS user_id, p.type, p.subject, p.message,
                       p.status, p.admin_response, p.created_at,
                       u.name AS user_name, u.email AS user_email
                FROM pqrs p
                LEFT JOIN users u ON u.id = p.user_id
                ORDER BY p.created_at DESC NULLS LAST
                """;
            var items = await conn.QueryAsync<PqrsModel>(sql);
            return (items.ToList(), null);
        }
        catch (Exception ex)
        {
            return (new List<PqrsModel>(), Friendly(ex, "get-pqrs"));
        }
    }

    public async Task<string?> RespondPqrsAsync(string token, string tenantId, string id, string response)
    {
        try
        {
            await using var conn = await OpenTenantAsync(tenantId);
            var affected = await conn.ExecuteAsync("""
                UPDATE pqrs SET admin_response = @response, status = 'resuelto', updated_at = now()
                WHERE id = @id::uuid
                """, new { response, id });
            return affected == 0 ? "PQRS no encontrada." : null;
        }
        catch (Exception ex)
        {
            return Friendly(ex, "respond-pqrs");
        }
    }

    public async Task<string?> UpdatePqrsStatusAsync(string token, string tenantId, string id, string status)
    {
        var normalized = status switch
        {
            "pendiente" => "pendiente",
            "en_revision" => "en_revision",
            "resuelto" => "resuelto",
            _ => null,
        };
        if (normalized is null) return "Estado de PQRS inválido.";

        try
        {
            await using var conn = await OpenTenantAsync(tenantId);
            var affected = await conn.ExecuteAsync(
                "UPDATE pqrs SET status = @status, updated_at = now() WHERE id = @id::uuid",
                new { status = normalized, id });
            return affected == 0 ? "PQRS no encontrada." : null;
        }
        catch (Exception ex)
        {
            return Friendly(ex, "update-pqrs-status");
        }
    }

    // ── Audit ────────────────────────────────────────────────────────────────

    public async Task<(List<AuditLogModel> Logs, string? Error)> GetAuditLogsAsync(string token, string tenantId, int perPage = 100)
    {
        try
        {
            await using var conn = await OpenTenantAsync(tenantId);
            const string sql = """
                SELECT action,
                       performed_by_user_id::text AS performed_by_user_id,
                       target_type, target_id, ip_address, created_at
                FROM audit_logs
                ORDER BY created_at DESC
                LIMIT @limit
                """;
            var logs = await conn.QueryAsync<AuditLogModel>(sql, new { limit = perPage });
            return (logs.ToList(), null);
        }
        catch (Exception ex)
        {
            return (new List<AuditLogModel>(), Friendly(ex, "get-audit-logs"));
        }
    }

    // ── Config ───────────────────────────────────────────────────────────────

    public async Task<(TenantConfigModel? Config, string? Error)> GetConfigAsync(string token, string tenantId)
    {
        try
        {
            var config = await LoadTenantConfigAsync(tenantId);
            return config is null ? (null, "No se encontró la configuración del banco.") : (config, null);
        }
        catch (Exception ex)
        {
            return (null, Friendly(ex, "get-config"));
        }
    }

    public async Task<string?> UpdateConfigAsync(string token, string tenantId, ConfigViewModel vm)
    {
        var actor = SessionToken.TryParse(token)?.UserId;
        try
        {
            await using var central = await OpenCentralAsync();

            var configId = await central.ExecuteScalarAsync<string?>(
                "SELECT id::text FROM tenant_configs WHERE tenant_id = @t", new { t = tenantId });
            if (configId is null) return "No se encontró la configuración del banco.";

            await central.ExecuteAsync("""
                UPDATE tenant_configs
                SET max_transaction_amount = @max,
                    transfer_fee_type = @feeType,
                    transfer_fee_value = @feeValue,
                    webhook_url = @webhook,
                    updated_at = now()
                WHERE tenant_id = @t
                """,
                new
                {
                    max = vm.MaxTransactionAmount,
                    feeType = vm.TransferFeeType,
                    feeValue = vm.TransferFeeValue,
                    webhook = string.IsNullOrWhiteSpace(vm.WebhookUrl) ? null : vm.WebhookUrl,
                    t = tenantId,
                });

            // Audit log lives in the tenant DB.
            try
            {
                await using var tenant = await OpenTenantAsync(tenantId);
                await WriteAuditAsync(tenant, null, actor, "config.updated", "config", configId,
                    previous: null,
                    next: new
                    {
                        max_transaction_amount = vm.MaxTransactionAmount,
                        transfer_fee_type = vm.TransferFeeType,
                        transfer_fee_value = vm.TransferFeeValue,
                        webhook_url = vm.WebhookUrl,
                    });
            }
            catch (Exception auditEx)
            {
                _logger.LogWarning(auditEx, "config.updated audit log failed");
            }

            return null;
        }
        catch (Exception ex)
        {
            return Friendly(ex, "update-config");
        }
    }

    // ── Internal: config loading ───────────────────────────────────────────────

    private async Task<TenantConfigModel?> LoadTenantConfigAsync(string tenantId)
    {
        await using var conn = await OpenCentralAsync();
        const string sql = """
            SELECT currency AS "Currency",
                   max_transaction_amount AS "MaxTransactionAmount",
                   transfer_fee_type AS "TransferFeeType",
                   transfer_fee_value AS "TransferFeeValue",
                   webhook_url AS "WebhookUrl",
                   exchange_rates::text AS "ExchangeRatesJson"
            FROM tenant_configs WHERE tenant_id = @t LIMIT 1
            """;
        var row = await conn.QuerySingleOrDefaultAsync<ConfigRow>(sql, new { t = tenantId });
        if (row is null) return null;

        return new TenantConfigModel
        {
            Currency = string.IsNullOrEmpty(row.Currency) ? "COP" : row.Currency,
            MaxTransactionAmount = row.MaxTransactionAmount,
            TransferFeeType = string.IsNullOrEmpty(row.TransferFeeType) ? "percentage" : row.TransferFeeType,
            TransferFeeValue = row.TransferFeeValue,
            WebhookUrl = string.IsNullOrWhiteSpace(row.WebhookUrl) ? null : row.WebhookUrl,
            ExchangeRates = ParseRates(row.ExchangeRatesJson),
        };
    }

    private static Dictionary<string, decimal>? ParseRates(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var dict = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetDecimal(out var d))
                    dict[prop.Name] = d;
                else if (prop.Value.ValueKind == JsonValueKind.String && decimal.TryParse(prop.Value.GetString(), out var ds))
                    dict[prop.Name] = ds;
            }
            return dict;
        }
        catch
        {
            return null;
        }
    }

    // ── Internal: money rules (mirror Laravel FeeService / ExchangeRateService) ──

    private static decimal CalculateFee(decimal amount, TenantConfigModel config) => config.TransferFeeType switch
    {
        "percentage" => Math.Round(amount * (config.TransferFeeValue / 100m), 2, MidpointRounding.AwayFromZero),
        "fixed" => config.TransferFeeValue,
        _ => 0m,
    };

    /// <summary>Returns (converted, rate, error). Rates are "how many base units per 1 unit of currency".</summary>
    private static (decimal Converted, decimal Rate, string? Error) Convert(
        decimal amount, string from, string to, Dictionary<string, decimal>? rates)
    {
        if (rates is null
            || !rates.TryGetValue(from, out var fromRate)
            || !rates.TryGetValue(to, out var toRate)
            || toRate == 0)
        {
            return (0, 0, $"No hay tasa de cambio configurada para {from} → {to}.");
        }

        var amountInBase = amount * fromRate;
        var converted = amountInBase / toRate;
        var rate = fromRate / toRate;
        return (Math.Round(converted, 2, MidpointRounding.AwayFromZero),
                Math.Round(rate, 8, MidpointRounding.AwayFromZero), null);
    }

    // ── Internal: account resolution + locking (mirror TransactionService) ──────

    private static async Task<AccountLock?> ResolveAndLockAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string idOrNumber)
    {
        const string sql = """
            SELECT id::text AS "Id", account_number AS "AccountNumber", user_id::text AS "UserId",
                   balance AS "Balance", currency AS "Currency", status AS "Status"
            FROM accounts
            WHERE status = 'active' AND deleted_at IS NULL AND (id::text = @v OR account_number = @v)
            LIMIT 1
            FOR UPDATE
            """;
        return await conn.QuerySingleOrDefaultAsync<AccountLock>(sql, new { v = idOrNumber }, tx);
    }

    private static async Task<string?> ResolveAccountIdAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string idOrNumber)
    {
        const string sql = """
            SELECT id::text FROM accounts
            WHERE status = 'active' AND deleted_at IS NULL AND (id::text = @v OR account_number = @v)
            LIMIT 1
            """;
        return await conn.ExecuteScalarAsync<string?>(sql, new { v = idOrNumber }, tx);
    }

    private static async Task<AccountLock?> LockAccountByIdAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string id)
    {
        const string sql = """
            SELECT id::text AS "Id", account_number AS "AccountNumber", user_id::text AS "UserId",
                   balance AS "Balance", currency AS "Currency", status AS "Status"
            FROM accounts
            WHERE status = 'active' AND deleted_at IS NULL AND id = @id::uuid
            LIMIT 1
            FOR UPDATE
            """;
        return await conn.QuerySingleOrDefaultAsync<AccountLock>(sql, new { id }, tx);
    }

    // ── Internal: audit logging ─────────────────────────────────────────────────

    private async Task WriteAuditAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string? performedBy,
        string action, string targetType, string targetId, object? previous, object? next, string? reason = null)
    {
        // performed_by_user_id is NOT NULL in the schema; skip (best-effort) if we don't know the actor.
        if (string.IsNullOrEmpty(performedBy))
        {
            _logger.LogWarning("Skipping audit '{action}' — no acting user in session token.", action);
            return;
        }

        try
        {
            const string sql = """
                INSERT INTO audit_logs
                    (id, action, performed_by_user_id, target_type, target_id,
                     previous_state, new_state, reason, ip_address, correlation_id, created_at)
                VALUES
                    (@id::uuid, @action, @actor::uuid, @targetType, @targetId,
                     @previous::json, @next::json, @reason, NULL, @corr, now())
                """;
            await conn.ExecuteAsync(sql, new
            {
                id = Guid.NewGuid().ToString(),
                action,
                actor = performedBy,
                targetType,
                targetId,
                previous = previous is null ? null : JsonSerializer.Serialize(previous),
                next = next is null ? null : JsonSerializer.Serialize(next),
                reason,
                corr = Guid.NewGuid().ToString(),
            }, tx);
        }
        catch (Exception ex)
        {
            // Never let an audit failure break the user-facing action.
            _logger.LogWarning(ex, "Audit log insert failed for action {action}", action);
        }
    }

    private static object AccountState(AccountModel a) => new
    {
        id = a.Id,
        account_number = a.AccountNumber,
        user_id = a.UserId,
        balance = a.Balance,
        currency = a.Currency,
        status = a.Status,
    };

    // ── Internal: bcrypt (Laravel-compatible) ───────────────────────────────────

    private static bool VerifyPassword(string plain, string? hash)
    {
        if (string.IsNullOrEmpty(hash)) return false;
        try { return BCrypt.Net.BCrypt.Verify(plain, hash); }
        catch { return false; }
    }

    private static string HashPassword(string plain) => BCrypt.Net.BCrypt.HashPassword(plain, 12);

    // ── Internal DTOs ────────────────────────────────────────────────────────────

    private sealed class LoginRow
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
        public string Password { get; set; } = "";
        public string Role { get; set; } = "cliente";
        public string Status { get; set; } = "active";
    }

    private sealed class AccountLock
    {
        public string Id { get; set; } = "";
        public string AccountNumber { get; set; } = "";
        public string UserId { get; set; } = "";
        public decimal Balance { get; set; }
        public string Currency { get; set; } = "";
        public string Status { get; set; } = "";
    }

    private sealed class ConfigRow
    {
        public string Currency { get; set; } = "COP";
        public decimal MaxTransactionAmount { get; set; }
        public string TransferFeeType { get; set; } = "percentage";
        public decimal TransferFeeValue { get; set; }
        public string? WebhookUrl { get; set; }
        public string? ExchangeRatesJson { get; set; }
    }
}
