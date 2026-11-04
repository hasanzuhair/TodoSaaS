namespace TodoSaaS.Api.Models;

public interface IMustHaveTenant
{
    Guid TenantId { get; set; }
}

public class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Plan { get; set; } = "Free"; // Free, Pro, Enterprise
    public string? StripeCustomerId { get; set; }
    public string? StripeSubscriptionId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class TodoItem : IMustHaveTenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsCompleted { get; set; }
    public DateTime? DueDateUtc { get; set; }
    public string? AttachmentUrl { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }
}

// Request & Response DTOs
public record TodoCreateDto(string Title, string? Description, DateTime? DueDateUtc, string? AttachmentUrl);
public record TodoUpdateDto(string? Title, string? Description, bool? IsCompleted, DateTime? DueDateUtc, string? AttachmentUrl);
public record TenantCreateDto(string Name);
public record TenantOnboardDto(string OrganizationName, string? AdminEmail, string? Plan);
public record CheckoutRequestDto(string Email, string? SuccessUrl, string? CancelUrl);
public record TokenRequestDto(Guid? TenantId, string? Email, string? Role);
public record SimulateUpgradeDto(string? Plan);

public class ProcessedWebhookEvent
{
    public string Id { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public DateTime ProcessedAtUtc { get; set; } = DateTime.UtcNow;
}

// Immutable SOC 2 / HIPAA Audit Log Trail
public class AuditLog : IMustHaveTenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string ActorId { get; set; } = string.Empty;
    public string ActorEmail { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty; // e.g. "todo.created", "todo.deleted", "tenant.upgraded"
    public string EntityName { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string? ChangesJson { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
}
