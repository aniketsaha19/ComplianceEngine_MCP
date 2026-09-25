using ComplianceEngine.Data;
using ComplianceEngine.Models;
using ComplianceEngine.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ComplianceEngine.Controllers;

[ApiController]
[Route("trades")]
public class TradesController : ControllerBase
{
    private readonly ComplianceDbContext _db;
    private readonly RuleEvaluationService _evaluator;

    public TradesController(ComplianceDbContext db, RuleEvaluationService evaluator)
    {
        _db = db;
        _evaluator = evaluator;
    }

    [HttpPost]
    public async Task<IActionResult> RecordTrade([FromBody] RecordTradeRequest request)
    {
        var tenant = GetCurrentTenant();
        if (tenant == null)
            return Unauthorized("No authenticated tenant");

        // Validate request
        if (request.portfolio_id <= 0)
        {
            return BadRequest(new { error = "Portfolio ID is required" });
        }

        if (string.IsNullOrWhiteSpace(request.Ticker))
        {
            return BadRequest(new { error = "Ticker is required" });
        }

        if (string.IsNullOrWhiteSpace(request.Action) || (request.Action != "BUY" && request.Action != "SELL"))
        {
            return BadRequest(new { error = "Action must be 'BUY' or 'SELL'" });
        }

        if (request.Quantity <= 0)
        {
            return BadRequest(new { error = "Quantity must be greater than 0" });
        }

        if (request.Price <= 0)
        {
            return BadRequest(new { error = "Price must be greater than 0" });
        }

        try
        {
            var portfolio = await _db.Portfolios
                .FirstOrDefaultAsync(p => p.Id == request.portfolio_id && p.TenantId == tenant.Id);
                
            if (portfolio == null)
            {
                return NotFound(new { error = $"Portfolio {request.portfolio_id} not found or not owned by tenant" });
            }

            // Create trade record
            var trade = new Trade
            {
                PortfolioId = request.portfolio_id,
                Ticker = request.Ticker,
                Sector = request.Sector,
                Action = request.Action,
                Quantity = request.Quantity,
                Price = request.Price,
                Status = "EXECUTED",
                RequestedAt = DateTime.UtcNow
            };

            _db.Trades.Add(trade);
            await _db.SaveChangesAsync();

            // Update holdings automatically
            await UpdateHoldingsAfterTrade(request.portfolio_id, trade);

            return Ok(new
            {
                tradeId = trade.Id,
                portfolioId = trade.PortfolioId,
                ticker = trade.Ticker,
                action = trade.Action,
                quantity = trade.Quantity,
                price = trade.Price,
                status = trade.Status,
                executedAt = trade.RequestedAt,
                message = "Trade recorded and portfolio holdings updated"
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = "Failed to record trade", details = ex.Message });
        }
    }

    [HttpPost("compliance-check")]
    public async Task<IActionResult> CheckTradeCompliance([FromBody] RecordTradeRequest request)
    {
        var tenant = GetCurrentTenant();
        if (tenant == null)
            return Unauthorized("No authenticated tenant");

        // Without this check any tenant could evaluate — and so read — another tenant's portfolio.
        var owned = await _db.Portfolios
            .AnyAsync(p => p.Id == request.portfolio_id && p.TenantId == tenant.Id);
        if (!owned)
        {
            return NotFound(new { error = $"Portfolio {request.portfolio_id} not found or not owned by tenant" });
        }

        try
        {
            var tradeRequest = new TradeRequest
            {
                Ticker = request.Ticker,
                Sector = request.Sector,
                Action = request.Action,
                Quantity = request.Quantity,
                Price = request.Price
            };

            var result = await _evaluator.EvaluateTradeCheckAsync(request.portfolio_id, tradeRequest);
            
            return Ok(new
            {
                tradeAllowed = result.Allowed,
                portfolioId = request.portfolio_id,
                proposedTrade = request,
                complianceCheck = new
                {
                    wouldViolateRules = result.BreachedRules.Any(),
                    estimatedFinalCompliance = result.Allowed ? "PASS" : "BLOCK",
                    recommendations = result.Allowed 
                        ? new List<string> { "Trade complies with all rules" }
                        : result.BreachedRules.Select(r => $"Would violate rule: {r.RuleName}").ToList()
                }
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = "Failed to check trade compliance", details = ex.Message });
        }
    }

    private async Task UpdateHoldingsAfterTrade(int portfolioId, Trade trade)
    {
        try
        {
            // Get current holding for the ticker
            var currentHolding = await _db.Holdings
                .FirstOrDefaultAsync(h => h.PortfolioId == portfolioId && h.Ticker == trade.Ticker);

            if (trade.Action == "BUY")
            {
                if (currentHolding == null)
                {
                    // Create new holding
                    _db.Holdings.Add(new Holding
                    {
                        PortfolioId = portfolioId,
                        Ticker = trade.Ticker,
                        Sector = trade.Sector,
                        Quantity = trade.Quantity,
                        MarketValue = trade.Quantity * trade.Price,
                        LastTradePrice = trade.Price,
                        LastTradeAt = trade.RequestedAt
                    });
                }
                else
                {
                    // Update existing holding
                    currentHolding.Quantity += trade.Quantity;
                    currentHolding.MarketValue = currentHolding.Quantity * trade.Price;
                    currentHolding.LastTradePrice = trade.Price;
                    currentHolding.LastTradeAt = trade.RequestedAt;

                    _db.Holdings.Update(currentHolding);
                }
            }
            else if (trade.Action == "SELL")
            {
                if (currentHolding != null && currentHolding.Quantity >= trade.Quantity)
                {
                    currentHolding.Quantity -= trade.Quantity;
                    
                    if (currentHolding.Quantity == 0)
                    {
                        // Remove holding if quantity becomes 0
                        _db.Holdings.Remove(currentHolding);
                    }
                    else
                    {
                        currentHolding.MarketValue = currentHolding.Quantity * trade.Price;
                        currentHolding.LastTradePrice = trade.Price;
                        currentHolding.LastTradeAt = trade.RequestedAt;
                        _db.Holdings.Update(currentHolding);
                    }
                }
                // If trying to sell more than we have, that's a business logic error
            }

            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // Log the error but don't fail the trade
            Console.WriteLine($"Warning: Failed to update holdings after trade {trade.Id}: {ex.Message}");
        }
    }

    private Tenant? GetCurrentTenant()
    {
        return HttpContext.Items["CurrentTenant"] as Tenant;
    }
}

// DTO used by MCP Gateway clients
public class RecordTradeRequest
{
    public int portfolio_id { get; set; }
    public string PortfolioId { get; set; } = "";
    public string Ticker { get; set; } = "";
    public string? Sector { get; set; }
    public string Action { get; set; } = ""; // "BUY" | "SELL"
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
}

// Legacy DTO for compatibility (if any old MCP calls use this)
public class LegacyRecordTradeRequest
{
    public string portfolioId { get; set; } = "";
    public string ticker { get; set; } = "";
    public string? sector { get; set; }
    public string action { get; set; } = "";
    public decimal quantity { get; set; }
    public decimal price { get; set; }
}