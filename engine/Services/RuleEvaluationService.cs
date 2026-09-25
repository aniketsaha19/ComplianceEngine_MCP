using ComplianceEngine.Data;
using ComplianceEngine.Models;
using Microsoft.EntityFrameworkCore;

namespace ComplianceEngine.Services;

public class HoldingWeight
{
    public string Ticker { get; set; } = "";
    public string? Sector { get; set; }
    public decimal Value { get; set; } // MarketValue
    public decimal Weight { get; set; }
}

public class RuleEvaluationOutcome
{
    public int RuleId { get; set; }
    public string RuleName { get; set; } = "";
    public string RuleType { get; set; } = "";
    public bool Breached { get; set; }
    public decimal CurrentValue { get; set; }
    public decimal Threshold { get; set; }
    public string Detail { get; set; } = "";
}

public class TradeRequest
{
    public string Ticker { get; set; } = "";
    public string? Sector { get; set; }
    public string Action { get; set; } = ""; // "BUY" | "SELL"
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
}

public class TradeCheckResult
{
    public bool Allowed { get; set; }
    public decimal WouldBeHoldingValue { get; set; }
    public List<RuleEvaluationOutcome> BreachedRules { get; set; } = new();
}

public class TradeResult
{
    public string Status { get; set; } = ""; // "EXECUTED" | "BLOCKED"
    public List<RuleEvaluationOutcome> BreachedRules { get; set; } = new();
}

public class RuleEvaluationService
{
    private readonly ComplianceDbContext _db;
    private const decimal LargePositionTrigger = 0.05m; // UCITS "5%" trigger
    private const int TopNHoldings = 10;

