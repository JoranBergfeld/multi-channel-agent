# Azure Hosting, IaC and Observability — Research Notes

**Issue:** #8  
**Date:** 2026-08-28  
**Scope:** Hosting the multi-channel inventory-agent backend on Azure; IaC tooling; observability for agent turns and LLM calls.

---

## 1. Context and Requirements

The backend must simultaneously:

| Requirement | Notes |
|---|---|
| Expose public HTTP webhooks | Microsoft Graph change notifications (email), Bot Framework messaging endpoint |
| Serve a web application | Chat UI, admin surfaces |
| Hold long-lived WebSocket / realtime-audio connections | Voice channel (Azure OpenAI Realtime API via WebSocket) |
| Minimal operational overhead | Greenfield project, small team |

All technology must be Microsoft/Azure.

---

## 2. Hosting Options Compared

### 2.1 Azure Container Apps (ACA)

**What it is:** Serverless container hosting built on top of Kubernetes/KEDA; you deploy container images without managing the cluster. GA since 2022; actively developed.  
Source: [ACA overview – learn.microsoft.com/en-us/azure/container-apps/overview](https://learn.microsoft.com/en-us/azure/container-apps/overview) (page updated 2026-07-07).

#### WebSocket / Long-lived connections

ACA's HTTP ingress **natively supports WebSocket and gRPC** alongside HTTP/1.1 and HTTP/2. TLS 1.2/1.3 is terminated at the ingress point; ws:// upgrades traverse the proxy transparently. There is no documented per-connection time limit; connections stay open as long as the container and the replica live.

> "With HTTP ingress enabled, your container app has: Support for WebSocket and gRPC …"  
> — [ACA Ingress overview – learn.microsoft.com/en-us/azure/container-apps/ingress-overview](https://learn.microsoft.com/en-us/azure/container-apps/ingress-overview) (updated 2026-08-05)

TCP ingress is also available for non-HTTP protocols, but a container app can only expose one ingress type at a time. If both HTTP webhooks and raw TCP are needed from the same container, proxy all traffic through a single HTTP port (e.g., via an in-container NGINX sidecar) or split them across separate Container Apps within the same environment.

#### Cold-start behaviour

When minimum replicas = 0 the first inbound request after an idle period triggers a cold start (image pull + container init). On the Consumption workload profile this is typically **2–10 seconds** depending on image size and language runtime. Mitigation: set `minReplicas: 1` (pays idle rate ~30–40 % of active rate) or use the **Dedicated workload profile** which keeps a pre-warmed node.

For the voice channel (real-time audio), accepting a cold-start on the first connection attempt is likely unacceptable; running at min 1 replica (or using a Dedicated profile) is recommended for that component.

#### Scale-to-zero and cost at low scale

ACA uses the **Consumption plan** by default:

| Metric | Free grant (per subscription/month) | Paid rate |
|---|---|---|
| vCPU-seconds | 180,000 | ~$0.000024/vCPU-s |
| GiB-seconds | 360,000 | ~$0.000003/GiB-s |
| HTTP requests | 2 million | ~$0.40/million |

When `minReplicas: 0` and no traffic arrives, **no compute charge is incurred**. For a low-traffic development environment the free grant typically covers everything.  
Source: [ACA pricing – azure.microsoft.com/en-us/pricing/details/container-apps/](https://azure.microsoft.com/en-us/pricing/details/container-apps/)

#### Managed Identity

Both **system-assigned** and **user-assigned** Managed Identities are fully supported. They integrate with Microsoft Entra ID and can be used to authenticate to Key Vault, Azure OpenAI, Service Bus, Graph API, etc. without storing credentials in the container. Adding/removing an identity does not require a redeploy.  
Source: [Managed identities in ACA – learn.microsoft.com/en-us/azure/container-apps/managed-identity](https://learn.microsoft.com/en-us/azure/container-apps/managed-identity) (updated 2026-02-13)

#### Custom domains and TLS

Custom domains are supported via:
1. **Managed (free) certificate** – auto-issued and renewed by DigiCert; requires public ingress and DNS pointing directly to the environment IP.
2. **Bring-your-own certificate** – upload a .pfx / .pem; can import from Azure Key Vault.

ECDSA P-384/P-521 certificates are **not supported**; use RSA.  
Source: [Custom domain names and certificates – learn.microsoft.com/en-us/azure/container-apps/custom-domains-certificates](https://learn.microsoft.com/en-us/azure/container-apps/custom-domains-certificates)

#### Deployment story

- **`azd` template support:** ACA is the default compute target for most modern `azd` templates including AI/agent samples.
- **Bicep:** First-class resource types (`Microsoft.App/containerApps`); new features land in Bicep on day zero.
- **GitHub Actions / Azure Pipelines:** `az containerapp update --image` or dedicated ACA deploy action.
- **Zero-downtime revisions:** Traffic splitting across revisions supports blue/green and canary deployments natively.

---

### 2.2 Azure App Service

**What it is:** PaaS web hosting (Windows or Linux VMs) for code-first workloads (.NET, Node.js, Python, Java, PHP) or custom containers. Mature, GA since 2014.  
Source: [App Service overview – learn.microsoft.com/en-us/azure/app-service/overview](https://learn.microsoft.com/en-us/azure/app-service/overview) (updated 2026-08-18)

#### WebSocket / Long-lived connections

WebSockets are **fully supported on Basic tier and above** (not Free/Shared). Enable via the Azure Portal → Configuration → General Settings → Web Sockets = On, or via Bicep `webSocketsEnabled: true`.  
Source: [Enable WebSockets – learn.microsoft.com/en-us/azure/app-service/web-sites-enable-websockets](https://learn.microsoft.com/en-us/azure/app-service/web-sites-enable-websockets)

Long-lived connections remain open as long as the app pool is alive. There is a configurable idle-connection timeout (default 4 minutes for the load balancer in front) which must be raised or kept alive with application-level pings for real-time audio scenarios.

#### Cold-start behaviour

App Service does **not** support true scale-to-zero on any Standard, Basic, Premium, or Isolated tier — at least one VM instance is always running and always billed. The Free (F1) tier can suspend apps after inactivity, producing cold starts of 2–10 s, but F1 is rate-limited (60 CPU min/day) and unsuitable for production.

#### Scale-to-zero and cost at low scale

| Tier | Scale to zero | Min monthly cost (West Europe) |
|---|---|---|
| Free (F1) | Effectively yes (shared, rate-limited) | $0 |
| Basic B1 | **No** | ~$13–15/month |
| Standard S1 | No | ~$50–60/month |
| Premium P1v3 | No | ~$130–150/month |

App Service is cost-effective only when the host is already busy. For a lightly-loaded agent backend that sleeps between conversations it is **more expensive than ACA at low scale**.

#### Managed Identity

System-assigned and user-assigned Managed Identities are fully supported across all paid tiers.  
Source: [App Service managed identities – learn.microsoft.com/en-us/azure/app-service/overview-managed-identity](https://learn.microsoft.com/en-us/azure/app-service/overview-managed-identity)

#### Custom domains and TLS

Free managed certificates, bring-your-own, and App Service Managed Certificate (ASMC) are all available on Basic+.  
**Note (as of 2025-07-28):** Microsoft made changes to ASMC issuance/renewal processes; review official guidance if relying on ASMC.  
Source: [Secure app with custom domain – learn.microsoft.com/en-us/azure/app-service/tutorial-secure-domain-certificate](https://learn.microsoft.com/en-us/azure/app-service/tutorial-secure-domain-certificate)

#### Deployment story

- **`azd`:** Supported; several templates target App Service (especially .NET web apps).
- **Bicep:** `Microsoft.Web/sites` resource type; mature, day-zero support.
- **VS Code / Visual Studio / GitHub Actions:** Deep first-party integration; one-click publish.
- **Deployment slots:** Blue/green via staging slot + swap; zero-downtime deployments.

---

### 2.3 Azure Functions

**What it is:** Event-driven, serverless compute. Triggered by HTTP, queues, timers, Graph change notifications, etc. GA since 2016; **Flex Consumption plan** GA since December 2024, now recommended for new serverless apps.  
Source: [Functions Scale and Hosting – learn.microsoft.com/en-us/azure/azure-functions/functions-scale](https://learn.microsoft.com/en-us/azure/azure-functions/functions-scale) (updated 2026-08-12)

#### WebSocket / Long-lived connections

Azure Functions **does not natively support WebSocket connections** on any scale-to-zero plan. Functions are fundamentally request/response; a long-lived WebSocket upgrade cannot be held open across the serverless execution model.

For real-time messaging with Functions, Microsoft recommends pairing with **Azure Web PubSub** (managed WebSocket hub) or **Azure SignalR Service**, with Functions handling the event logic.  
Source: [Functions Scale and Hosting – functions-scale](https://learn.microsoft.com/en-us/azure/azure-functions/functions-scale)

#### Cold-start behaviour

| Plan | Scale-to-zero | Typical cold start | Mitigation |
|---|---|---|---|
| Flex Consumption (GA) | Yes | ~0.3–1.8 s (language-dependent) | "Always Ready" pre-warmed instances (extra cost) |
| Premium | No | Negligible (pre-warmed) | N/A |
| Dedicated (App Service) | No | Negligible | N/A |
| Legacy Consumption (Windows) | Yes | 1–5 s | Legacy; migrate to Flex |

**Note:** The legacy Linux Consumption plan is retiring 2028-09-30; the Linux Consumption option is no longer getting new features.

#### Scale-to-zero and cost at low scale

Flex Consumption: pay per execution (vCPU-second + GiB-second + invocation count). First 100,000 requests and 400,000 GB-s per month are free (subscription-wide). True zero cost at zero load.  
Source: [Functions pricing – azure.microsoft.com/en-us/pricing/details/functions/](https://azure.microsoft.com/en-us/pricing/details/functions/)

#### Managed Identity

Fully supported on all plans.  
Source: [Functions identity – learn.microsoft.com/en-us/azure/azure-functions/functions-identity-access-azure-sql-with-managed-identity](https://learn.microsoft.com/en-us/azure/azure-functions/functions-identity-access-azure-sql-with-managed-identity)

#### Custom domains and TLS

Custom domains and managed certificates are supported on Premium and Dedicated plans. On Flex Consumption, custom domains require an **Azure Front Door or API Management** layer in front.

#### Deployment story

- **`azd`:** Excellent; most Azure-Samples AI agent templates use Functions for the backend logic layer, with ACA or App Service for the long-lived connection layer.
- **Bicep:** `Microsoft.Web/sites` with `kind: functionapp`; mature support.
- **CI/CD:** `func azure functionapp publish` or GitHub Actions with `azure/functions-action`.

---

### 2.4 Summary Comparison Table

| Criterion | Azure Container Apps | Azure App Service | Azure Functions |
|---|---|---|---|
| **WebSocket / long-lived** | ✅ Native HTTP ingress support | ✅ Basic+ (enable via config) | ❌ Not supported (use Web PubSub) |
| **Realtime audio (WSS)** | ✅ | ✅ (Premium P1v3+ recommended) | ❌ |
| **Cold start** | 2–10 s at min=0; ~0 at min=1 | None (always on) | 0.3–1.8 s (Flex); none (Premium) |
| **Scale-to-zero** | ✅ (Consumption) | ❌ | ✅ (Flex / Consumption) |
| **Cost at low scale** | Very low (free grant covers dev) | ~$13–15/mo min | Very low (free grant covers dev) |
| **Managed Identity** | ✅ System + User | ✅ System + User | ✅ System + User |
| **Custom domain + TLS** | ✅ Free cert or BYOC | ✅ Free cert or BYOC | Limited on Flex; need front-end layer |
| **Container deploy** | ✅ First class | ✅ (custom container) | ✅ Premium/Dedicated only |
| **azd support** | ✅ Default target for AI templates | ✅ | ✅ |
| **Revision / slot** | ✅ Traffic-split revisions | ✅ Deployment slots | ✅ Slots (Premium/Dedicated) |

---

## 3. IaC Tooling Compared

### 3.1 Bicep

Microsoft's purpose-built, Azure-native DSL that compiles to ARM templates.

**Strengths:**
- **Day-zero feature support** — new Azure resource types/properties appear in Bicep before they reach the Terraform AzureRM provider.
- **No state file** — Azure Resource Manager is the source of truth; no risk of state corruption or secret leakage in a state store.
- **IDE experience** — VS Code Bicep extension, type-safe completion, `what-if` previews.
- **First-party support** — Microsoft engineers maintain Bicep and use it internally.

**Weaknesses:**
- Azure-only. Cannot provision GitHub repositories, Cloudflare DNS, Datadog monitors, etc.
- Less mature module ecosystem than Terraform Registry.

Source: [Comparing Terraform and Bicep – learn.microsoft.com/en-us/azure/developer/terraform/get-started/comparing-terraform-and-bicep](https://learn.microsoft.com/en-us/azure/developer/terraform/get-started/comparing-terraform-and-bicep)

### 3.2 Terraform (AzureRM provider)

Cloud-agnostic HCL-based IaC maintained by HashiCorp (now IBM).

**Strengths:**
- Multi-cloud and third-party provider support (GitHub, Cloudflare, PagerDuty, …).
- Rich module registry; large community.
- Mature state management with remote backends (Azure Storage, Terraform Cloud).

**Weaknesses:**
- AzureRM provider **lags behind Bicep** for cutting-edge Azure features (typically days to weeks).
- State file management adds operational overhead (locking, encryption, secrets in state).
- HashiCorp's 2023 BSL license change; enterprises may need to evaluate licensing.

Source: [Comparing Terraform and Bicep – learn.microsoft.com](https://learn.microsoft.com/en-us/azure/developer/terraform/get-started/comparing-terraform-and-bicep); [Use Terraform with azd – learn.microsoft.com/en-us/azure/developer/azure-developer-cli/use-terraform-for-azd](https://learn.microsoft.com/en-us/azure/developer/azure-developer-cli/use-terraform-for-azd)

### 3.3 Azure Developer CLI (`azd`)

`azd` is **not** an IaC language; it is an **application lifecycle orchestrator** that wraps Bicep (default) or Terraform. Key commands: `azd init`, `azd up`, `azd deploy`, `azd provision`, `azd pipeline config`.  
Source: [azd overview – learn.microsoft.com/en-us/azure/developer/azure-developer-cli/overview](https://learn.microsoft.com/en-us/azure/developer/azure-developer-cli/overview) (updated 2026-01-09)

**What `azd` adds on top of Bicep/Terraform:**
- Convention-based project layout (`azure.yaml`, `infra/` directory).
- Single `azd up` packages the app, provisions infra, and deploys code.
- Built-in CI/CD pipeline generation for GitHub Actions and Azure Pipelines (`azd pipeline config`).
- Template gallery (`azure.github.io/awesome-azd`) including AI/agent reference architectures targeting ACA + Azure OpenAI + Cosmos DB + Application Insights.
- First-party integration with Azure AI Foundry agent samples.

**Recommended pairing:** `azd` + Bicep for an Azure-only greenfield project. Bicep handles the declarative resource definitions; `azd` handles the end-to-end developer workflow.

### 3.4 IaC Comparison Table

| Criterion | Bicep | Terraform | `azd` |
|---|---|---|---|
| Language | Bicep DSL → ARM | HCL | Wraps Bicep or Terraform |
| State management | None (ARM is truth) | State file required | Delegates to tool |
| Day-zero Azure support | ✅ | Usually days–weeks lag | ✅ (via Bicep) |
| Multi-cloud / 3rd party | ❌ | ✅ | ❌ (Azure only) |
| App + infra lifecycle | ❌ | ❌ | ✅ |
| CI/CD pipeline generation | Partial | Partial | ✅ Built-in |
| Template gallery | Some (Azure-Samples) | Terraform Registry | awesome-azd gallery |
| Microsoft strategic bet | ✅ | Community | ✅ |

---

## 4. Observability: Application Insights + Azure Monitor

### 4.1 Standard Story

**Azure Monitor Application Insights** is the recommended APM platform for Azure workloads. It supports OpenTelemetry (OTel) via the **Azure Monitor OpenTelemetry Distro** — Microsoft's supported distribution of the OTel SDK.

Languages supported with the Distro: .NET, Java, Node.js, Python.  
Source: [Enable OpenTelemetry in Application Insights – learn.microsoft.com/en-us/azure/azure-monitor/app/opentelemetry-enable](https://learn.microsoft.com/en-us/azure/azure-monitor/app/opentelemetry-enable) (updated 2026-08-19)

Key capabilities:
- **Distributed traces** — correlated spans across services, including dependency calls (HTTP, DB, Service Bus).
- **Application Map** — auto-discovered topology.
- **Live Metrics** — real-time telemetry stream; useful for load testing the voice channel.
- **Failures and Performance views** — p50/p95/p99 latency, exception aggregation.
- **Alerts and Workbooks** — KQL-based dashboards and alerting on any telemetry attribute.
- **Agents details view** — a unified monitoring surface for AI agents (Microsoft Foundry, Copilot Studio, third-party). Currently listed in the Application Insights portal.

Source: [Application Insights OTel overview – learn.microsoft.com/en-us/azure/azure-monitor/app/app-insights-overview](https://learn.microsoft.com/en-us/azure/azure-monitor/app/app-insights-overview) (updated 2026-05-28)

### 4.2 OpenTelemetry GenAI Semantic Conventions

The [OpenTelemetry GenAI Semantic Conventions](https://github.com/open-telemetry/semantic-conventions-genai) define standardised span, metric, and event names for LLM and agent workloads:

| Convention | Covers |
|---|---|
| `gen_ai.request.*` | Model name, temperature, max tokens, prompt |
| `gen_ai.response.*` | Completion, finish reason, token counts |
| `gen_ai.usage.*` | Input/output tokens (cost attribution) |
| `gen_ai.agent.*` | Agent invocations, tool calls, MCP interactions |

**Stability status (as of 2026-08):** The conventions are under active development; some attributes are marked **experimental**. Check the [semconv CHANGELOG](https://github.com/open-telemetry/semantic-conventions-genai/releases) before depending on specific attribute names in dashboards.

**Azure support:** The Azure Monitor OpenTelemetry Distro ingests any OTel span — including spans produced by SDK-level instrumentation of Azure OpenAI calls or Semantic Kernel. Microsoft Azure AI Foundry's agent framework emits traces structured around the GenAI conventions.  
Source: [Configure tracing for AI agent frameworks – learn.microsoft.com/en-us/azure/foundry/observability/how-to/trace-agent-framework](https://learn.microsoft.com/en-us/azure/foundry/observability/how-to/trace-agent-framework)

### 4.3 Tracing Agent Turns and LLM Calls — Recommended Implementation

```
[Container App]
    └─ Azure Monitor OTel Distro (SDK)
         ├─ HTTP spans  (webhooks, Bot Framework)
         ├─ gen_ai spans (Azure OpenAI Realtime API calls)
         ├─ custom spans (inventory mutation, conversation turn)
         └─ dependency spans (Cosmos DB, Service Bus, Graph API)
              │
              ▼
         Application Insights workspace
              │
              ▼
         Azure Monitor Logs (KQL queries, dashboards, alerts)
```

**PII / prompt content:** Prompt and completion content capture is **opt-in**. Use `OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT=true` (or the SDK equivalent) only in non-production; omit or filter in production for compliance.

**Reference implementation:** [github.com/PreiyaaKedia/agent-tracing-azure](https://github.com/PreiyaaKedia/agent-tracing-azure) — demonstrates LangChain + Microsoft Agent Framework + Application Insights.

---

## 5. Recommendation

### Use **Azure Container Apps + Bicep + `azd`**

**Reasoning:**

1. **ACA is the only option that natively satisfies all three simultaneous requirements** (HTTP webhooks, web serving, and long-lived WebSocket/audio connections) at low operational overhead. App Service can do the same but at higher minimum cost and without scale-to-zero. Functions cannot hold WebSocket connections at all.

2. **Scale-to-zero keeps dev/test costs near $0** — critical for a greenfield project. When traffic grows, KEDA-based autoscaling handles it without re-architecture.

3. **Container-first deploy** means the same image runs locally (Docker Compose), in CI, and in production. No language runtime version mismatch.

4. **Managed Identity is first-class** and integrates directly with Azure OpenAI, Cosmos DB, Graph API, Key Vault — no secrets in environment variables.

5. **Bicep + `azd`** is Microsoft's strategic recommendation for Azure-only projects. `azd up` gives a one-command dev environment; Bicep provides day-zero resource support and no state file. The awesome-azd template gallery already contains AI agent + ACA reference architectures to start from.

6. **Application Insights + OTel Distro + GenAI semantic conventions** provides end-to-end tracing of agent turns, LLM calls (token usage, latency), and tool invocations with minimal instrumentation code.

### Trade-offs That Would Overturn This Recommendation

| Situation | Alternative |
|---|---|
| Team prefers code-first (no Dockerfile), strong .NET / Node.js background | **Azure App Service** (Premium P2v3) — simpler deploy story, always-warm, same managed identity and custom domain support |
| Multi-cloud or strong Terraform skills | **Terraform** instead of Bicep (no change to hosting choice) |
| Webhooks and AI logic are easily decomposed into discrete event handlers with no long-lived connections needed | **Azure Functions** (Flex Consumption) for cost and simplicity; use **Azure Web PubSub** for any realtime needs |
| Voice channel is a separate, latency-sensitive service | Deploy voice component on App Service Premium (always-warm) and webhook/web components on ACA — hybrid |
| Heavy compliance / network isolation requirements | **ACA Dedicated workload profile** with VNet injection, or **App Service Environment v3 (ASEv3)** |

---

## 6. Source Index

| # | Source | URL | Last verified |
|---|---|---|---|
| 1 | ACA overview | https://learn.microsoft.com/en-us/azure/container-apps/overview | 2026-07-07 |
| 2 | ACA ingress overview | https://learn.microsoft.com/en-us/azure/container-apps/ingress-overview | 2026-08-05 |
| 3 | ACA managed identities | https://learn.microsoft.com/en-us/azure/container-apps/managed-identity | 2026-02-13 |
| 4 | ACA custom domains and certificates | https://learn.microsoft.com/en-us/azure/container-apps/custom-domains-certificates | 2025 |
| 5 | ACA free managed certificates | https://learn.microsoft.com/en-us/azure/container-apps/custom-domains-managed-certificates | 2025 |
| 6 | ACA pricing | https://azure.microsoft.com/en-us/pricing/details/container-apps/ | 2025 |
| 7 | App Service overview | https://learn.microsoft.com/en-us/azure/app-service/overview | 2026-08-18 |
| 8 | App Service WebSockets | https://learn.microsoft.com/en-us/azure/app-service/web-sites-enable-websockets | 2025 |
| 9 | App Service managed identity | https://learn.microsoft.com/en-us/azure/app-service/overview-managed-identity | 2025 |
| 10 | App Service custom domain | https://learn.microsoft.com/en-us/azure/app-service/tutorial-secure-domain-certificate | 2025 |
| 11 | Functions Scale and Hosting | https://learn.microsoft.com/en-us/azure/azure-functions/functions-scale | 2026-08-12 |
| 12 | Functions pricing | https://azure.microsoft.com/en-us/pricing/details/functions/ | 2025 |
| 13 | Comparing Terraform and Bicep | https://learn.microsoft.com/en-us/azure/developer/terraform/get-started/comparing-terraform-and-bicep | 2025 |
| 14 | Use Terraform with azd | https://learn.microsoft.com/en-us/azure/developer/azure-developer-cli/use-terraform-for-azd | 2025 |
| 15 | azd overview | https://learn.microsoft.com/en-us/azure/developer/azure-developer-cli/overview | 2026-01-09 |
| 16 | awesome-azd gallery | https://azure.github.io/awesome-azd/ | 2025 |
| 17 | Application Insights OTel overview | https://learn.microsoft.com/en-us/azure/azure-monitor/app/app-insights-overview | 2026-05-28 |
| 18 | Enable OTel in Application Insights | https://learn.microsoft.com/en-us/azure/azure-monitor/app/opentelemetry-enable | 2026-08-19 |
| 19 | Trace AI agent frameworks (Foundry) | https://learn.microsoft.com/en-us/azure/foundry/observability/how-to/trace-agent-framework | 2025 |
| 20 | OTel GenAI Semantic Conventions | https://github.com/open-telemetry/semantic-conventions-genai | 2026 |
| 21 | Agent tracing reference impl | https://github.com/PreiyaaKedia/agent-tracing-azure | 2025 |
