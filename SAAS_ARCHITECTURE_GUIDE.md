# Enterprise Multi-Tenant SaaS Architecture & Blueprint Guide

> **A Complete Production-Grade Reference and Step-by-Step Tutorial for Modern SaaS Applications in .NET 9**  
> *Easily adaptable to any domain: CRM, E-Commerce, Invoicing, Healthcare, Project Management, or ERP.*

---

## Table of Contents
1. [Executive Architecture Overview](#1-executive-architecture-overview)
2. [Master Package Catalog & Roles](#2-master-package-catalog--roles)
3. [How the System Works Under the Hood](#3-how-the-system-works-under-the-hood)
4. [Step-by-Step Tutorial for Beginners (Build Any SaaS)](#4-step-by-step-tutorial-for-beginners-build-any-saas)
   - [Phase 1: Multi-Tenant Database Layer](#phase-1-multi-tenant-database-layer-ef-core)
   - [Phase 2: Authentication & Identity (ZITADEL & JWT)](#phase-2-authentication--identity-zitadel--jwt)
   - [Phase 3: Transactional Outbox & Async Messaging](#phase-3-transactional-outbox--async-messaging-masstransit--rabbitmq)
   - [Phase 4: Distributed Cache with Tenant Versioning](#phase-4-distributed-cache-with-tenant-versioning-redis)
   - [Phase 5: Billing, Stripe Webhooks & Idempotency](#phase-5-billing-stripe-webhooks--idempotency)
   - [Phase 6: Plan Quota Enforcement & Rate Limiting](#phase-6-plan-quota-enforcement--rate-limiting)
   - [Phase 7: Deep Health Checks & Observability](#phase-7-deep-health-checks--observability)
   - [Phase 8: Frontend UI & Self-Serve Onboarding](#phase-8-frontend-ui--self-serve-onboarding)
5. [Adapting This Architecture to Other Domains](#5-adapting-this-architecture-to-other-domains)
6. [Operational Runbook & Quick Commands](#6-operational-runbook--quick-commands)
7. [The 7 Modern Software Engineering Standards](#7-the-7-modern-software-engineering-standards)
8. [Multi-Environment Lifecycle (Dev, Staging, Production)](#8-multi-environment-lifecycle-dev-staging-production)

---

## 1. Executive Architecture Overview

This architecture implements a **cloud-native, horizontally scalable, multi-tenant distributed system** designed to withstand real-world failure modes (split-brain writes, cache stampedes, ghost webhooks, noisy neighbors, and cold starts).

```mermaid
flowchart TD
    Client["Web App / Mobile Client"] -->|HTTPS / REST| Ingress["Reverse Proxy (Caddy / Ingress)"]
    Ingress --> RateLimiter["ASP.NET Core RateLimiter (120 req/min)"]
    RateLimiter --> AuthMiddleware["Auth & Tenant Middleware"]
    
    AuthMiddleware -->|Validate JWT| Zitadel["ZITADEL OIDC Server (Port 8085)"]
    AuthMiddleware --> Controller["Minimal API Endpoints"]

    subgraph Caching Layer
        Controller <-->|Cache-Aside (v-tag)| Redis[("Redis Distributed Cache")]
    end

    subgraph Data & Outbox Layer
        Controller -->|1. Atomic Write| PG[("PostgreSQL 16 (Tenant-Isolated)")]
        Controller -->|2. Outbox Table| PG
    end

    subgraph Async Message Broker
        OutboxWorker["MassTransit Outbox Worker (SKIP LOCKED)"] -.->|Polls Outbox| PG
        OutboxWorker -->|Dispatches Events| RabbitMQ[("RabbitMQ Exchange")]
        RabbitMQ --> EmailConsumer["Consumer: Email Reminders (Mailpit)"]
        RabbitMQ --> WebhookConsumer["Consumer: Background Tasks"]
    end

    subgraph Object Storage & External Services
        Controller -->|Presigned URLs| MinIO[("MinIO (S3 Compatible Storage)")]
        Controller <-->|Billing & Portal| Stripe[("Stripe API")]
    end

    subgraph Observability
        Controller -->|Metrics| Prometheus[("Prometheus (:9090)")]
        Prometheus --> Grafana[("Grafana Dashboards (:3000)")]
        Controller -->|Traces| Sentry[("Sentry Error Tracking")]
    end
```

---

## 2. Master Package Catalog & Roles

Every package in this solution was chosen to satisfy a specific pillar of distributed systems design:

| NuGet Package | Category | Why We Use It (Its Exact Role) |
| :--- | :--- | :--- |
| **`Npgsql.EntityFrameworkCore.PostgreSQL`** | Database | High-performance PostgreSQL provider for EF Core with connection pooling and automated retry on failure (`EnableRetryOnFailure`). |
| **`Microsoft.EntityFrameworkCore.Sqlite`** | Database | Local fallback provider for offline developer mode when Docker/PostgreSQL is unavailable. |
| **`MassTransit`** | Event Bus | Enterprise abstraction over RabbitMQ. Handles retry policies, dead-letter exchanges, and consumer lifecycle. |
| **`MassTransit.RabbitMQ`** | Transport | RabbitMQ broker transport layer for MassTransit. |
| **`MassTransit.EntityFrameworkCore`** | Resilience | **Transactional Outbox Implementation**: Hooks into `DbContext.SaveChangesAsync()` to write outgoing events into the same PostgreSQL transaction, using `FOR UPDATE SKIP LOCKED` for worker dispatching. |
| **`Microsoft.Extensions.Caching.StackExchangeRedis`** | Caching | Connects ASP.NET Core to Redis for high-throughput distributed caching. |
| **`Microsoft.AspNetCore.Authentication.JwtBearer`** | Security | Validates OIDC JWT bearer tokens with asymmetric RSA/ECDSA keys issued by ZITADEL or HMAC SHA256 in local test mode. |
| **`Microsoft.AspNetCore.RateLimiting`** | Reliability | Built-in token-bucket and sliding-window rate limiting partitioned per tenant/IP to stop "noisy neighbor" starvation. |
| **`AspNetCore.HealthChecks.NpgSql`** | Reliability | Deep health check that queries PostgreSQL connectivity and reports status to Kubernetes `/healthz/ready`. |
| **`AspNetCore.HealthChecks.Redis`** | Reliability | Deep health check that pings Redis latency. |
| **`Stripe.net`** | Billing | Official Stripe SDK for Checkout sessions, Customer Billing Portal, and cryptographic webhook verification. |
| **`AWSSDK.S3`** | Storage | S3-compatible SDK used against MinIO to generate presigned upload and download URLs. |
| **`prometheus-net.AspNetCore`** | Metrics | Exports runtime and HTTP request metrics at `/metrics` for Prometheus scraping. |
| **`Serilog.AspNetCore` & `Serilog.Sinks.Console`** | Logging | High-speed structured JSON logging with correlation IDs and tenant enrichers. |
| **`Sentry.AspNetCore`** | Monitoring | Production real-time exception tracking and distributed tracing. |
| **`Scalar.AspNetCore`** | Documentation | Modern, interactive API documentation UI (replacing legacy Swagger UI) at `/scalar/v1`. |

---

## 3. How the System Works Under the Hood

### The Lifecycle of a Write Request (`POST /api/todos`)

1. **Edge & Rate Limiting**: The client request passes through Caddy reverse proxy and enters ASP.NET Core. The `RateLimiter` checks the tenant bucket. If the tenant made >120 requests this minute, it responds immediately with `HTTP 429 Too Many Requests`.
2. **Authentication & Tenant Context**: The `JwtBearer` middleware validates the bearer token signature. The custom `TenantMiddleware` extracts the `tenant_id` claim, resolving it into a scoped `ITenantContext`.
3. **Plan Quota Verification**: The endpoint inspects the tenant's current plan. If on `Free` plan and the total active tasks $\ge 25$, it rejects the request with `HTTP 402 Payment Required` and an upgrade link.
4. **EF Core Global Query Filter**: The entity is saved via `AppDbContext`. The global query filter (`builder.Entity<T>().HasQueryFilter(e => e.TenantId == tenantContext.TenantId)`) guarantees tenant data isolation.
5. **Atomic Transactional Outbox**: Instead of directly dispatching a network message to RabbitMQ (which could fail or cause phantom reads if the database commit fails), `publishEndpoint.Publish(new TodoCreatedEvent(...))` saves the event into the `outbox_messages` table inside the **exact same database transaction** as the todo record.
6. **Cache Invalidation via Tenant Versioning**: Rather than scanning or clearing all keys in Redis, the system increments a version key: `tenant:{tenantId}:todos:version`. All subsequent reads immediately point to the new version tag without any cache stampede.
7. **Background Worker Dispatch**: The MassTransit outbox daemon continuously locks pending rows in PostgreSQL (`FOR UPDATE SKIP LOCKED`) and publishes them reliably to RabbitMQ exchanges.
8. **Consumer Processing**: The `TodoReminderConsumer` pulls the message from RabbitMQ and sends the email via Mailpit (SMTP).

---

## 4. Step-by-Step Tutorial for Beginners (Build Any SaaS)

Follow these phases to reproduce this architecture for your own project.

---

### Phase 1: Multi-Tenant Database Layer (EF Core)

#### Step 1.1: Define Tenant Marker Interface
Every entity that belongs to a specific customer must implement `IMustHaveTenant`:
```csharp
public interface IMustHaveTenant
{
    Guid TenantId { get; set; }
}

public class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Plan { get; set; } = "Free"; // Free, Pro, Enterprise
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
```

#### Step 1.2: Tenant Context & Scoped Resolution
```csharp
public interface ITenantContext
{
    Guid TenantId { get; }
    void SetTenantId(Guid tenantId);
}

public class TenantContext : ITenantContext
{
    public Guid TenantId { get; private set; }
    public void SetTenantId(Guid tenantId) => TenantId = tenantId;
}
```

#### Step 1.3: Enforce Query Filters in DbContext
In your `DbContext`, automatically attach query filters to every `IMustHaveTenant` model and automatically populate `TenantId` on save:
```csharp
public class AppDbContext : DbContext
{
    private readonly ITenantContext _tenantContext;

    public AppDbContext(DbContextOptions<AppDbContext> options, ITenantContext tenantContext)
        : base(options) => _tenantContext = tenantContext;

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<MyEntity> MyEntities => Set<MyEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Global multi-tenant query filter
        modelBuilder.Entity<MyEntity>()
            .HasQueryFilter(e => e.TenantId == _tenantContext.TenantId);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var entry in ChangeTracker.Entries<IMustHaveTenant>())
        {
            if (entry.State == EntityState.Added && entry.Entity.TenantId == Guid.Empty)
            {
                entry.Entity.TenantId = _tenantContext.TenantId;
            }
        }
        return base.SaveChangesAsync(cancellationToken);
    }
}
```

---

### Phase 2: Authentication & Identity (ZITADEL & JWT)

#### Step 2.1: Run ZITADEL in Docker Compose
Use pinned version `v2.68.2` which bundles the management console and login UI in one container:
```yaml
  zitadel:
    image: ghcr.io/zitadel/zitadel:v2.68.2
    command: 'start-from-init --masterkey "MasterKeyNeedsToHave32Characters" --tlsMode disabled'
    environment:
      - ZITADEL_DATABASE_POSTGRES_HOST=postgres
      - ZITADEL_DATABASE_POSTGRES_PORT=5432
      - ZITADEL_DATABASE_POSTGRES_DATABASE=zitadel
      - ZITADEL_DATABASE_POSTGRES_USER_USERNAME=postgres
      - ZITADEL_DATABASE_POSTGRES_USER_PASSWORD=localpassword
      - ZITADEL_EXTERNALSECURE=false
      - ZITADEL_EXTERNALPORT=8085
      - ZITADEL_EXTERNALDOMAIN=localhost
      - ZITADEL_FIRSTINSTANCE_ORG_HUMAN_USERNAME=admin
      - ZITADEL_FIRSTINSTANCE_ORG_HUMAN_PASSWORD=Password123!
    ports:
      - "8085:8080"
```

#### Step 2.2: Configure JWT Validation in Program.cs
Support both ZITADEL OIDC and lightweight local token issuance:
```csharp
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = "TodoSaaS",
            ValidateAudience = true,
            ValidAudience = "TodoSaaS.Clients",
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });
builder.Services.AddAuthorization();
```

---

### Phase 3: Transactional Outbox & Async Messaging (MassTransit + RabbitMQ)

Never write to a database and directly publish to a message broker in separate calls. If the broker call fails, you have an inconsistent state.

#### Step 3.1: Register MassTransit with EF Core Outbox
```csharp
builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<MyDomainEventConsumer>();

    x.AddEntityFrameworkOutbox<AppDbContext>(o =>
    {
        o.UsePostgres();
        o.UseBusOutbox(); // Automatically catches Publish() calls
    });

    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(rabbitHost, "/", h =>
        {
            h.Username(rabbitUser);
            h.Password(rabbitPass);
        });
        cfg.ConfigureEndpoints(context);
    });
});
```

#### Step 3.2: Publishing an Event Atomically
```csharp
app.MapPost("/api/items", async (ItemCreateDto dto, AppDbContext db, IPublishEndpoint bus) =>
{
    var item = new MyEntity { Title = dto.Title };
    db.MyEntities.Add(item);

    // Staged into outbox_messages table inside DB transaction
    await bus.Publish(new ItemCreatedEvent(item.Id, item.Title));
    
    // Both entity and message commit together atomically!
    await db.SaveChangesAsync();
    return Results.Created($"/api/items/{item.Id}", item);
});
```

---

### Phase 4: Distributed Cache with Tenant Versioning (Redis)

#### The Problem with Wildcard Key Purging
Calling `KEYS tenant:123:*` in Redis blocks the single-threaded Redis engine and degrades performance when you have millions of keys.

#### The Solution: Version Tags
Store a version number for the tenant: `tenant:{tenantId}:version`. Include the version in every cache key:
```csharp
// 1. Fetch current version tag
var version = await cache.GetVersionAsync($"tenant:{tenantId}:items:version");
var cacheKey = $"tenant:{tenantId}:items:v{version}:search_{search}";

// 2. Try read from Redis
var cached = await cache.GetAsync<List<MyEntity>>(cacheKey);
if (cached != null) return Results.Ok(cached);

// 3. Fallback to DB
var items = await db.MyEntities.ToListAsync();
await cache.SetAsync(cacheKey, items, TimeSpan.FromMinutes(10));
return Results.Ok(items);
```

**When a write occurs**:
```csharp
// Increment the version tag (e.g. from 1 -> 2)
await cache.InvalidateVersionAsync($"tenant:{tenantId}:items:version");
// All existing v1 keys are now instantly and gracefully abandoned!
```

---

### Phase 5: Billing, Stripe Webhooks & Idempotency

#### Step 5.1: Idempotency Table
Stripe will retry webhooks repeatedly if your server doesn't respond in time. To prevent duplicate charges or double upgrades:
```csharp
public class ProcessedWebhookEvent
{
    public string Id { get; set; } = string.Empty; // e.g. evt_1N4m...
    public string EventType { get; set; } = string.Empty;
    public DateTime ProcessedAtUtc { get; set; } = DateTime.UtcNow;
}
```

#### Step 5.2: Handling the Webhook
```csharp
// Check if event was already handled
var alreadyProcessed = await db.ProcessedWebhookEvents.AnyAsync(e => e.Id == eventId);
if (alreadyProcessed)
{
    _logger.LogInformation("Duplicate webhook {EventId} ignored.", eventId);
    return;
}

// Process business logic (e.g. upgrade plan)
tenant.Plan = "Pro";

// Mark as processed in same transaction
db.ProcessedWebhookEvents.Add(new ProcessedWebhookEvent { Id = eventId, EventType = eventType });
await db.SaveChangesAsync();
```

---

### Phase 6: Plan Quota Enforcement & Rate Limiting

#### Step 6.1: Hard Quota Check
```csharp
var tenant = await db.Tenants.FindAsync(tenantContext.TenantId);
if (tenant.Plan == "Free")
{
    var count = await db.MyEntities.CountAsync();
    if (count >= 25)
    {
        return Results.Json(new { 
            error = "QuotaExceeded", 
            message = "Free tier limit of 25 items reached. Upgrade to Pro." 
        }, statusCode: StatusCodes.Status402PaymentRequired);
    }
}
```

#### Step 6.2: Tenant Rate Limiter
```csharp
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
    {
        var tenantId = httpContext.User.FindFirst("tenant_id")?.Value 
                       ?? httpContext.Connection.RemoteIpAddress?.ToString() 
                       ?? "anonymous";

        return RateLimitPartition.GetFixedWindowLimiter(tenantId, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 120,
            Window = TimeSpan.FromMinutes(1)
        });
    });
});
```

---

### Phase 7: Deep Health Checks & Observability

Configure active health checks for orchestrators (Kubernetes / ECS / Docker Swarm):
```csharp
var hc = builder.Services.AddHealthChecks();
hc.AddNpgSql(pgConnectionString, name: "postgres", tags: new[] { "ready", "db" });
hc.AddRedis(redisConnectionString, name: "redis", tags: new[] { "ready", "cache" });

// Liveness: Is the process responsive?
app.MapHealthChecks("/healthz/live", new HealthCheckOptions { Predicate = _ => false });

// Readiness: Are all dependencies (Postgres, Redis, RabbitMQ) operational?
app.MapHealthChecks("/healthz/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});
```

---

### Phase 8: Frontend UI & Self-Serve Onboarding

Provide a single-click self-serve onboarding endpoint (`POST /api/tenants/onboard`):
```csharp
app.MapPost("/api/tenants/onboard", async (TenantOnboardDto dto, AppDbContext db, ITokenService tokenService) =>
{
    var tenant = new Tenant { Name = dto.OrganizationName, Plan = "Free" };
    db.Tenants.Add(tenant);

    // Seed starter content
    db.MyEntities.Add(new MyEntity { TenantId = tenant.Id, Title = "Welcome to your new workspace!" });
    await db.SaveChangesAsync();

    var token = tokenService.GenerateToken(tenant.Id, dto.AdminEmail, "Admin");
    return Results.Created($"/api/tenants/{tenant.Id}", new { tenant, token });
});
```

---

## 5. Adapting This Architecture to Other Domains

To build a SaaS in any other domain, keep the entire infrastructure and replace the `TodoItem` entity:

| Domain | Replace `TodoItem` With: | Async Event To Publish: | Plan Quota Check: |
| :--- | :--- | :--- | :--- |
| **CRM SaaS** | `Contact`, `Lead`, `Deal` | `LeadAssignedEvent` | Free: max 50 contacts; Pro: unlimited. |
| **Invoicing SaaS** | `Invoice`, `Client`, `Payment` | `InvoiceSentEvent` | Free: max 5 invoices/month; Pro: unlimited. |
| **E-Commerce SaaS** | `Product`, `Order`, `Inventory` | `OrderPlacedEvent` | Free: 2% transaction fee; Pro: 0% fee. |
| **Project Mgmt** | `Project`, `Milestone`, `Ticket` | `MilestoneCompletedEvent` | Free: 3 projects; Pro: unlimited projects. |
| **Healthcare SaaS**| `Patient`, `Appointment`, `Rx` | `AppointmentBookedEvent` | Free: 1 doctor; Pro: up to 10 doctors. |

---

## 6. Operational Runbook & Quick Commands

### Start All 10 Services
```bash
docker compose up -d
```

### Run Automated Test Suite
```bash
dotnet test
```

### Access Infrastructure Consoles

| Service | Local URL | Credentials / Notes |
| :--- | :--- | :--- |
| **Interactive App Dashboard** | [http://localhost:8080](http://localhost:8080) | Live UI with Quota & Upgrades |
| **Interactive API Docs (Scalar)**| [http://localhost:8080/scalar/v1](http://localhost:8080/scalar/v1) | Live OpenAPI test client |
| **ZITADEL Auth Console** | [http://localhost:8085/ui/console](http://localhost:8085/ui/console) | `admin` / `Password123!` |
| **Deep Readiness Health Check** | [http://localhost:8080/healthz/ready](http://localhost:8080/healthz/ready) | PostgreSQL, Redis, RabbitMQ |
| **RabbitMQ Management** | [http://localhost:15672](http://localhost:15672) | `guest` / `guest` |
| **Mailpit (Local SMTP)** | [http://localhost:8025](http://localhost:8025) | Local inbox for email events |
| **Grafana Monitoring** | [http://localhost:3000](http://localhost:3000) | `admin` / `admin` |
| **MinIO S3 Console** | [http://localhost:9001](http://localhost:9001) | `minioadmin` / `minioadmin` |
| **Jaeger Distributed Tracing** | [http://localhost:16686](http://localhost:16686) | Visual flame graphs & OTLP spans |

---

## 7. The 7 Modern Software Engineering Standards Implemented

This project implements all 7 advanced software engineering disciplines expected in high-performing distributed engineering teams:

### 1. Distributed Tracing (W3C OpenTelemetry Standard)
- End-to-end distributed tracing across HTTP requests, EF Core SQL queries, MassTransit message bus, and external APIs.
- Integrated with Jaeger UI (`http://localhost:16686`) via standard OTLP protocol.

### 2. Immutable Audit Trails (SOC 2 Type II / HIPAA Ready)
- Dedicated append-only `AuditLogs` table and `IAuditService`.
- Captures `ActorId`, `ActorEmail`, `Action`, `EntityName`, `EntityId`, JSON changes diff, IP address, and UTC timestamp.
- Protected by multi-tenant global query filters and exposed via `GET /api/audit-logs`.

### 3. Feature Flags & Progressive Delivery (OpenFeature)
- Standardized Microsoft Feature Management abstraction (`Microsoft.FeatureManagement.AspNetCore`).
- Decouples deployment from feature release with dark launches and kill-switches (`GET /api/features`).

### 4. Architecture Fitness Tests (NetArchTest)
- Automated unit tests asserting architectural boundaries:
  - Domain models cannot depend on Data persistence layer.
  - Multi-tenant entities must implement `IMustHaveTenant`.
  - Consumers must reside in `Consumers` namespace.
  - Enforced in CI/CD pipeline on every commit.

### 5. GitOps & Progressive Canary Deployments
- `gitops/argocd-app.yaml`: Automated declarative Kubernetes deployment synchronizing directly from GitHub.
- `gitops/rollout.yaml`: Argo Rollouts Canary manifest routing 5% -> 20% -> 50% -> 100% traffic with automated Prometheus metric analysis for rollbacks.

### 6. Zero-Trust Secret Management
- `k8s/external-secret.yaml`: Kubernetes ExternalSecrets connecting to HashiCorp Vault / AWS Secrets Manager with 30-day automated credential rotation.

### 7. One-Click Dev Environment (Dev Containers & Codespaces)
- `.devcontainer/devcontainer.json`: 1-click cloud developer environment for VS Code and GitHub Codespaces with .NET 9, Docker-in-Docker, and pre-configured extensions.

---

## 8. Multi-Environment Lifecycle (Dev, Staging, Production)

A reliable enterprise SaaS must maintain strict isolation between environments while preserving configuration parity. This architecture utilizes a 3-tier lifecycle:

```
[ Developer Machine / DevContainer ]
        |
        v  (git push)
[ GitHub Actions CI Pipeline ]
   ├─ Build Release
   ├─ NetArchTest (12 Rules)
   ├─ Vulnerability Audit
   └─ Publish Docker Image to GHCR
        |
        +-----------------------+
        |                       |
        v                       v
[ Staging Environment ]   [ Production Environment ]
 - Namespace: todosaas-staging    - Namespace: todosaas-production
 - 2 Replicas                    - 5 Replicas + HPA (2 to 10)
 - Sandbox PostgreSQL & Redis     - Aurora Multi-AZ & Redis HA
 - Mailpit Mock SMTP             - Resend Production SMTP
 - Mock Billing Sandbox           - Live Stripe Webhooks
 - Fast Feedback Verification    - Argo Rollouts Canary (5% -> 100%)
```

### 1. Configuration Parity via ASPNETCORE_ENVIRONMENT
The solution defines three distinct environment configuration files in `TodoSaaS.Api`:
- `appsettings.json` / `appsettings.Development.json`: Local development defaults pointing to localhost and Docker network names.
- `appsettings.Staging.json`: Configured with staging database endpoints, synthetic test credentials, and permissive logging.
- `appsettings.Production.json`: Configured with minimal logging, strict security headers, real Stripe live keys injected at runtime, and production S3 storage endpoints.

### 2. Kustomize Multi-Environment Overlays
Kubernetes deployments are structured with Kustomize overlays in `k8s/overlays/`:

#### Staging Overlay (`k8s/overlays/staging/kustomization.yaml`)
```yaml
apiVersion: kustomize.config.k8s.io/v1beta1
kind: Kustomization
namespace: todosaas-staging
resources:
  - ../../deployment.yaml
  - ../../external-secret.yaml
namePrefix: staging-
patches:
  - target:
      kind: Deployment
      name: todosaas-api
    patch: |-
      - op: replace
        path: /spec/replicas
        value: 2
      - op: add
        path: /spec/template/spec/containers/0/env
        value:
          - name: ASPNETCORE_ENVIRONMENT
            value: "Staging"
```

#### Production Overlay (`k8s/overlays/production/kustomization.yaml`)
```yaml
apiVersion: kustomize.config.k8s.io/v1beta1
kind: Kustomization
namespace: todosaas-production
resources:
  - ../../deployment.yaml
  - ../../external-secret.yaml
namePrefix: prod-
patches:
  - target:
      kind: Deployment
      name: todosaas-api
    patch: |-
      - op: replace
        path: /spec/replicas
        value: 5
      - op: add
        path: /spec/template/spec/containers/0/env
        value:
          - name: ASPNETCORE_ENVIRONMENT
            value: "Production"
```

### 3. Deploying Environments via CLI

#### To Staging:
```bash
kubectl apply -k k8s/overlays/staging
```

#### To Production:
```bash
kubectl apply -k k8s/overlays/production
```

### 4. Continuous GitOps Reconciliation with ArgoCD
The root `gitops/argocd-app.yaml` continuously synchronizes state with GitHub:
```bash
kubectl apply -f gitops/argocd-app.yaml
```

ArgoCD detects commits to `main`, pulls the new container image from GitHub Container Registry (`ghcr.io/hasanzuhair/todosaas-api:latest`), and triggers the progressive canary rollout defined in `gitops/rollout.yaml`.


