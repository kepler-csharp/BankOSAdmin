namespace BankAdmin.Data;

/// <summary>Result of the database connectivity probe exposed at /Home/DbCheck.</summary>
public sealed class DbHealthReport
{
    public string Host { get; set; } = "";
    public string CentralDatabase { get; set; } = "";
    public bool CentralOk { get; set; }
    public string? CentralError { get; set; }
    public List<TenantHealth> Tenants { get; set; } = new();
}

public sealed class TenantHealth
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Database { get; set; } = "";
    public bool Ok { get; set; }
    public int? Users { get; set; }
    public string? Error { get; set; }
}
