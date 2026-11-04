using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Stripe;
using Stripe.Checkout;
using TodoSaaS.Api.Data;
using TodoSaaS.Api.Models;

namespace TodoSaaS.Api.Services;

public interface IBillingService
{
    Task<string> CreateCheckoutSessionAsync(Guid tenantId, string customerEmail, string successUrl, string cancelUrl);
    Task<string> CreateCustomerPortalSessionAsync(string stripeCustomerId, string returnUrl);
    Task HandleWebhookEventAsync(string jsonPayload, string stripeSignature);
}

public class StripeBillingService : IBillingService
{
    private readonly IConfiguration _config;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<StripeBillingService> _logger;

    public StripeBillingService(IConfiguration config, IServiceProvider serviceProvider, ILogger<StripeBillingService> logger)
    {
        _config = config;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task<string> CreateCheckoutSessionAsync(Guid tenantId, string customerEmail, string successUrl, string cancelUrl)
    {
        var apiKey = _config["Stripe:SecretKey"];
        if (string.IsNullOrEmpty(apiKey) || apiKey.StartsWith("sk_test_mock") || apiKey.StartsWith("mock_"))
        {
            _logger.LogInformation("Using mock Stripe checkout session for tenant {TenantId}", tenantId);
            return $"{successUrl}?session_id=cs_mock_{tenantId:N}&mock=true";
        }

        var priceId = _config["Stripe:ProPlanPriceId"] ?? "price_pro_subscription";
        var options = new SessionCreateOptions
        {
            PaymentMethodTypes = new List<string> { "card" },
            Mode = "subscription",
            CustomerEmail = customerEmail,
            LineItems = new List<SessionLineItemOptions>
            {
                new SessionLineItemOptions
                {
                    Price = priceId,
                    Quantity = 1,
                },
            },
            Metadata = new Dictionary<string, string>
            {
                { "tenant_id", tenantId.ToString() }
            },
            SuccessUrl = successUrl,
            CancelUrl = cancelUrl,
        };

        var service = new SessionService();
        var session = await service.CreateAsync(options);
        return session.Url;
    }

    public async Task<string> CreateCustomerPortalSessionAsync(string stripeCustomerId, string returnUrl)
    {
        var apiKey = _config["Stripe:SecretKey"];
        if (string.IsNullOrEmpty(apiKey) || apiKey.StartsWith("sk_test_mock") || apiKey.StartsWith("mock_"))
        {
            _logger.LogInformation("Using mock Stripe customer portal session for customer {CustomerId}", stripeCustomerId);
            return $"{returnUrl}?portal_mock=true";
        }

        var options = new Stripe.BillingPortal.SessionCreateOptions
        {
            Customer = stripeCustomerId,
            ReturnUrl = returnUrl,
        };
        var service = new Stripe.BillingPortal.SessionService();
        var session = await service.CreateAsync(options);
        return session.Url;
    }

    public async Task HandleWebhookEventAsync(string jsonPayload, string stripeSignature)
    {
        var webhookSecret = _config["Stripe:WebhookSecret"];
        string eventId = "";
        string eventType = "";
        string? tenantIdStr = null;
        string? customerId = null;
        string? subscriptionId = null;

        try
        {
            if (!string.IsNullOrEmpty(stripeSignature) && !string.IsNullOrEmpty(webhookSecret) && !webhookSecret.StartsWith("whsec_mock"))
            {
                var stripeEvent = EventUtility.ConstructEvent(jsonPayload, stripeSignature, webhookSecret);
                eventId = stripeEvent.Id;
                eventType = stripeEvent.Type;
                if (stripeEvent.Data.Object is Session session)
                {
                    session.Metadata?.TryGetValue("tenant_id", out tenantIdStr);
                    customerId = session.CustomerId;
                    subscriptionId = session.SubscriptionId;
                }
                else if (stripeEvent.Data.Object is Subscription sub)
                {
                    subscriptionId = sub.Id;
                }
            }
            else
            {
                using var doc = JsonDocument.Parse(jsonPayload);
                var root = doc.RootElement;
                if (root.TryGetProperty("id", out var idProp)) eventId = idProp.GetString() ?? "";
                if (root.TryGetProperty("type", out var typeProp)) eventType = typeProp.GetString() ?? "";

                if (root.TryGetProperty("data", out var dataProp) && dataProp.TryGetProperty("object", out var objProp))
                {
                    if (objProp.TryGetProperty("customer", out var cusProp)) customerId = cusProp.GetString();
                    if (objProp.TryGetProperty("subscription", out var subProp)) subscriptionId = subProp.GetString();
                    if (objProp.TryGetProperty("id", out var objIdProp) && eventType.StartsWith("customer.subscription"))
                    {
                        subscriptionId = objIdProp.GetString();
                    }
                    if (objProp.TryGetProperty("metadata", out var metaProp) && metaProp.TryGetProperty("tenant_id", out var tidProp))
                    {
                        tenantIdStr = tidProp.GetString();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse Stripe webhook event");
            return;
        }

        if (string.IsNullOrEmpty(eventId)) return;

        _logger.LogInformation("Processing Stripe Event: {EventType} (ID: {EventId})", eventType, eventId);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Point 6: Webhook Idempotency Check
        if (await db.ProcessedWebhookEvents.AnyAsync(e => e.Id == eventId))
        {
            _logger.LogInformation("Duplicate Stripe webhook received: {EventId}. Skipping processing.", eventId);
            return;
        }

        if (eventType == Stripe.EventTypes.CheckoutSessionCompleted)
        {
            if (!string.IsNullOrEmpty(tenantIdStr) && Guid.TryParse(tenantIdStr, out var tenantId))
            {
                var tenant = await db.Tenants.FindAsync(tenantId);
                if (tenant != null)
                {
                    tenant.Plan = "Pro";
                    tenant.StripeCustomerId = customerId;
                    tenant.StripeSubscriptionId = subscriptionId;
                    _logger.LogInformation("Tenant {TenantId} successfully upgraded to Pro plan!", tenantId);
                }
            }
        }
        else if (eventType == Stripe.EventTypes.CustomerSubscriptionDeleted)
        {
            if (!string.IsNullOrEmpty(subscriptionId))
            {
                var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.StripeSubscriptionId == subscriptionId);
                if (tenant != null)
                {
                    tenant.Plan = "Free";
                    _logger.LogInformation("Tenant {TenantId} subscription cancelled; reverted to Free plan.", tenant.Id);
                }
            }
        }
        else if (eventType == Stripe.EventTypes.InvoicePaymentFailed)
        {
            _logger.LogWarning("Stripe invoice payment failed for event {EventId}", eventId);
        }

        // Mark event as processed atomically
        db.ProcessedWebhookEvents.Add(new ProcessedWebhookEvent
        {
            Id = eventId,
            EventType = eventType,
            ProcessedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }
}
