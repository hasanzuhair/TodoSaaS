using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Amazon.S3;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using Prometheus;
using Scalar.AspNetCore;
using Sentry;
using Serilog;
using Stripe;
using TodoSaaS.Api.Consumers;
using TodoSaaS.Api.Data;
using TodoSaaS.Api.Events;
using TodoSaaS.Api.Models;
using TodoSaaS.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// 1. Structured Logging (Serilog)
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateLogger();

builder.Host.UseSerilog();

// 2. Error Tracking (Sentry)
builder.WebHost.UseSentry(options =>
{
    var dsn = builder.Configuration["Sentry:Dsn"];
    if (!string.IsNullOrEmpty(dsn))
    {
        options.Dsn = dsn;
        options.TracesSampleRate = 1.0;
        options.SendDefaultPii = false;
    }
});

// 3. Database: PostgreSQL with SQLite local fallback
var pgConnection = builder.Configuration.GetConnectionString("Postgres");
var dbProvider = builder.Configuration["DatabaseProvider"] ?? (string.IsNullOrEmpty(pgConnection) ? "Sqlite" : "Postgres");

builder.Services.AddDbContext<AppDbContext>(options =>
{
    if (dbProvider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
    {
        options.UseSqlite(builder.Configuration.GetConnectionString("Sqlite") ?? "Data Source=todos.db");
    }
    else
    {
        options.UseNpgsql(pgConnection, npgsqlOptions =>
        {
            npgsqlOptions.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorCodesToAdd: null);
        });
    }
});

// 4. Cache & Distributed Store: Redis (with in-memory fallback)
var redisConnection = builder.Configuration.GetConnectionString("Redis");
var useRedis = builder.Configuration.GetValue<bool>("UseRedis", !string.IsNullOrEmpty(redisConnection));
if (useRedis && !string.IsNullOrEmpty(redisConnection))
{
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = redisConnection;
        options.InstanceName = "TodoSaaS_";
    });
}
else
{
    builder.Services.AddDistributedMemoryCache();
}
builder.Services.AddSingleton<ICacheService, DistributedCacheService>();

// 5. Message Queue, Event Bus & Transactional Outbox (MassTransit)
var rabbitHost = builder.Configuration["RabbitMQ:Host"];
var useRabbitMq = builder.Configuration.GetValue<bool>("UseRabbitMQ", !string.IsNullOrEmpty(rabbitHost));
builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<TodoReminderConsumer>();

    // Point 2: Transactional Outbox ensures atomic database writes and message dispatching
    x.AddEntityFrameworkOutbox<AppDbContext>(o =>
    {
        if (dbProvider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            o.UseSqlite();
        }
        else
        {
            o.UsePostgres();
        }
        o.UseBusOutbox();
    });

    if (useRabbitMq && !string.IsNullOrEmpty(rabbitHost))
    {
        x.UsingRabbitMq((context, cfg) =>
        {
            var host = rabbitHost;
            var user = builder.Configuration["RabbitMQ:Username"] ?? "guest";
            var pass = builder.Configuration["RabbitMQ:Password"] ?? "guest";

            cfg.Host(host, "/", h =>
            {
                h.Username(user);
                h.Password(pass);
            });

            cfg.ConfigureEndpoints(context);
        });
    }
    else
    {
        x.UsingInMemory((context, cfg) =>
        {
            cfg.ConfigureEndpoints(context);
        });
    }
});

// 6. Object Storage: S3 / MinIO / Cloudflare R2
builder.Services.AddSingleton<IAmazonS3>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var s3Config = new AmazonS3Config
    {
        ServiceURL = config["Storage:ServiceUrl"] ?? "http://localhost:9000",
        ForcePathStyle = true,
        UseHttp = true
    };
    var accessKey = config["Storage:AccessKey"] ?? "minioadmin";
    var secretKey = config["Storage:SecretKey"] ?? "minioadmin";
    return new AmazonS3Client(accessKey, secretKey, s3Config);
});
builder.Services.AddScoped<IFileStorageService, S3FileStorageService>();

// 7. Email, Billing & Auth Services
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<IBillingService, StripeBillingService>();
builder.Services.AddSingleton<ITokenService, JwtTokenService>();

// 8. Multi-Tenancy Context
builder.Services.AddScoped<ITenantContext, TenantContext>();

// 9. CORS Policy
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

