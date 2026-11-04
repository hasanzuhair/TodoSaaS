using System.Security.Claims;

namespace TodoSaaS.Api.Services;

public interface ITenantContext
{
    Guid TenantId { get; }
    void SetTenantId(Guid tenantId);
}

public class TenantContext : ITenantContext
{
    // Default fallback tenant for dev testing
    private Guid _tenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public Guid TenantId => _tenantId;

    public void SetTenantId(Guid tenantId)
    {
        _tenantId = tenantId;
    }
}

public class TenantMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IConfiguration _config;

    public TenantMiddleware(RequestDelegate next, IConfiguration config)
    {
        _next = next;
        _config = config;
    }

    public async Task InvokeAsync(HttpContext context, ITenantContext tenantContext)
    {
        // 1. Try reading TenantId from validated JWT claims (ZITADEL, Clerk, Auth0, Identity, JwtTokenService)
        var claimTenantId = context.User.FindFirst("tenant_id")?.Value
                         ?? context.User.FindFirst("org_id")?.Value
                         ?? context.User.FindFirst("urn:zitadel:iam:org:id")?.Value;

        if (!string.IsNullOrEmpty(claimTenantId) && Guid.TryParse(claimTenantId, out var parsedClaimId))
        {
            tenantContext.SetTenantId(parsedClaimId);
        }
        // 2. Fallback to X-Tenant-ID header (authenticated via API Key or in local Dev mode)
        else if (context.Request.Headers.TryGetValue("X-Tenant-ID", out var headerTenantId)
                 && Guid.TryParse(headerTenantId.FirstOrDefault(), out var parsedHeaderId))
        {
            tenantContext.SetTenantId(parsedHeaderId);
        }

        await _next(context);
    }
}
