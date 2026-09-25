namespace ComplianceEngine.Models;

public class Tenant
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public bool IsActive { get; set; } = true;

    public ICollection<ApiKey> ApiKeys { get; set; } = new List<ApiKey>();
    public ICollection<Rule> Rules { get; set; } = new List<Rule>();
    public ICollection<Portfolio> Portfolios { get; set; } = new List<Portfolio>();
}