// 10. API Gateway & Rate Limiting (Token Bucket per IP / Tenant with Tiered Protection)
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsync(
            JsonSerializer.Serialize(new
            {
                error = "TooManyRequests",
                message = "Rate limit exceeded (120 requests/minute). Please slow down.",
                retryAfterSeconds = 60
            }), token);
    };
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
    {
        var key = httpContext.User.FindFirst("tenant_id")?.Value
                  ?? httpContext.Connection.RemoteIpAddress?.ToString()
                  ?? "anonymous";

        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 120,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 10
        });
    });
});

// 11. Point 1: Cryptographic Authentication & Authorization (ZITADEL / Clerk / Local JWT)
var oidcAuthority = builder.Configuration["Auth:Authority"];
var jwtSecret = builder.Configuration["Auth:JwtSecret"] ?? "SuperSecretTodoSaaSKeyForLocalDevelopmentAndTestingPurposesOnly1234567890";
var jwtIssuer = builder.Configuration["Auth:Issuer"] ?? "TodoSaaS";
var jwtAudience = builder.Configuration["Auth:Audience"] ?? "TodoSaaS.Clients";

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = false;
        options.SaveToken = true;

        if (!string.IsNullOrEmpty(oidcAuthority))
        {
            options.Authority = oidcAuthority;
            var metadataAddress = builder.Configuration["Auth:MetadataAddress"];
            if (!string.IsNullOrEmpty(metadataAddress))
            {
                options.MetadataAddress = metadataAddress;
            }
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateAudience = false,
                ValidIssuer = oidcAuthority,
                NameClaimType = ClaimTypes.NameIdentifier
            };
        }
        else
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = jwtIssuer,
                ValidateAudience = true,
                ValidAudience = jwtAudience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30)
            };
        }
    });
builder.Services.AddAuthorization();

// 12. Point 4: Deep Health Checks (Database, Cache, Liveness & Readiness)
var hcBuilder = builder.Services.AddHealthChecks();
if (dbProvider.Equals("Postgres", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(pgConnection))
{
    hcBuilder.AddNpgSql(pgConnection, name: "postgres", tags: new[] { "ready", "db" });
}
if (useRedis && !string.IsNullOrEmpty(redisConnection))
{
    hcBuilder.AddRedis(redisConnection, name: "redis", tags: new[] { "ready", "cache" });
}

// 13. Stripe SDK Configuration
StripeConfiguration.ApiKey = builder.Configuration["Stripe:SecretKey"];

// 14. OpenAPI Documentation & Scalar
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure Middleware Pipeline
app.UseSerilogRequestLogging();
app.UseHttpMetrics(); // Prometheus HTTP metrics

// Apply CORS, Authentication & Multi-Tenant Middleware
app.UseCors();
app.UseAuthentication();
app.UseMiddleware<TenantMiddleware>();

app.UseRateLimiter();
app.UseAuthorization();

// Static files for Single Page Dashboard UI
app.UseDefaultFiles();
app.UseStaticFiles();

// OpenAPI & Scalar API Interactive Reference
app.MapOpenApi();
app.MapScalarApiReference(options =>
{
    options.WithTitle("TodoSaaS API Documentation")
           .WithTheme(ScalarTheme.Mars);
});

// 15. Prometheus Metrics Endpoint
app.MapMetrics("/metrics");

// 16. Point 4: Deep Health Check Endpoints (/healthz, /healthz/live, /healthz/ready)
static Task WriteHealthResponseAsync(HttpContext context, HealthReport report)
{
    context.Response.ContentType = "application/json";
    var result = JsonSerializer.Serialize(new
    {
        status = report.Status.ToString(),
        totalDuration = $"{report.TotalDuration.TotalMilliseconds:F2}ms",
        entries = report.Entries.Select(e => new
        {
            key = e.Key,
            status = e.Value.Status.ToString(),
            duration = $"{e.Value.Duration.TotalMilliseconds:F2}ms",
            description = e.Value.Description,
            error = e.Value.Exception?.Message
        })
    }, new JsonSerializerOptions { WriteIndented = true });
    return context.Response.WriteAsync(result);
}

app.MapHealthChecks("/healthz", new HealthCheckOptions
{
    Predicate = _ => true,
    ResponseWriter = WriteHealthResponseAsync
});

app.MapHealthChecks("/healthz/live", new HealthCheckOptions
{
    Predicate = r => r.Tags.Contains("live") || !r.Tags.Any(),
    ResponseWriter = WriteHealthResponseAsync
});

app.MapHealthChecks("/healthz/ready", new HealthCheckOptions
{
    Predicate = r => r.Tags.Contains("ready"),
    ResponseWriter = WriteHealthResponseAsync
});

// 17. Point 3: Production Database Migrations & Default Tenant Seeding
using (var scope = app.Services.CreateScope())
{
    try
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (db.Database.IsRelational())
        {
            db.Database.Migrate();
        }
        else
        {
            db.Database.EnsureCreated();
        }

        var defaultTenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        if (!db.Tenants.Any(t => t.Id == defaultTenantId))
        {
            db.Tenants.Add(new Tenant
            {
                Id = defaultTenantId,
                Name = "Acme Corp (Default Tenant)",
                Plan = "Pro"
            });
            db.SaveChanges();
        }
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Could not run DB migrations during startup");
    }

    try
    {
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorageService>();
        await storage.EnsureBucketExistsAsync();
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Storage bucket check skipped or pending");
    }
}

