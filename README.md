# TodoSaaS: Production-Grade .NET 9 SaaS Reference Architecture

A complete, standard, secure, and reliable Software-as-a-Service (SaaS) backend built with **.NET 9 (ASP.NET Core Minimal APIs)**, demonstrating all 16 production systems configured and working together.

---

## 1. The 16 Production Systems

| # | System | Tool Used | Configuration & Role |
| :-: | :--- | :--- | :--- |
| **1** | **DNS, CDN & Edge WAF** | **Cloudflare** | SSL termination, DDoS defense, edge caching, and origin IP masking. |
| **2** | **Reverse Proxy** | **Caddy** | Configured in `Caddyfile`. Handles gzip/zstd, security headers, and path proxying. |
| **3** | **API Gateway** | **ASP.NET Core Middleware** | Token bucket rate limiting per IP/Tenant, CORS, and request protection. |
| **4** | **Containerization** | **Docker** (`aspnet:9.0-noble-chiseled`) | Multi-stage build in `TodoSaaS.Api/Dockerfile`. Non-root unprivileged container. |
| **5** | **Compute & Orchestration** | **Docker Compose** & **Kubernetes** | Local `docker-compose.yml` + Production `k8s/deployment.yaml` with HPA autoscaling. |
| **6** | **Primary Database** | **PostgreSQL 16** + **EF Core** | Schema in `AppDbContext.cs` with **Global Query Filters** for multi-tenant isolation. |
| **7** | **Cache & In-Memory Store** | **Redis 7** | Configured via `StackExchangeRedis` for distributed locks, session cache, and fast reads. |
| **8** | **Message Queue & Workers** | **MassTransit** + **RabbitMQ** | Asynchronous workers in `TodoReminderConsumer.cs` for task reminders and alerts. |
| **9** | **Object Storage** | **MinIO** (Local) / **Cloudflare R2** (Prod) | S3-compatible file storage in `S3FileStorageService.cs` using short-lived Presigned URLs. |
| **10** | **CI / CD Pipeline** | **GitHub Actions** | Automated pipeline in `.github/workflows/ci-cd.yml` (build, test, Trivy scan, push image). |
| **11** | **Secrets Management** | **Infisical** / **.NET User-Secrets** | Configuration via `appsettings.json` and environment variable overrides. |
| **12** | **Auth & Identity** | **ZITADEL** / **Clerk** / **ASP.NET Core JWT** | Self-hosted ZITADEL (:8085) with OIDC, or Clerk Cloud. Multi-tenant claim extraction (`org_id`, `urn:zitadel:iam:org:id`, `tenant_id`) in `TenantMiddleware.cs`. |
| **13** | **Observability & Metrics** | **Prometheus** + **Grafana** | Prometheus metrics scraped at `/metrics`; visualized on Grafana dashboard. |
| **14** | **Error Tracking** | **Sentry** (`Sentry.AspNetCore`) | Real-time crash diagnostics, line-level stack traces, and release tracking. |
| **15** | **Transactional Email** | **Mailpit** (Local) / **Resend** (Prod) | `EmailService.cs` sends via local Mailpit SMTP (port 1025) with zero internet needed. |
| **16** | **Billing & Subscriptions** | **Stripe** (`Stripe.net`) | `StripeBillingService.cs` handles checkout sessions, customer portal, and webhooks. |

---

## 2. Quick Start (Local Run)

### Option A: Run Full Stack via Docker Compose (Recommended)
Make sure your user has permissions to run Docker, then run:
```bash
docker compose up -d
```

#### Services & Web Consoles:
* **Interactive Web Dashboard**: [http://localhost:80](http://localhost:80) or [http://localhost:8080](http://localhost:8080)
* **Scalar API Interactive Documentation**: [http://localhost:8080/scalar/v1](http://localhost:8080/scalar/v1)
* **OpenAPI Specification**: [http://localhost:8080/openapi/v1.json](http://localhost:8080/openapi/v1.json)
* **Reverse Proxy (Caddy)**: [http://localhost:80](http://localhost:80)
* **RabbitMQ Dashboard**: [http://localhost:15672](http://localhost:15672) (User: `guest` / `guest`)
* **Mailpit (Email Inbox)**: [http://localhost:8025](http://localhost:8025)
* **MinIO Console**: [http://localhost:9001](http://localhost:9001) (User: `minioadmin` / `minioadmin`)
* **ZITADEL Auth Console**: [http://localhost:8085/ui/console](http://localhost:8085/ui/console)
* **Prometheus Metrics**: [http://localhost:9090](http://localhost:9090)
* **Grafana Dashboards**: [http://localhost:3000](http://localhost:3000) (User: `admin` / `admin`)

---

### Option B: Run API Locally with .NET CLI
```bash
cd TodoSaaS.Api
~/.dotnet/dotnet run
```

---

## 3. Testing Core API Endpoints

### 1. Health Check
```bash
curl http://localhost:8080/healthz
```

### 2. Create a Todo Item (Auto-isolated by Tenant)
```bash
curl -X POST http://localhost:8080/api/todos \
  -H "Content-Type: application/json" \
  -H "X-Tenant-ID: 11111111-1111-1111-1111-111111111111" \
  -d '{"title": "Launch SaaS to Production", "description": "Configure DNS and SSL"}'
```

### 3. Get All Todos for Current Tenant
```bash
curl -H "X-Tenant-ID: 11111111-1111-1111-1111-111111111111" http://localhost:8080/api/todos
```

### 4. Complete a Todo (Triggers Async MassTransit Event)
```bash
curl -X POST http://localhost:8080/api/todos/<TODO_ID>/complete \
  -H "X-Tenant-ID: 11111111-1111-1111-1111-111111111111"
```

### 5. Send an Asynchronous Task Due Reminder (Triggers Email via Queue)
```bash
curl -X POST "http://localhost:8080/api/todos/<TODO_ID>/reminder?email=user@example.com" \
  -H "X-Tenant-ID: 11111111-1111-1111-1111-111111111111"
```
*Open Mailpit at `http://localhost:8025` to see the delivered email instantly.*

### 6. Get Presigned S3/MinIO File Upload URL
```bash
curl "http://localhost:8080/api/storage/presigned-url?fileName=spec.pdf&contentType=application/pdf"
```

### 7. Scrape Prometheus Metrics
```bash
curl http://localhost:8080/metrics
```