    public RuleEvaluationService(ComplianceDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Evaluate portfolio against tenant's rules using persisted holdings
    /// </summary>
    public async Task<List<RuleEvaluationOutcome>> EvaluatePortfolioAsync(int portfolioId, int? tradeId = null)
    {
        var portfolio = await _db.Portfolios
            .Include(p => p.Tenant)
            .FirstOrDefaultAsync(p => p.Id == portfolioId);
            
        if (portfolio?.Tenant == null)
            throw new ArgumentException($"Portfolio {portfolioId} or tenant not found");

        var holdings = await _db.Holdings.Where(h => h.PortfolioId == portfolioId).ToListAsync();
        return await EvaluateHoldingsAsync(holdings, portfolio.TenantId, portfolioId, tradeId);
    }

    /// <summary>
    /// Evaluate hypothetical holdings for trade-check scenarios
    /// </summary>
    public async Task<TradeCheckResult> EvaluateTradeCheckAsync(int portfolioId, TradeRequest tradeRequest)
    {
        var portfolio = await _db.Portfolios
            .Include(p => p.Tenant)
            .FirstOrDefaultAsync(p => p.Id == portfolioId);
            
        if (portfolio?.Tenant == null)
            throw new ArgumentException($"Portfolio {portfolioId} or tenant not found");

        // Get current holdings
        var holdings = await _db.Holdings.Where(h => h.PortfolioId == portfolioId).ToListAsync();
        
        // Apply hypothetical trade
        var hypotheticalHoldings = ApplyTradeToHoldings(holdings, tradeRequest, portfolioId);
        
        // Evaluate against tenant's rules
        var results = await EvaluateHypotheticalHoldingsAsync(hypotheticalHoldings, portfolio.TenantId, portfolioId);
        var breachedRules = results.Where(r => r.Breached).ToList();
        
        // Calculate what the holding value would be
        var targetHolding = hypotheticalHoldings.FirstOrDefault(h => h.Ticker == tradeRequest.Ticker);
        var wouldBeHoldingValue = targetHolding?.MarketValue ?? 0m;

        return new TradeCheckResult
        {
            Allowed = !breachedRules.Any(),
            WouldBeHoldingValue = wouldBeHoldingValue,
            BreachedRules = breachedRules
        };
    }

    /// <summary>
    /// Apply trade and persist result with proper audit trail
    /// </summary>
    public async Task<TradeResult> ProcessTradeAsync(int portfolioId, TradeRequest tradeRequest)
    {
        var portfolio = await _db.Portfolios
            .Include(p => p.Tenant)
            .FirstOrDefaultAsync(p => p.Id == portfolioId);
            
        if (portfolio?.Tenant == null)
            throw new ArgumentException($"Portfolio {portfolioId} or tenant not found");

        // Evaluate trade first
        var checkResult = await EvaluateTradeCheckAsync(portfolioId, tradeRequest);
        
        // Create trade record
        var trade = new Trade
        {
            PortfolioId = portfolioId,
            Ticker = tradeRequest.Ticker,
            Sector = tradeRequest.Sector,
            Action = tradeRequest.Action,
            Quantity = tradeRequest.Quantity,
            Price = tradeRequest.Price,
            Status = checkResult.Allowed ? "EXECUTED" : "BLOCKED",
            RequestedAt = DateTime.UtcNow
        };
        
        _db.Trades.Add(trade);
        await _db.SaveChangesAsync(); // Save trade to get ID
        
        // Evaluate holdings (including persisted evaluation for audit trail)
        var holdings = await _db.Holdings.Where(h => h.PortfolioId == portfolioId).ToListAsync();
        var hypotheticalHoldings = ApplyTradeToHoldings(holdings, tradeRequest, portfolioId);
        var evaluationResults = await EvaluateHoldingsAsync(hypotheticalHoldings, portfolio.TenantId, portfolioId, trade.Id);
        var breachedRules = evaluationResults.Where(r => r.Breached).ToList();
        
        // If trade is allowed, update holdings
        if (checkResult.Allowed)
        {
            await UpdateHoldingsAsync(portfolioId, tradeRequest);
        }
        
        return new TradeResult
        {
            Status = trade.Status,
            BreachedRules = breachedRules
        };
    }

    /// <summary>
    /// Evaluate holdings list (persisted or hypothetical) against tenant's rules
    /// </summary>
    private async Task<List<RuleEvaluationOutcome>> EvaluateHoldingsAsync(List<Holding> holdings, int tenantId, int portfolioId, int? tradeId)
    {
        if (holdings.Count == 0)
        {
            // Still need to evaluate rules against empty portfolio
            var emptyTenantRules = await _db.Rules.Where(r => r.TenantId == tenantId && r.IsActive).ToListAsync();
            var emptyOutcomes = new List<RuleEvaluationOutcome>();
            
            foreach (var rule in emptyTenantRules)
            {
                var emptyOutcome = CreateEmptyPortfolioOutcome(rule);
                emptyOutcomes.Add(emptyOutcome);
                
                // Record evaluation for audit trail
                _db.RuleEvaluations.Add(new RuleEvaluation
                {
                    RuleId = rule.Id,
                    PortfolioId = portfolioId,
                    TradeId = tradeId,
                    Breached = emptyOutcome.Breached,
                    CurrentValue = emptyOutcome.CurrentValue,
                    Threshold = emptyOutcome.Threshold,
                    EvaluatedAt = DateTime.UtcNow
                });
            }
            
            await _db.SaveChangesAsync();
            return emptyOutcomes;
        }

        // Calculate weights directly from MarketValue
        var weighted = CalculateHoldingsWeights(holdings);
        
        // Load tenant's active rules
        var tenantActiveRules = await _db.Rules.Where(r => r.TenantId == tenantId && r.IsActive).ToListAsync();
        var evaluationResults = new List<RuleEvaluationOutcome>();

        foreach (var rule in tenantActiveRules)
        {
            var outcome = rule.RuleType switch
            {
                "max_position_pct" => EvaluateMaxPosition(rule, weighted),
                "max_sector_pct" => EvaluateMaxSector(rule, weighted),
                "min_holdings_count" => EvaluateMinHoldings(rule, weighted),
                "aggregate_large_position_pct" => EvaluateAggregateLargePosition(rule, weighted),
                "max_top_n_concentration" => EvaluateTopN(rule, weighted),
                _ => new RuleEvaluationOutcome { RuleId = rule.Id, RuleName = rule.Name, RuleType = rule.RuleType, Detail = "Unknown rule type" }
            };
            evaluationResults.Add(outcome);

            // Record evaluation for audit trail
            _db.RuleEvaluations.Add(new RuleEvaluation
            {
                RuleId = rule.Id,
                PortfolioId = portfolioId,
                TradeId = tradeId,
                Breached = outcome.Breached,
                CurrentValue = outcome.CurrentValue,
                Threshold = outcome.Threshold,
                EvaluatedAt = DateTime.UtcNow
            });
        }

        await _db.SaveChangesAsync();
        return evaluationResults;
    }

    /// <summary>
    /// Evaluate hypothetical holdings without writing to database (for trade-check)
    /// </summary>
    private async Task<List<RuleEvaluationOutcome>> EvaluateHypotheticalHoldingsAsync(List<Holding> holdings, int tenantId, int portfolioId)
    {
        if (holdings.Count == 0)
        {
            // Still need to evaluate rules against empty portfolio
            var emptyTenantRules = await _db.Rules.Where(r => r.TenantId == tenantId && r.IsActive).ToListAsync();
            var emptyOutcomes = new List<RuleEvaluationOutcome>();
            
            foreach (var rule in emptyTenantRules)
            {
                var emptyOutcome = CreateEmptyPortfolioOutcome(rule);
                emptyOutcomes.Add(emptyOutcome);
            }
            
            return emptyOutcomes;
        }

        // Calculate weights directly from MarketValue
        var weighted = CalculateHoldingsWeights(holdings);
        
        // Load tenant's active rules
        var tenantActiveRules = await _db.Rules.Where(r => r.TenantId == tenantId && r.IsActive).ToListAsync();
        var evaluationResults = new List<RuleEvaluationOutcome>();

        foreach (var rule in tenantActiveRules)
        {
            var outcome = rule.RuleType switch
            {
                "max_position_pct" => EvaluateMaxPosition(rule, weighted),
                "max_sector_pct" => EvaluateMaxSector(rule, weighted),
                "min_holdings_count" => EvaluateMinHoldings(rule, weighted),
                "aggregate_large_position_pct" => EvaluateAggregateLargePosition(rule, weighted),
                "max_top_n_concentration" => EvaluateTopN(rule, weighted),
                _ => new RuleEvaluationOutcome { RuleId = rule.Id, RuleName = rule.Name, RuleType = rule.RuleType, Detail = "Unknown rule type" }
            };
            evaluationResults.Add(outcome);
        }

        return evaluationResults;
    }

    /// <summary>
    /// Calculate weights directly from MarketValue fields (no price lookup needed)
    /// </summary>
    private List<HoldingWeight> CalculateHoldingsWeights(List<Holding> holdings)
    {
        var weighted = new List<HoldingWeight>();
        decimal totalValue = holdings.Sum(h => h.MarketValue);

        foreach (var h in holdings)
        {
            weighted.Add(new HoldingWeight
            {
                Ticker = h.Ticker,
                Sector = h.Sector,
                Value = h.MarketValue,
                Weight = totalValue == 0 ? 0 : h.MarketValue / totalValue
            });
        }

        return weighted;
    }

    /// <summary>
    /// Apply trade to holdings list (for hypothetical scenarios)
    /// </summary>
    private List<Holding> ApplyTradeToHoldings(List<Holding> holdings, TradeRequest tradeRequest, int portfolioId)
    {
        var result = new List<Holding>(holdings);
        
        if (tradeRequest.Action == "SELL")
        {
            // Find and update existing holding
            var holding = result.FirstOrDefault(h => h.Ticker == tradeRequest.Ticker);
            if (holding == null)
            {
                throw new InvalidOperationException($"Cannot sell {tradeRequest.Ticker} - no existing holding found");
            }
            
            holding.Quantity -= tradeRequest.Quantity;
            holding.MarketValue = holding.Quantity * tradeRequest.Price;
            holding.LastTradePrice = tradeRequest.Price;
            holding.LastTradeAt = DateTime.UtcNow;
            
            // Remove if quantity becomes 0 or negative
            if (holding.Quantity <= 0)
            {
                result.RemoveAll(h => h.Ticker == tradeRequest.Ticker);
            }
        }
        else // BUY
        {
            // Find or create holding
            var existingHolding = result.FirstOrDefault(h => h.Ticker == tradeRequest.Ticker);
            if (existingHolding != null)
            {
                existingHolding.Quantity += tradeRequest.Quantity;
                existingHolding.MarketValue = existingHolding.Quantity * tradeRequest.Price;
                existingHolding.LastTradePrice = tradeRequest.Price;
                existingHolding.LastTradeAt = DateTime.UtcNow;
            }
            else
            {
                // Create new holding for BUY
                result.Add(new Holding
                {
                    PortfolioId = portfolioId,
                    Ticker = tradeRequest.Ticker,
                    Sector = tradeRequest.Sector,
                    Quantity = tradeRequest.Quantity,
                    MarketValue = tradeRequest.Quantity * tradeRequest.Price,
                    LastTradePrice = tradeRequest.Price,
                    LastTradeAt = DateTime.UtcNow
                });
            }
        }
        
        return result;
    }

    /// <summary>
    /// Update Holdings table from database after a successful trade
    /// </summary>
    private async Task UpdateHoldingsAsync(int portfolioId, TradeRequest tradeRequest)
    {
        var holding = await _db.Holdings
            .FirstOrDefaultAsync(h => h.PortfolioId == portfolioId && h.Ticker == tradeRequest.Ticker);

        if (holding != null)
        {
            if (tradeRequest.Action == "SELL")
            {
                holding.Quantity -= tradeRequest.Quantity;
                holding.MarketValue = holding.Quantity * tradeRequest.Price;
            }
            else // BUY
            {
                holding.Quantity += tradeRequest.Quantity;
                holding.MarketValue = holding.Quantity * tradeRequest.Price;
            }
            
            holding.LastTradePrice = tradeRequest.Price;
            holding.LastTradeAt = DateTime.UtcNow;
            
            // Remove if quantity becomes 0
            if (holding.Quantity <= 0)
            {
                _db.Holdings.Remove(holding);
            }
        }
        else
        {
            // Create new holding for BUY
            if (tradeRequest.Action == "BUY")
            {
                _db.Holdings.Add(new Holding
                {
                    PortfolioId = portfolioId,
                    Ticker = tradeRequest.Ticker,
                    Sector = tradeRequest.Sector,
                    Quantity = tradeRequest.Quantity,
                    MarketValue = tradeRequest.Quantity * tradeRequest.Price,
                    LastTradePrice = tradeRequest.Price,
                    LastTradeAt = DateTime.UtcNow
                });
            }
        }

        await _db.SaveChangesAsync();
    }

    private RuleEvaluationOutcome CreateEmptyPortfolioOutcome(Rule rule)
    {
        return rule.RuleType switch
        {
            "max_position_pct" => new RuleEvaluationOutcome
            {
                RuleId = rule.Id, RuleName = rule.Name, RuleType = rule.RuleType,
                Breached = false, CurrentValue = 0, Threshold = rule.Threshold,
                Detail = "No positions in portfolio"
            },
            "max_sector_pct" => new RuleEvaluationOutcome
            {
                RuleId = rule.Id, RuleName = rule.Name, RuleType = rule.RuleType,
                Breached = false, CurrentValue = 0, Threshold = rule.Threshold,
                Detail = "No sector exposure in empty portfolio"
            },
            "min_holdings_count" => new RuleEvaluationOutcome
            {
                RuleId = rule.Id, RuleName = rule.Name, RuleType = rule.RuleType,
                Breached = 0 < rule.Threshold, CurrentValue = 0, Threshold = rule.Threshold,
                Detail = 0 < rule.Threshold 
                    ? $"Portfolio is empty, below minimum of {rule.Threshold} holdings" 
                    : "Empty portfolio meets minimum holdings requirement"
            },
            "aggregate_large_position_pct" => new RuleEvaluationOutcome
            {
                RuleId = rule.Id, RuleName = rule.Name, RuleType = rule.RuleType,
                Breached = false, CurrentValue = 0, Threshold = rule.Threshold,
                Detail = "No large positions in empty portfolio"
            },
            "max_top_n_concentration" => new RuleEvaluationOutcome
            {
                RuleId = rule.Id, RuleName = rule.Name, RuleType = rule.RuleType,
                Breached = false, CurrentValue = 0, Threshold = rule.Threshold,
                Detail = "No holdings in empty portfolio"
            },
            _ => new RuleEvaluationOutcome { RuleId = rule.Id, RuleName = rule.Name, RuleType = rule.RuleType, Detail = "Unknown rule type" }
        };
    }

    // Rule evaluation methods (unchanged logic, different input type)
    private RuleEvaluationOutcome EvaluateMaxPosition(Rule rule, List<HoldingWeight> w)
    {
        if (w.Count == 0) return CreateEmptyPortfolioOutcome(rule);
        
        var worst = w.OrderByDescending(x => x.Weight).First();
        bool breached = worst.Weight > rule.Threshold;
        return new RuleEvaluationOutcome
        {
            RuleId = rule.Id, RuleName = rule.Name, RuleType = rule.RuleType,
            Breached = breached, CurrentValue = worst.Weight, Threshold = rule.Threshold,
            Detail = breached
                ? $"{worst.Ticker} is {worst.Weight:P1} of the portfolio, exceeding the {rule.Threshold:P0} single-position limit."
                : $"Largest position is {worst.Ticker} at {worst.Weight:P1}, within the {rule.Threshold:P0} limit."
        };
    }

    private RuleEvaluationOutcome EvaluateMaxSector(Rule rule, List<HoldingWeight> w)
    {
        if (w.Count == 0) return CreateEmptyPortfolioOutcome(rule);
        
        var worst = w.GroupBy(x => x.Sector ?? "Unknown")
            .Select(g => new { Sector = g.Key, Weight = g.Sum(x => x.Weight) })
            .OrderByDescending(x => x.Weight).First();
        bool breached = worst.Weight > rule.Threshold;
        return new RuleEvaluationOutcome
        {
            RuleId = rule.Id, RuleName = rule.Name, RuleType = rule.RuleType,
            Breached = breached, CurrentValue = worst.Weight, Threshold = rule.Threshold,
            Detail = breached
                ? $"{worst.Sector} sector is {worst.Weight:P1} of the portfolio, exceeding the {rule.Threshold:P0} sector limit."
                : $"Largest sector exposure is {worst.Sector} at {worst.Weight:P1}, within the {rule.Threshold:P0} limit."
        };
    }

    private RuleEvaluationOutcome EvaluateMinHoldings(Rule rule, List<HoldingWeight> w)
    {
        int count = w.Select(x => x.Ticker).Distinct().Count();
        bool breached = count < rule.Threshold;
        return new RuleEvaluationOutcome
        {
            RuleId = rule.Id, RuleName = rule.Name, RuleType = rule.RuleType,
            Breached = breached, CurrentValue = count, Threshold = rule.Threshold,
            Detail = breached
                ? $"Portfolio holds only {count} distinct securities, below the minimum of {rule.Threshold} required."
                : $"Portfolio holds {count} distinct securities, meeting the minimum diversification requirement."
        };
    }

    private RuleEvaluationOutcome EvaluateAggregateLargePosition(Rule rule, List<HoldingWeight> w)
    {
        if (w.Count == 0) return CreateEmptyPortfolioOutcome(rule);
        
        decimal aggregate = w.Where(x => x.Weight >= LargePositionTrigger).Sum(x => x.Weight);
        bool breached = aggregate > rule.Threshold;
        return new RuleEvaluationOutcome
        {
            RuleId = rule.Id, RuleName = rule.Name, RuleType = rule.RuleType,
            Breached = breached, CurrentValue = aggregate, Threshold = rule.Threshold,
            Detail = breached
                ? $"Positions of {LargePositionTrigger:P0}+ total {aggregate:P1}, exceeding the {rule.Threshold:P0} aggregate limit."
                : $"Positions of {LargePositionTrigger:P0}+ total {aggregate:P1}, within the {rule.Threshold:P0} aggregate limit."
        };
    }

    private RuleEvaluationOutcome EvaluateTopN(Rule rule, List<HoldingWeight> w)
    {
        if (w.Count == 0) return CreateEmptyPortfolioOutcome(rule);
        
        decimal topSum = w.OrderByDescending(x => x.Weight).Take(TopNHoldings).Sum(x => x.Weight);
        bool breached = topSum > rule.Threshold;
        return new RuleEvaluationOutcome
        {
            RuleId = rule.Id, RuleName = rule.Name, RuleType = rule.RuleType,
            Breached = breached, CurrentValue = topSum, Threshold = rule.Threshold,
            Detail = breached
                ? $"Top {TopNHoldings} holdings represent {topSum:P1} of the portfolio, exceeding the {rule.Threshold:P0} limit."
                : $"Top {TopNHoldings} holdings represent {topSum:P1} of the portfolio, within the {rule.Threshold:P0} limit."
        };
    }
}