// ==========================================
// API ENDPOINTS
// ==========================================

// --- AUTH & TOKEN ISSUANCE ---
app.MapPost("/api/auth/token", (TokenRequestDto dto, ITokenService tokenService) =>
{
    var tenantId = dto.TenantId ?? Guid.Parse("11111111-1111-1111-1111-111111111111");
    var email = string.IsNullOrWhiteSpace(dto.Email) ? "dev@example.com" : dto.Email.Trim();
    var token = tokenService.GenerateToken(tenantId, email, dto.Role ?? "Admin");
    return Results.Ok(new { token, tenantId, email, expiresIn = 604800 });
}).AllowAnonymous();

// --- TENANT MANAGEMENT ---
app.MapGet("/api/tenants", async (AppDbContext db) =>
{
    var tenants = await db.Tenants.OrderBy(t => t.Name).ToListAsync();
    return Results.Ok(tenants);
}).AllowAnonymous();

app.MapGet("/api/tenants/current", async (AppDbContext db, ITenantContext tenantContext, ICacheService cache, HttpContext httpContext) =>
{
    var tenant = await db.Tenants.FindAsync(tenantContext.TenantId);
    if (tenant is null)
    {
        return Results.NotFound(new { error = $"Tenant {tenantContext.TenantId} not found." });
    }

    var totalTodos = await db.Todos.CountAsync();
    var activeTodos = await db.Todos.CountAsync(t => !t.IsCompleted);
    var plan = tenant.Plan ?? "Free";
    var maxTodos = plan.Equals("Free", StringComparison.OrdinalIgnoreCase) ? 25 : -1;

    var response = new
    {
        tenant.Id,
        tenant.Name,
        tenant.Plan,
        tenant.StripeCustomerId,
        tenant.StripeSubscriptionId,
        tenant.CreatedAtUtc,
        Stats = new
        {
            TotalTodos = totalTodos,
            ActiveTodos = activeTodos,
            MaxTodos = maxTodos,
            QuotaUsedPercent = maxTodos > 0 ? Math.Min(100, (int)((double)totalTodos / maxTodos * 100)) : 0,
            IsQuotaExceeded = maxTodos > 0 && totalTodos >= maxTodos
        }
    };

    return Results.Ok(response);
}).RequireAuthorization();

app.MapPost("/api/tenants", async (TenantCreateDto dto, AppDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(dto.Name))
    {
        return Results.BadRequest(new { error = "Tenant name is required." });
    }

    var tenant = new Tenant
    {
        Name = dto.Name.Trim(),
        Plan = "Free"
    };

    db.Tenants.Add(tenant);
    await db.SaveChangesAsync();

    return Results.Created($"/api/tenants/{tenant.Id}", tenant);
}).RequireAuthorization();

