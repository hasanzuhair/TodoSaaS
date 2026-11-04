namespace TodoSaaS.Api.Events;

public record TodoCreatedEvent(Guid TodoId, Guid TenantId, string Title, DateTime CreatedAtUtc);
public record TodoCompletedEvent(Guid TodoId, Guid TenantId, string Title, DateTime CompletedAtUtc);
public record SendReminderEvent(Guid TodoId, Guid TenantId, string Email, string Title, DateTime DueDateUtc);
