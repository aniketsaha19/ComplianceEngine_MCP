using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ComplianceEngine.Data;
using ComplianceEngine.Models;

namespace ComplianceEngine.Controllers;

[ApiController]
[Route("tenants")]
public class TenantsController : ControllerBase
{
    private readonly ComplianceDbContext _db;
    private readonly ILogger<TenantsController> _logger;
    private static readonly PasswordHasher<Tenant> Hasher = new();

    public TenantsController(ComplianceDbContext db, ILogger<TenantsController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>Sign up. Returns an API key for direct API use; MCP clients get theirs via /tenants/login.</summary>
    [HttpPost]
    public async Task<IActionResult> CreateTenant([FromBody] CreateTenantRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { error = "Name is required" });
        }
        if (string.IsNullOrWhiteSpace(request.Email) || !request.Email.Contains('@'))
        {
            return BadRequest(new { error = "A valid email is required" });
        }
        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 12)
        {
            return BadRequest(new { error = "Password must be at least 12 characters" });
        }

        var email = request.Email.Trim().ToLowerInvariant();
        if (await _db.Tenants.AnyAsync(t => t.Email == email))
        {
            return Conflict(new { error = "A tenant with that email already exists" });
        }

        var tenant = new Tenant
        {
            Name = request.Name,
            Email = email,
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };
        tenant.PasswordHash = Hasher.HashPassword(tenant, request.Password);

        var rawApiKey = GenerateApiKey();
        tenant.ApiKeys.Add(new ApiKey
        {
            KeyHash = ComputeSha256Hash(rawApiKey),
            Label = "signup",
            CreatedAt = DateTime.UtcNow
        });

        _db.Tenants.Add(tenant);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // The AnyAsync check above is not atomic — a double-submit can race past it.
            return Conflict(new { error = "A tenant with that email already exists" });
        }

        return Ok(new
        {
            tenantId = tenant.Id,
            apiKey = rawApiKey // Only shown once!
        });
    }

    /// <summary>
    /// Exchange tenant credentials for a fresh API key. The MCP gateway calls this on the
    /// user's behalf during OAuth login, so the user never sees or handles a key.
    /// </summary>
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new { error = "Email and password are required" });
        }

        var email = request.Email.Trim().ToLowerInvariant();
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Email == email && t.IsActive);

        // Verify even when no tenant matched, so a wrong email and a wrong password cost the same time.
        var probe = tenant ?? new Tenant();
        var hash = tenant?.PasswordHash ?? Hasher.HashPassword(probe, "timing-equalizer");
        var result = Hasher.VerifyHashedPassword(probe, hash, request.Password);
        if (tenant == null || result == PasswordVerificationResult.Failed)
        {
            return Unauthorized(new { error = "Invalid email or password" });
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            tenant.PasswordHash = Hasher.HashPassword(tenant, request.Password);
        }

        var rawApiKey = GenerateApiKey();
        _db.ApiKeys.Add(new ApiKey
        {
            TenantId = tenant.Id,
            KeyHash = ComputeSha256Hash(rawApiKey),
            Label = string.IsNullOrWhiteSpace(request.Label) ? "mcp" : request.Label!.Trim(),
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        return Ok(new
        {
            tenantId = tenant.Id,
            tenantName = tenant.Name,
            apiKey = rawApiKey
        });
    }

    /// <summary>Revoke the API key used to make this call (MCP logout / token revocation).</summary>
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        var tenant = HttpContext.Items["CurrentTenant"] as Tenant;
        if (tenant == null)
        {
            return Unauthorized(new { error = "API key required" });
        }

        var keyHash = ComputeSha256Hash(Request.Headers.Authorization.ToString()["Bearer ".Length..]);
        var key = await _db.ApiKeys.FirstOrDefaultAsync(k => k.KeyHash == keyHash && k.RevokedAt == null);
        if (key != null)
        {
            key.RevokedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        return Ok(new { revoked = true });
    }

    [HttpGet("/health")]
    public async Task<IActionResult> Health()
    {
        try
        {
            var tenantCount = await _db.Tenants.CountAsync();
            return Ok(new
            {
                status = "healthy",
                tenantCount = tenantCount,
                timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            // Logged, not returned: the operator needs the reason (login failure, missing table,
            // firewall) in the container logs, while the caller gets no internal detail.
            _logger.LogError(ex, "Health check failed: could not query the database");
            return StatusCode(503, new
            {
                status = "unhealthy",
                // Safe to expose: names the failing subsystem without any connection detail.
                reason = ex.GetBaseException().GetType().Name
            });
        }
    }

    [HttpGet("profile")]
    public IActionResult GetProfile()
    {
        var tenant = HttpContext.Items["CurrentTenant"] as Tenant;
        if (tenant == null)
        {
            return Unauthorized(new { error = "API key required" });
        }

        return Ok(new
        {
            id = tenant.Id,
            name = tenant.Name,
            email = tenant.Email,
            isActive = tenant.IsActive,
            createdAt = tenant.CreatedAt
        });
    }

    // 2601 = duplicate key row in a unique index, 2627 = unique constraint violation.
    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException sql && (sql.Number == 2601 || sql.Number == 2627);

    private static string GenerateApiKey() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    private static string ComputeSha256Hash(string rawData) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawData))).ToLowerInvariant();
}

public class CreateTenantRequest
{
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
}

public class LoginRequest
{
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
    public string? Label { get; set; }
}
