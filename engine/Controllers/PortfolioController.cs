using ComplianceEngine.Data;
using ComplianceEngine.Models;
using ComplianceEngine.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ComplianceEngine.Controllers;

[ApiController]
[Route("portfolios")]
public class PortfolioController : ControllerBase
{
    private readonly ComplianceDbContext _db;
    private readonly RuleEvaluationService _evaluator;

    public PortfolioController(ComplianceDbContext db, RuleEvaluationService evaluator)
    {
        _db = db;
        _evaluator = evaluator;
    }

    [HttpPost]
    public async Task<IActionResult> CreatePortfolio([FromBody] CreatePortfolioRequest request)
    {
        var tenant = GetCurrentTenant();
        if (tenant == null)
            return Unauthorized("No authenticated tenant");

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { error = "Portfolio name is required" });
        }

        try
        {
            // Check for duplicate portfolio name within tenant
            var exists = await _db.Portfolios
                .AnyAsync(p => p.TenantId == tenant.Id && p.Name == request.Name);
                
            if (exists)
            {
                return Conflict(new { error = "Portfolio with this name already exists for tenant" });
            }

            var portfolio = new Portfolio
            {
                TenantId = tenant.Id,
                Name = request.Name,
                CreatedAt = DateTime.UtcNow
            };

            _db.Portfolios.Add(portfolio);
            await _db.SaveChangesAsync();

            return Ok(new
            {
                portfolioId = portfolio.Id,
                name = portfolio.Name
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = "Failed to create portfolio", details = ex.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetPortfolios()
    {
        var tenant = GetCurrentTenant();
        if (tenant == null)
            return Unauthorized("No authenticated tenant");

        var portfolios = await _db.Portfolios
            .Where(p => p.TenantId == tenant.Id)
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.CreatedAt,
                holdingsCount = _db.Holdings.Count(h => h.PortfolioId == p.Id)
            })
            .ToListAsync();

        return Ok(portfolios);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeletePortfolio(int id)
    {
        var tenant = GetCurrentTenant();
        if (tenant == null)
            return Unauthorized("No authenticated tenant");

        var portfolio = await _db.Portfolios
            .FirstOrDefaultAsync(p => p.Id == id && p.TenantId == tenant.Id);
            
        if (portfolio == null)
            return NotFound(new { error = $"Portfolio {id} not found or not owned by tenant" });

        try
        {
            // Delete related data (cascading)
            _db.Portfolios.Remove(portfolio);
            await _db.SaveChangesAsync();

            return Ok(new { message = $"Portfolio {id} deleted successfully" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = "Failed to delete portfolio", details = ex.Message });
        }
    }

    [HttpGet("{id}/holdings")]
    public async Task<IActionResult> GetHoldings(int id)
    {
        var tenant = GetCurrentTenant();
        if (tenant == null)
            return Unauthorized("No authenticated tenant");

        var portfolio = await _db.Portfolios
            .FirstOrDefaultAsync(p => p.Id == id && p.TenantId == tenant.Id);
            
        if (portfolio == null)
            return NotFound(new { error = $"Portfolio {id} not found or not owned by tenant" });

        var holdings = await _db.Holdings
            .Where(h => h.PortfolioId == id)
            .OrderBy(h => h.Ticker)
            .Select(h => new
            {
                h.Id,
                h.Ticker,
                h.Sector,
                h.Quantity,
                h.MarketValue,
                h.LastTradePrice,
                h.LastTradeAt
            })
            .ToListAsync();

        return Ok(new { portfolioId = id, portfolioName = portfolio.Name, holdings });
    }

    [HttpGet("{id}/compliance-summary")]
    public async Task<IActionResult> GetComplianceSummary(int id)
    {
        var tenant = GetCurrentTenant();
        if (tenant == null)
            return Unauthorized("No authenticated tenant");

        var portfolio = await _db.Portfolios
            .FirstOrDefaultAsync(p => p.Id == id && p.TenantId == tenant.Id);
            
        if (portfolio == null)
            return NotFound(new { error = $"Portfolio {id} not found or not owned by tenant" });

        try
        {
            var results = await _evaluator.EvaluatePortfolioAsync(id);
            return Ok(new
            {
                portfolioId = id,
                portfolioName = portfolio.Name,
                evaluatedAt = DateTime.UtcNow,
                compliant = !results.Any(r => r.Breached),
                rules = results
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = "Failed to evaluate compliance", details = ex.Message });
        }
    }

    [HttpPost("{id}/trade-check")]
    public async Task<IActionResult> TradeCheck(int id, [FromBody] TradeRequest request)
    {
        var tenant = GetCurrentTenant();
        if (tenant == null)
            return Unauthorized("No authenticated tenant");

        var portfolio = await _db.Portfolios
            .FirstOrDefaultAsync(p => p.Id == id && p.TenantId == tenant.Id);
            
        if (portfolio == null)
            return NotFound(new { error = $"Portfolio {id} not found or not owned by tenant" });

        // Validate request
        if (!ValidateTradeRequest(request, out var validationError))
        {
            return BadRequest(new { error = validationError });
        }

        try
        {
            var result = await _evaluator.EvaluateTradeCheckAsync(id, request);
            return Ok(new
            {
                result.Allowed,
                result.WouldBeHoldingValue,
                breachedRules = result.BreachedRules
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = "Failed to evaluate trade", details = ex.Message });
        }
    }

    [HttpPost("{id}/trades")]
    public async Task<IActionResult> ProcessTrade(int id, [FromBody] TradeRequest request)
    {
        var tenant = GetCurrentTenant();
        if (tenant == null)
            return Unauthorized("No authenticated tenant");

        var portfolio = await _db.Portfolios
            .FirstOrDefaultAsync(p => p.Id == id && p.TenantId == tenant.Id);
            
        if (portfolio == null)
            return NotFound(new { error = $"Portfolio {id} not found or not owned by tenant" });

        // Validate request
        if (!ValidateTradeRequest(request, out var validationError))
        {
            return BadRequest(new { error = validationError });
        }

        try
        {
            var result = await _evaluator.ProcessTradeAsync(id, request);
            return Ok(new
            {
                status = result.Status,
                breachedRules = result.BreachedRules
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = "Failed to process trade", details = ex.Message });
        }
    }

    private bool ValidateTradeRequest(TradeRequest request, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(request.Ticker))
        {
            error = "Ticker is required";
            return false;
        }

        if (request.Ticker.Length > 20)
        {
            error = "Ticker cannot exceed 20 characters";
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.Action) || 
            (request.Action != "BUY" && request.Action != "SELL"))
        {
            error = "Action must be 'BUY' or 'SELL'";
            return false;
        }

        if (request.Quantity <= 0)
        {
            error = "Quantity must be greater than 0";
            return false;
        }

        if (request.Price <= 0)
        {
            error = "Price must be greater than 0";
            return false;
        }

        return true;
    }

    private Tenant? GetCurrentTenant()
    {
        return HttpContext.Items["CurrentTenant"] as Tenant;
    }
}

public class CreatePortfolioRequest
{
    public string Name { get; set; } = "";
}