// Self-Serve Automated Onboarding Flow
app.MapPost("/api/tenants/onboard", async (TenantOnboardDto dto, AppDbContext db, ITokenService tokenService) =>
{
    var orgName = string.IsNullOrWhiteSpace(dto.OrganizationName) ? "My Team Workspace" : dto.OrganizationName.Trim();
    var email = string.IsNullOrWhiteSpace(dto.AdminEmail) ? "admin@myworkspace.local" : dto.AdminEmail.Trim();

    var tenant = new Tenant
    {
        Name = orgName,
        Plan = string.IsNullOrWhiteSpace(dto.Plan) ? "Free" : dto.Plan.Trim(),
        CreatedAtUtc = DateTime.UtcNow
    };

    db.Tenants.Add(tenant);

    // Seed welcoming starter tasks
    var welcomeTodo = new TodoItem
    {
        TenantId = tenant.Id,
        Title = "🚀 Welcome to TodoSaaS! Complete this onboarding task",
        Description = "Explore your multi-tenant workspace, upload files to MinIO, and test real-time Redis caching.",
        IsCompleted = false,
        CreatedAtUtc = DateTime.UtcNow
    };
    db.Todos.Add(welcomeTodo);

    await db.SaveChangesAsync();

    var token = tokenService.GenerateToken(tenant.Id, email, "Admin");

    return Results.Created($"/api/tenants/{tenant.Id}", new
    {
        tenant,
        token,
        email,
        message = "Workspace successfully onboarded with starter data!"
    });
}).AllowAnonymous();

// GDPR Right to Data Portability
app.MapGet("/api/tenants/export", async (AppDbContext db, ITenantContext tenantContext) =>
{
    var tenant = await db.Tenants.FindAsync(tenantContext.TenantId);
    if (tenant is null) return Results.NotFound();

    var todos = await db.Todos.OrderByDescending(t => t.CreatedAtUtc).ToListAsync();

    var exportData = new
    {
        ExportedAtUtc = DateTime.UtcNow,
        Tenant = tenant,
        TotalItems = todos.Count,
        Items = todos
    };

    var json = JsonSerializer.Serialize(exportData, new JsonSerializerOptions { WriteIndented = true });
    var bytes = Encoding.UTF8.GetBytes(json);

    return Results.File(bytes, "application/json", $"todosaas-export-{tenant.Id}.json");
}).RequireAuthorization();

// GDPR Right to Erasure
app.MapDelete("/api/tenants/current", async (AppDbContext db, ITenantContext tenantContext, ICacheService cache) =>
{
    var tenant = await db.Tenants.FindAsync(tenantContext.TenantId);
    if (tenant is null) return Results.NotFound();

    var todos = await db.Todos.ToListAsync();
    db.Todos.RemoveRange(todos);

    db.Tenants.Remove(tenant);
    await db.SaveChangesAsync();

    await cache.InvalidateVersionAsync($"tenant:{tenantContext.TenantId}:todos:version");
    await cache.RemoveAsync($"tenant:{tenantContext.TenantId}:info");

    return Results.Ok(new { message = $"Tenant {tenant.Name} and all associated items permanently purged in compliance with GDPR." });
}).RequireAuthorization();

// --- TODO MANAGEMENT (WITH DISTRIBUTED CACHE & OUTBOX EVENT PUBLISHING) ---

app.MapGet("/api/todos", async (bool? completed, string? search, AppDbContext db, ICacheService cache, ITenantContext tenantContext, HttpContext httpContext) =>
{
    var tenantId = tenantContext.TenantId;
    var version = await cache.GetVersionAsync($"tenant:{tenantId}:todos:version");
    var cacheKey = $"tenant:{tenantId}:todos:v{version}:c_{completed}:s_{search}";

    var cachedTodos = await cache.GetAsync<List<TodoItem>>(cacheKey);
    if (cachedTodos != null)
    {
        httpContext.Response.Headers.Append("X-Cache", "HIT");
        return Results.Ok(cachedTodos);
    }

    var query = db.Todos.AsQueryable();

    if (completed.HasValue)
    {
        query = query.Where(t => t.IsCompleted == completed.Value);
    }

    if (!string.IsNullOrWhiteSpace(search))
    {
        query = query.Where(t => t.Title.Contains(search) || (t.Description != null && t.Description.Contains(search)));
    }

    var todos = await query.OrderByDescending(t => t.CreatedAtUtc).ToListAsync();

    await cache.SetAsync(cacheKey, todos, TimeSpan.FromMinutes(5));
    httpContext.Response.Headers.Append("X-Cache", "MISS");

    return Results.Ok(todos);
}).RequireAuthorization();

app.MapGet("/api/todos/{id:guid}", async (Guid id, AppDbContext db) =>
{
    var todo = await db.Todos.FirstOrDefaultAsync(t => t.Id == id);
    return todo is not null ? Results.Ok(todo) : Results.NotFound();
}).RequireAuthorization();

