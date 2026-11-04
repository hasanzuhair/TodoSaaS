using MassTransit;
using TodoSaaS.Api.Events;
using TodoSaaS.Api.Services;

namespace TodoSaaS.Api.Consumers;

public class TodoReminderConsumer : IConsumer<SendReminderEvent>, IConsumer<TodoCompletedEvent>
{
    private readonly IEmailService _emailService;
    private readonly ILogger<TodoReminderConsumer> _logger;

    public TodoReminderConsumer(IEmailService emailService, ILogger<TodoReminderConsumer> logger)
    {
        _emailService = emailService;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<SendReminderEvent> context)
    {
        var msg = context.Message;
        _logger.LogInformation("Processing Reminder for Todo: {Title} for Tenant: {TenantId}", msg.Title, msg.TenantId);

        var body = $"<h1>Reminder: Task Due Soon!</h1><p>Your task <strong>{msg.Title}</strong> is due at {msg.DueDateUtc:yyyy-MM-dd HH:mm UTC}.</p>";
        await _emailService.SendEmailAsync(msg.Email, $"Reminder: {msg.Title}", body);
    }

    public Task Consume(ConsumeContext<TodoCompletedEvent> context)
    {
        var msg = context.Message;
        _logger.LogInformation("Todo Completed: {Title} (ID: {TodoId}) at {CompletedAt}", msg.Title, msg.TodoId, msg.CompletedAtUtc);
        return Task.CompletedTask;
    }
}
