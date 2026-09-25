using System.Text.Json.Serialization;

namespace ComplianceEngine.Models;

public class Trade
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public string Ticker { get; set; } = "";
    public string? Sector { get; set; }
    public string Action { get; set; } = ""; // BUY | SELL
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public string Status { get; set; } = ""; // EXECUTED | BLOCKED
    public DateTime RequestedAt { get; set; }
    
    [JsonIgnore]
    public Portfolio? Portfolio { get; set; }
}