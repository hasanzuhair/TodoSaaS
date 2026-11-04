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
