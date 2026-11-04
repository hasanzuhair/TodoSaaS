using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TodoSaaS.Api.Data;
using TodoSaaS.Api.Models;

namespace TodoSaaS.Api.Services;

public interface IAuditService
{
    Task RecordAsync(string action, string entityName, string entityId, object? diff = null);
    Task<List<AuditLog>> GetLogsAsync(int page = 1, int pageSize = 50);
}

public class AuditService : IAuditService
{
    private readonly AppDbContext _db;
    private readonly ITenantContext _tenantContext;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<AuditService> _logger;

    public AuditService(AppDbContext db, ITenantContext tenantContext, IHttpContextAccessor httpContextAccessor, ILogger<AuditService> logger)
    {
        _db = db;
        _tenantContext = tenantContext;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public async Task RecordAsync(string action, string entityName, string entityId, object? diff = null)
    {
        var httpContext = _httpContextAccessor.HttpContext;
        var email = httpContext?.User.FindFirst("email")?.Value 
                    ?? httpContext?.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                    ?? "system";
        var actorId = httpContext?.User.FindFirst("sub")?.Value ?? email;
        var ip = httpContext?.Connection.RemoteIpAddress?.ToString();
        var userAgent = httpContext?.Request.Headers["User-Agent"].ToString();

        var log = new AuditLog
        {
            TenantId = _tenantContext.TenantId,
            ActorId = actorId,
            ActorEmail = email,
            Action = action,
            EntityName = entityName,
            EntityId = entityId,
            ChangesJson = diff != null ? JsonSerializer.Serialize(diff) : null,
            IpAddress = ip,
            UserAgent = userAgent,
            TimestampUtc = DateTime.UtcNow
        };

        _db.AuditLogs.Add(log);
        await _db.SaveChangesAsync();
        _logger.LogInformation("AuditLog: [{Action}] {EntityName}:{EntityId} by {ActorEmail}", action, entityName, entityId, email);
    }

    public async Task<List<AuditLog>> GetLogsAsync(int page = 1, int pageSize = 50)
    {
        return await _db.AuditLogs
            .OrderByDescending(a => a.TimestampUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
    }
}
