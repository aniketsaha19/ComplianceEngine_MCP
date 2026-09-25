using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ComplianceEngine.Data;
using ComplianceEngine.Models;

namespace ComplianceEngine.Controllers;

[ApiController]
[Route("rules")]
public class RulesController : ControllerBase
{
    private readonly ComplianceDbContext _db;

    public RulesController(ComplianceDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> GetRules()
    {
        var tenant = GetCurrentTenant();
        if (tenant == null)
            return Unauthorized("No authenticated tenant");

        var rules = await _db.Rules
            .Where(r => r.TenantId == tenant.Id)
            .Select(r => new
            {
                r.Id,
                r.Name,
                r.Description,
                r.RuleType,
                r.Threshold,
                r.IsActive
            })
            .ToListAsync();

        return Ok(rules);
    }

    [HttpPost]
    public async Task<IActionResult> CreateRule([FromBody] CreateRuleRequest request)
    {
        var tenant = GetCurrentTenant();
        if (tenant == null)
            return Unauthorized("No authenticated tenant");

        if (string.IsNullOrWhiteSpace(request.Name) || 
            string.IsNullOrWhiteSpace(request.RuleType) || 
            request.Threshold < 0)
        {
            return BadRequest(new { error = "Name, ruleType, and threshold >= 0 are required" });
        }

        // Validate rule type
        var validRuleTypes = new[] { "max_position_pct", "max_sector_pct", "min_holdings_count", "aggregate_large_position_pct", "max_top_n_concentration" };
        if (!validRuleTypes.Contains(request.RuleType))
        {
            return BadRequest(new { error = "Invalid rule type", validTypes = validRuleTypes });
        }

        try
        {
            var rule = new Rule
            {
                TenantId = tenant.Id,
                Name = request.Name,
                Description = request.Description,
                RuleType = request.RuleType,
                Threshold = request.Threshold,
                IsActive = true
            };

            _db.Rules.Add(rule);
            await _db.SaveChangesAsync();

            return Ok(new
            {
                rule.Id,
                rule.Name,
                rule.Description,
                rule.RuleType,
                rule.Threshold,
                rule.IsActive
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = "Failed to create rule", details = ex.Message });
        }
    }

    [HttpPatch("{id}")]
    public async Task<IActionResult> UpdateRule(int id, [FromBody] UpdateRuleRequest request)
    {
        var tenant = GetCurrentTenant();
        if (tenant == null)
            return Unauthorized("No authenticated tenant");

        var rule = await _db.Rules.FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenant.Id);
        if (rule == null)
            return NotFound(new { error = $"Rule {id} not found or not owned by tenant" });

        try
        {
            if (!string.IsNullOrWhiteSpace(request.Name))
                rule.Name = request.Name;

            if (!string.IsNullOrWhiteSpace(request.Description))
                rule.Description = request.Description;

            if (!string.IsNullOrWhiteSpace(request.RuleType))
            {
                var validRuleTypes = new[] { "max_position_pct", "max_sector_pct", "min_holdings_count", "aggregate_large_position_pct", "max_top_n_concentration" };
                if (!validRuleTypes.Contains(request.RuleType))
                    return BadRequest(new { error = "Invalid rule type", validTypes = validRuleTypes });
                rule.RuleType = request.RuleType;
            }

            if (request.Threshold.HasValue && request.Threshold.Value >= 0)
                rule.Threshold = request.Threshold.Value;

            if (request.IsActive.HasValue)
                rule.IsActive = request.IsActive.Value;

            await _db.SaveChangesAsync();

            return Ok(new
            {
                rule.Id,
                rule.Name,
                rule.Description,
                rule.RuleType,
                rule.Threshold,
                rule.IsActive
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = "Failed to update rule", details = ex.Message });
        }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteRule(int id)
    {
        var tenant = GetCurrentTenant();
        if (tenant == null)
            return Unauthorized("No authenticated tenant");

        var rule = await _db.Rules.FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenant.Id);
        if (rule == null)
            return NotFound(new { error = $"Rule {id} not found or not owned by tenant" });

        try
        {
            // Remove associated rule evaluations first
            await _db.RuleEvaluations
                .Where(re => re.RuleId == id)
                .ExecuteDeleteAsync();

            // Remove the rule
            _db.Rules.Remove(rule);
            await _db.SaveChangesAsync();

            return Ok(new { message = $"Rule {id} deleted successfully" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = "Failed to delete rule", details = ex.Message });
        }
    }

    private Tenant? GetCurrentTenant()
    {
        return HttpContext.Items["CurrentTenant"] as Tenant;
    }
}

public class CreateRuleRequest
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string RuleType { get; set; } = "";
    public decimal Threshold { get; set; }
}

public class UpdateRuleRequest
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? RuleType { get; set; }
    public decimal? Threshold { get; set; }
    public bool? IsActive { get; set; }
}