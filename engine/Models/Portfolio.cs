namespace ComplianceEngine.Models;

public class Portfolio
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public Tenant? Tenant { get; set; }
    public ICollection<Holding> Holdings { get; set; } = new List<Holding>();
}