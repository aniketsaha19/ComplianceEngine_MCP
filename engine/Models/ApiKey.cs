namespace ComplianceEngine.Models;

/// <summary>
/// A credential a tenant authenticates with. Tenants may hold several at once
/// (one per MCP login session), so keys can be revoked individually.
/// </summary>
public class ApiKey
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public string KeyHash { get; set; } = "";
    public string Label { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }

    public Tenant Tenant { get; set; } = null!;
}