app.MapPost("/api/todos", async (TodoCreateDto dto, AppDbContext db, IPublishEndpoint publishEndpoint, ITenantContext tenantContext, ICacheService cache) =>
{
    if (string.IsNullOrWhiteSpace(dto.Title))
    {
        return Results.BadRequest(new { error = "Title is required." });
    }

    // Quota Enforcement: Free tier is capped at 25 tasks
    var tenant = await db.Tenants.FindAsync(tenantContext.TenantId);
    var plan = tenant?.Plan ?? "Free";
    if (plan.Equals("Free", StringComparison.OrdinalIgnoreCase))
    {
        var existingCount = await db.Todos.CountAsync();
        if (existingCount >= 25)
        {
            return Results.Json(new
            {
                error = "QuotaExceeded",
                message = "Free tier limit of 25 tasks reached. Upgrade to Pro for unlimited tasks.",
                limit = 25,
                current = existingCount,
                upgradeUrl = "/api/billing/checkout"
            }, statusCode: StatusCodes.Status402PaymentRequired);
        }
    }

    var todo = new TodoItem
    {
        Title = dto.Title.Trim(),
        Description = dto.Description,
        DueDateUtc = dto.DueDateUtc,
        AttachmentUrl = dto.AttachmentUrl
    };

    db.Todos.Add(todo);

    // Atomically written to transactional outbox in same database commit
    await publishEndpoint.Publish(new TodoCreatedEvent(todo.Id, tenantContext.TenantId, todo.Title, todo.CreatedAtUtc));
    await db.SaveChangesAsync();

    // Invalidate tenant todos cache
    await cache.InvalidateVersionAsync($"tenant:{tenantContext.TenantId}:todos:version");

    return Results.Created($"/api/todos/{todo.Id}", todo);
}).RequireAuthorization();

app.MapPut("/api/todos/{id:guid}", async (Guid id, TodoUpdateDto dto, AppDbContext db, ICacheService cache, ITenantContext tenantContext) =>
{
    var todo = await db.Todos.FirstOrDefaultAsync(t => t.Id == id);
    if (todo is null) return Results.NotFound();

    if (!string.IsNullOrWhiteSpace(dto.Title))
    {
        todo.Title = dto.Title.Trim();
    }
    if (dto.Description != null)
    {
        todo.Description = dto.Description;
    }
    if (dto.IsCompleted.HasValue)
    {
        todo.IsCompleted = dto.IsCompleted.Value;
        todo.CompletedAtUtc = todo.IsCompleted ? DateTime.UtcNow : null;
    }
    if (dto.DueDateUtc.HasValue)
    {
        todo.DueDateUtc = dto.DueDateUtc.Value;
    }
    if (dto.AttachmentUrl != null)
    {
        todo.AttachmentUrl = dto.AttachmentUrl;
    }

    await db.SaveChangesAsync();
    await cache.InvalidateVersionAsync($"tenant:{tenantContext.TenantId}:todos:version");

    return Results.Ok(todo);
}).RequireAuthorization();

app.MapDelete("/api/todos/{id:guid}", async (Guid id, AppDbContext db, ICacheService cache, ITenantContext tenantContext) =>
{
    var todo = await db.Todos.FirstOrDefaultAsync(t => t.Id == id);
    if (todo is null) return Results.NotFound();

    db.Todos.Remove(todo);
    await db.SaveChangesAsync();
    await cache.InvalidateVersionAsync($"tenant:{tenantContext.TenantId}:todos:version");

    return Results.NoContent();
}).RequireAuthorization();

app.MapPost("/api/todos/{id:guid}/complete", async (Guid id, AppDbContext db, IPublishEndpoint publishEndpoint, ITenantContext tenantContext, ICacheService cache) =>
{
    var todo = await db.Todos.FirstOrDefaultAsync(t => t.Id == id);
    if (todo is null) return Results.NotFound();

    todo.IsCompleted = true;
    todo.CompletedAtUtc = DateTime.UtcNow;

    // Atomically published via Outbox
    await publishEndpoint.Publish(new TodoCompletedEvent(todo.Id, tenantContext.TenantId, todo.Title, todo.CompletedAtUtc.Value));
    await db.SaveChangesAsync();

    await cache.InvalidateVersionAsync($"tenant:{tenantContext.TenantId}:todos:version");

    return Results.Ok(todo);
}).RequireAuthorization();

