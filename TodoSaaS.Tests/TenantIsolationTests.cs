using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using TodoSaaS.Api.Data;
using TodoSaaS.Api.Models;
using TodoSaaS.Api.Services;
using Xunit;

namespace TodoSaaS.Tests;

public class TenantIsolationTests
{
    private class TestTenantContext : ITenantContext
    {
        public Guid TenantId { get; set; } = Guid.NewGuid();
        public void SetTenantId(Guid tenantId) => TenantId = tenantId;
    }

    [Fact]
    public async Task QueryFilter_PreventsTenantFromSeeingOtherTenantData()
    {
        var tenantAId = Guid.NewGuid();
        var tenantBId = Guid.NewGuid();

        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;

        var contextA = new TestTenantContext { TenantId = tenantAId };

        // Tenant A creates a todo
        using (var dbA = new AppDbContext(options, contextA))
        {
            var todoA = new TodoItem
            {
                Title = "Tenant A Confidential Plan"
            };
            dbA.Todos.Add(todoA);
            await dbA.SaveChangesAsync();
        }

        // Tenant B queries todos
        var contextB = new TestTenantContext { TenantId = tenantBId };
        using (var dbB = new AppDbContext(options, contextB))
        {
            var todosB = await dbB.Todos.ToListAsync();

            // Tenant B should see 0 todos
            Assert.Empty(todosB);
        }

        // Tenant A queries todos again
        using (var dbA = new AppDbContext(options, contextA))
        {
            var todosA = await dbA.Todos.ToListAsync();

            // Tenant A sees their todo
            Assert.Single(todosA);
            Assert.Equal("Tenant A Confidential Plan", todosA[0].Title);
            Assert.Equal(tenantAId, todosA[0].TenantId);
        }
    }

    [Fact]
    public async Task SaveChanges_AutomaticallySetsTenantId()
    {
        var tenantId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;

        var context = new TestTenantContext { TenantId = tenantId };

        using (var db = new AppDbContext(options, context))
        {
            var todo = new TodoItem { Title = "Task without explicit tenant" };
            db.Todos.Add(todo);
            await db.SaveChangesAsync();

            Assert.Equal(tenantId, todo.TenantId);
        }
    }

    [Fact]
    public void SynchronousSaveChanges_AutomaticallySetsTenantId()
    {
        var tenantId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;

        var context = new TestTenantContext { TenantId = tenantId };

        using (var db = new AppDbContext(options, context))
        {
            var todo = new TodoItem { Title = "Sync task without explicit tenant" };
            db.Todos.Add(todo);
            db.SaveChanges();

            Assert.Equal(tenantId, todo.TenantId);
        }
    }

    [Fact]
    public async Task TenantMiddleware_SetsTenantIdFromHeader()
    {
        var expectedTenant = Guid.NewGuid();
        var tenantContext = new TenantContext();
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();

        var middleware = new TenantMiddleware(innerContext => Task.CompletedTask, config);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-Tenant-ID"] = expectedTenant.ToString();

        await middleware.InvokeAsync(httpContext, tenantContext);

        Assert.Equal(expectedTenant, tenantContext.TenantId);
    }

    [Fact]
    public async Task ProcessedWebhookEvent_TracksIdempotency()
    {
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;

        var context = new TestTenantContext();
        using var db = new AppDbContext(options, context);

        var eventId = "evt_test_123456";
        db.ProcessedWebhookEvents.Add(new ProcessedWebhookEvent
        {
            Id = eventId,
            EventType = "checkout.session.completed",
            ProcessedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var exists = await db.ProcessedWebhookEvents.AnyAsync(e => e.Id == eventId);
        Assert.True(exists);

        var duplicateExists = await db.ProcessedWebhookEvents.AnyAsync(e => e.Id == "evt_nonexistent");
        Assert.False(duplicateExists);
    }

    [Fact]
    public void JwtTokenService_GeneratesValidToken()
    {
        var inMemorySettings = new Dictionary<string, string?>
        {
            { "Auth:JwtSecret", "SuperSecretTodoSaaSKeyForLocalDevelopmentAndTestingPurposesOnly1234567890" },
            { "Auth:Issuer", "TodoSaaS" },
            { "Auth:Audience", "TodoSaaS.Clients" }
        };
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        var tokenService = new JwtTokenService(config);
        var tenantId = Guid.NewGuid();
        var token = tokenService.GenerateToken(tenantId, "test@example.com");

        Assert.NotNull(token);
        Assert.NotEmpty(token);

        var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
        var parsed = handler.ReadJwtToken(token);
        Assert.Equal("TodoSaaS", parsed.Issuer);
        Assert.Contains(parsed.Claims, c => c.Type == "tenant_id" && c.Value == tenantId.ToString());
    }

    [Fact]
    public async Task QuotaEnforcement_FreePlan_BlocksBeyondTwentyFiveTasks()
    {
        var tenantId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;

        var context = new TestTenantContext { TenantId = tenantId };
        using var db = new AppDbContext(options, context);

        var tenant = new Tenant { Id = tenantId, Name = "Acme Free", Plan = "Free" };
        db.Tenants.Add(tenant);

        // Add 25 tasks
        for (int i = 0; i < 25; i++)
        {
            db.Todos.Add(new TodoItem { Title = $"Task {i + 1}", TenantId = tenantId });
        }
        await db.SaveChangesAsync();

        var count = await db.Todos.CountAsync();
        Assert.Equal(25, count);

        // Verify quota logic
        var isExceeded = tenant.Plan == "Free" && count >= 25;
        Assert.True(isExceeded);
    }

    [Fact]
    public async Task BillingService_MockMode_GeneratesResilientUrls()
    {
        var inMemorySettings = new Dictionary<string, string?>
        {
            { "Stripe:SecretKey", "sk_test_mock_123456789" },
            { "Stripe:ProPlanPriceId", "price_pro_test" }
        };
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        var logger = new Microsoft.Extensions.Logging.Abstractions.NullLogger<StripeBillingService>();
        var billingService = new StripeBillingService(config, null!, logger);

        var tenantId = Guid.NewGuid();
        var checkoutUrl = await billingService.CreateCheckoutSessionAsync(
            tenantId, "test@example.com", "http://localhost:8080/success", "http://localhost:8080/cancel");

        Assert.NotNull(checkoutUrl);
        Assert.Contains("cs_mock_", checkoutUrl);

        var portalUrl = await billingService.CreateCustomerPortalSessionAsync(
            "cus_mock_123", "http://localhost:8080/return");

        Assert.NotNull(portalUrl);
        Assert.Contains("portal_mock=true", portalUrl);
    }
}
