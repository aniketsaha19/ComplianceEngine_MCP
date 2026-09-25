namespace ComplianceEngine.Models;

public class Rule
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    // max_position_pct | max_sector_pct | min_holdings_count | aggregate_large_position_pct | max_top_n_concentration
    public string RuleType { get; set; } = "";
    public decimal Threshold { get; set; }
    public bool IsActive { get; set; } = true;
    public Tenant? Tenant { get; set; }
}