app.MapPost("/api/todos/{id:guid}/reminder", async (Guid id, string email, AppDbContext db, IPublishEndpoint publishEndpoint, ITenantContext tenantContext) =>
{
    var todo = await db.Todos.FirstOrDefaultAsync(t => t.Id == id);
    if (todo is null) return Results.NotFound();

    var dueDate = todo.DueDateUtc ?? DateTime.UtcNow.AddDays(1);
    await publishEndpoint.Publish(new SendReminderEvent(todo.Id, tenantContext.TenantId, email, todo.Title, dueDate));
    await db.SaveChangesAsync();

    return Results.Accepted(value: new { message = "Reminder queued successfully via transactional outbox." });
}).RequireAuthorization();

// --- OBJECT STORAGE ---
app.MapGet("/api/storage/presigned-url", async (string fileName, string contentType, IFileStorageService storageService) =>
{
    var url = await storageService.GetPresignedUploadUrlAsync(fileName, contentType);
    return Results.Ok(new { uploadUrl = url });
}).RequireAuthorization();

app.MapPost("/api/storage/upload", async (IFormFile file, IFileStorageService storageService) =>
{
    if (file == null || file.Length == 0)
    {
        return Results.BadRequest(new { error = "No file uploaded." });
    }

    using var stream = file.OpenReadStream();
    var key = await storageService.UploadFileAsync(file.FileName, stream, file.ContentType);
    var downloadUrl = await storageService.GetDownloadUrlAsync(key);

    return Results.Ok(new { key, downloadUrl, fileName = file.FileName, size = file.Length });
}).DisableAntiforgery().RequireAuthorization();

// --- BILLING & STRIPE ---
app.MapPost("/api/billing/checkout", async (CheckoutRequestDto dto, IBillingService billingService, ITenantContext tenantContext) =>
{
    var checkoutUrl = await billingService.CreateCheckoutSessionAsync(
        tenantContext.TenantId,
        dto.Email,
        dto.SuccessUrl ?? "http://localhost:8080/billing/success",
        dto.CancelUrl ?? "http://localhost:8080/billing/cancel"
    );
    return Results.Ok(new { checkoutUrl });
}).RequireAuthorization();

app.MapPost("/api/billing/portal", async (string stripeCustomerId, string returnUrl, IBillingService billingService) =>
{
    var portalUrl = await billingService.CreateCustomerPortalSessionAsync(stripeCustomerId, returnUrl);
    return Results.Ok(new { portalUrl });
}).RequireAuthorization();

// Instant plan simulation for demo, onboarding, and local testing
app.MapPost("/api/billing/simulate-upgrade", async (SimulateUpgradeDto dto, AppDbContext db, ITenantContext tenantContext, ICacheService cache) =>
{
    var tenant = await db.Tenants.FindAsync(tenantContext.TenantId);
    if (tenant is null) return Results.NotFound();

    var targetPlan = string.IsNullOrWhiteSpace(dto.Plan) ? "Pro" : dto.Plan.Trim();
    tenant.Plan = targetPlan;
    if (string.IsNullOrEmpty(tenant.StripeCustomerId))
    {
        tenant.StripeCustomerId = $"cus_sim_{Guid.NewGuid():N}"[..18];
    }
    tenant.StripeSubscriptionId = $"sub_sim_{Guid.NewGuid():N}"[..18];

    await db.SaveChangesAsync();
    await cache.RemoveAsync($"tenant:{tenantContext.TenantId}:info");

    return Results.Ok(new
    {
        message = $"Successfully updated plan to {targetPlan}!",
        tenantId = tenant.Id,
        plan = tenant.Plan,
        stripeCustomerId = tenant.StripeCustomerId
    });
}).RequireAuthorization();

// Stripe Webhook: Unauthenticated endpoint verified via cryptographic signature
app.MapPost("/api/billing/webhook", async (HttpRequest request, IBillingService billingService) =>
{
    using var reader = new StreamReader(request.Body);
    var json = await reader.ReadToEndAsync();
    var signature = request.Headers["Stripe-Signature"].ToString();

    await billingService.HandleWebhookEventAsync(json, signature);
    return Results.Ok();
}).AllowAnonymous();

app.Run();
