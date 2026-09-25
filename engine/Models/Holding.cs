using System.Text.Json.Serialization;

namespace ComplianceEngine.Models;

public class Holding
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public string Ticker { get; set; } = "";
    public string? Sector { get; set; }
    public decimal Quantity { get; set; }
    public decimal MarketValue { get; set; }
    public decimal LastTradePrice { get; set; }
    public DateTime LastTradeAt { get; set; }
    
    [JsonIgnore]
    public Portfolio? Portfolio { get; set; }
}