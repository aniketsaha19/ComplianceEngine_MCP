using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ComplianceEngine.Data;
using ComplianceEngine.Models;

namespace ComplianceEngine.Middleware;

public class TenantAuthenticationMiddleware
{
    private readonly RequestDelegate _next;

    // Routes reachable without an API key: signup, credential login, health and Swagger.
    private static readonly string[] PublicPaths =
        { "/health", "/tenants/login", "/swagger" };

    public TenantAuthenticationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";

        if (IsPublic(path, context.Request.Method))
        {
            await _next(context);
            return;
        }

        var authHeader = context.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            await Deny(context, "API key required");
            return;
        }

        using var scope = context.RequestServices.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ComplianceDbContext>();

        var tenant = await AuthenticateTenantAsync(db, authHeader["Bearer ".Length..].Trim());
        if (tenant == null)
        {
            await Deny(context, "Invalid or inactive API key");
            return;
        }

        context.Items["CurrentTenant"] = tenant;
        await _next(context);
    }

    private static bool IsPublic(string path, string method)
    {
        // Signup is the one POST that cannot carry a key yet.
        if (path == "/tenants" && method == "POST") return true;
        return PublicPaths.Any(p => path == p || path.StartsWith(p + "/", StringComparison.Ordinal));
    }

    private static async Task Deny(HttpContext context, string message)
    {
        context.Response.StatusCode = 401;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync($"{{\"error\":\"{message}\"}}");
    }

    private static async Task<Tenant?> AuthenticateTenantAsync(ComplianceDbContext db, string apiKey)
    {
        var keyHash = ComputeSha256Hash(apiKey);

        var key = await db.ApiKeys
            .Include(k => k.Tenant)
            .FirstOrDefaultAsync(k => k.KeyHash == keyHash && k.RevokedAt == null && k.Tenant.IsActive);

        if (key == null) return null;

        // ponytail: writes LastUsedAt on every request; batch or drop it if write load ever matters.
        key.LastUsedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        return key.Tenant;
    }

    private static string ComputeSha256Hash(string rawData) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawData))).ToLowerInvariant();
}

public static class TenantAuthenticationMiddlewareExtensions
{
    public static IApplicationBuilder UseTenantAuthentication(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<TenantAuthenticationMiddleware>();
    }
}
