# Survey: Azure Agent Runtime Options
**Research date:** 2026-08-28  
**Issue:** #3 — "Survey Azure agent runtime options"  
**Context:** Multi-channel inventory agent on Azure — one agent owning a persisted inventory,
addressable in natural language over email, web, Teams, via text and voice.

---

## 1. Framing: the four stacks under review

| # | Stack | What it is |
|---|-------|-----------|
| A | **Microsoft Foundry Agent Service** | Managed hosted runtime inside the Microsoft Foundry platform (formerly Azure AI Foundry / Azure AI Studio). Runs prompt agents declaratively or hosted agents (your code, Foundry hosts it). |
| B | **Semantic Kernel + Microsoft Agent Framework** | Open-source orchestration SDK (Microsoft-backed). `Microsoft Agent Framework` is the open-source companion SDK that wraps SK for agentic patterns; hosted agents in Foundry can use it. |
| C | **Microsoft 365 Agents SDK** | Multi-channel messaging abstraction layer — the "plumbing" between channels (Teams, web chat, Slack, …) and whatever AI/logic you wire in. Successor to Bot Framework SDK. |
| D | **Azure OpenAI direct + own orchestration** | Call the Chat Completions or Responses API yourself; you own threads, routing, channel adapters, all of it. |

---

## 2. Stack-by-stack analysis

### 2A — Microsoft Foundry Agent Service

**Sources:**
- Overview: <https://learn.microsoft.com/en-us/azure/foundry/agents/overview>  
- Runtime components: <https://learn.microsoft.com/en-us/azure/foundry/agents/concepts/runtime-components>  
- Toolbox: <https://learn.microsoft.com/en-us/azure/foundry/agents/concepts/toolbox-overview>  
- Limits / regions: <https://learn.microsoft.com/en-us/azure/foundry/agents/concepts/limits-quotas-regions>  
- Publish to Teams: <https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/publish-copilot>  
- Environment setup: <https://learn.microsoft.com/en-us/azure/foundry/agents/environment-setup>  
- Quickstart (prompt agent): <https://learn.microsoft.com/en-us/azure/foundry/agents/quickstarts/prompt-agent>

#### GA / preview status
**Generally Available (GA).** The limits/regions page states explicitly: *"Foundry Agent Service is generally available (GA). Some sub-features are in public preview and might have different constraints."*  
Sub-features that are explicitly **preview** as of the research date: Tool Search, Skills (in Toolbox), Agent Optimizer, A2A protocol endpoint, the agent observability dashboard, and Computer Use tool.  
([Source: limits-quotas-regions page](https://learn.microsoft.com/en-us/azure/foundry/agents/concepts/limits-quotas-regions))

#### SDK language support
The Foundry Agent Service SDK (`azure-ai-projects` v2.x) ships for:

| Language | Package | Verified from quickstart |
|----------|---------|--------------------------|
| **Python** | `azure-ai-projects>=2.3.0` (PyPI) | Yes |
| **C# / .NET** | `Azure.AI.Projects` + `Azure.AI.Projects.Agents` (NuGet) | Yes |
| **TypeScript / JavaScript** | `@azure/ai-projects` (npm) | Yes |
| **Java** | `azure-ai-agents:2.2.0` (Maven) | Yes |
| **REST** | Any language over HTTPS | Yes |

([Source: quickstart](https://learn.microsoft.com/en-us/azure/foundry/agents/quickstarts/prompt-agent))

#### Tool and function-calling model
Two complementary mechanisms:

1. **Built-in platform tools** available without any code: web search, file search, code interpreter, image generation, Azure AI Search, SharePoint, Fabric IQ, WorkIQ, Bing Search (grounding), memory. Governed by the **Toolbox** — a single managed MCP-compatible endpoint that centralises auth, versioning, and governance across all tools. ([Source: toolbox-overview](https://learn.microsoft.com/en-us/azure/foundry/agents/concepts/toolbox-overview))

2. **Custom tools** via: JSON-described function definitions (OpenAI-style), OpenAPI specs, MCP servers (remote or Azure-Functions-hosted), or full hosted-agent code. The agent runtime handles the tool-call loop (detect intent → invoke tool → return result → re-run model) automatically for prompt agents; hosted agents own that loop themselves.

Any agent (prompt or hosted) can connect to a Toolbox via its MCP endpoint, gaining auth (Entra managed identity, OAuth OBO, key-based) and governance for free.

#### Conversation thread / state management
**Fully managed for prompt agents.** The service exposes a three-object model: **agent** (definition), **conversation** (persisted history across turns), **response** (unit of execution). You create a conversation object once; subsequent responses reference it and history is server-side. ([Source: runtime-components](https://learn.microsoft.com/en-us/azure/foundry/agents/concepts/runtime-components))

Standard setup allows BYO Cosmos DB for conversation storage (data stays in your tenant). Basic setup uses Microsoft-managed storage.

#### Channel binding (especially Teams)
**Native Teams/M365 publishing via portal or REST API.** From the Foundry portal, a single publish step:
- Compiles a Teams app manifest (`.zip`)
- Submits it to the M365 Copilot and Teams agent catalogs
- Enables the `activity` protocol
- Sets authorization (`BotServiceRbac` for "just you" or `BotServiceTenant` for org-wide)

Org-wide deployment requires M365 admin approval; the agent then appears under **Built by your org** in Teams.  
([Source: publish-copilot how-to](https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/publish-copilot))

Additional protocols supported: OpenResponses, Invocations (custom apps/webhooks), A2A agent-to-agent (**preview**).

For other channels (email, web chat, custom voice endpoints) you would front the Foundry agent endpoint with the **M365 Agents SDK** (Option C) or your own adapter code.

#### Realtime / voice support
Not a native Agent Service feature for *prompt agents*. However, hosted agents can call the **Azure OpenAI Realtime API** directly (WebRTC, WebSocket, SIP). See Section 2D for realtime model details. A hosted agent with a realtime API integration gives you speech-in/speech-out within the Foundry hosting envelope.

#### Regional availability
~30 Azure public regions as of research date (see full table in source):

Australia East, Brazil South, Canada Central, Canada East, Central US, East US, East US 2, France Central, Germany West Central, Italy North, Japan East, Japan West, Korea Central, North Central US, Norway East, Poland Central, South Africa North, South Central US, Southeast Asia, South India, Spain Central, Sweden Central, Switzerland North, Switzerland West, UAE North, UK South, West Central US, West Europe, West US, West US 3.

Also available in **Azure Government** (US Gov Virginia and US Gov Arizona) with a feature subset.

> Note: tool availability varies by region. For example, file search is not available in Italy North and Brazil South; Computer Use only available in Canada Central, East US 2, Japan West, and a few others. Always verify the tool-by-region matrix before selecting a region. ([Source: limits-quotas-regions](https://learn.microsoft.com/en-us/azure/foundry/agents/concepts/limits-quotas-regions))

---

### 2B — Semantic Kernel + Microsoft Agent Framework

**Sources:**
- SK overview: <https://learn.microsoft.com/en-us/semantic-kernel/overview/>  
- SK Agent Framework: <https://learn.microsoft.com/en-us/semantic-kernel/frameworks/agent/>  
- GitHub (Microsoft Agent Framework / SK Agents): <https://github.com/microsoft/agent-framework>

#### GA / preview status
**Semantic Kernel v1.0+: GA** for C#, Python, and Java. The overview page states: *"Version 1.0+ support across C#, Python, and Java means it's reliable, committed to non-breaking changes."*  
([Source: SK overview](https://learn.microsoft.com/en-us/semantic-kernel/overview/))

The SK Agent Framework (sub-package) ships inside the main SK package. The `AzureAIAgent` and `OpenAIResponsesAgent` agent types are available in the Python module; the C# `OpenAIAssistantAgent` targets the OpenAI Assistants API. Check individual NuGet / PyPI package release notes for per-agent-type GA status — the framework evolves quickly.

#### SDK language support

| Language | Package | Notes |
|----------|---------|-------|
| **C# / .NET** | `Microsoft.SemanticKernel` + `Microsoft.SemanticKernel.Agents.*` (NuGet) | GA; Java and C# have the widest agent type coverage |
| **Python** | `semantic-kernel` (PyPI, includes `.agents` module) | GA |
| **Java** | `semantickernel-agents-core` (Maven) | GA |
| ~~JavaScript~~ | Not in GA agent packages | SK JS exists for prompt work but agent packages were not listed in GA |

([Source: SK Agent Framework install page](https://learn.microsoft.com/en-us/semantic-kernel/frameworks/agent/))

#### Tool and function-calling model
**Plugin model.** You annotate your .NET / Python / Java functions with SK plugin attributes; the kernel converts them to OpenAI-compatible tool definitions and handles the model↔tool loop. Supports:
- Native function plugins (annotated code)
- OpenAPI spec plugins
- MCP server connectors
- The `AzureAIAgent` type can use Foundry-managed tools (file search, code interpreter, etc.) through the Foundry project endpoint

The model decides which plugins to call; SK marshals the calls and returns results.

#### Conversation thread / state management
**Developer responsibility** for persistent storage. SK provides `ChatHistory` (in-process object) and abstractions for chat history, but does **not** manage a server-side conversation store out of the box. You must wire in your own persistence (e.g., Azure Cosmos DB, Redis, Azure Table Storage). If you use `AzureAIAgent` backed by Foundry, thread state is managed by Foundry under the covers.

#### Channel binding (especially Teams)
**None natively.** SK has no channel adapters. You would pair it with:
- **M365 Agents SDK** (Option C) for Teams/multi-channel
- **Azure Bot Framework** (older approach, still supported)
- Your own HTTP surface

The SK overview notes: *"Any existing chat-based APIs are easily expanded to support additional modalities like voice and video"* — but this means you connect SK to the Azure OpenAI Realtime API yourself; it's not automatic.

#### Realtime / voice support
No native voice support. Achievable by calling Azure OpenAI Realtime API from within a SK agent's plugin or tool, but requires custom code and session management.

#### Regional availability
**N/A — it is a library, not a hosted service.** You host it wherever you deploy your application (Azure App Service, Container Apps, AKS, etc.). It calls Foundry / Azure OpenAI endpoints in whatever region you configure.

---

### 2C — Microsoft 365 Agents SDK

**Sources:**
- Overview: <https://learn.microsoft.com/en-us/microsoft-365/agents-sdk/agents-sdk-overview>  
- Landing page / docs index: <https://learn.microsoft.com/en-us/microsoft-365/agents-sdk/>  
- GitHub samples: <https://github.com/microsoft/Agents>

#### GA / preview status
The SDK docs show `ms.date: 2026-04-28` and `updated_at: 2026-08-19`. The overview does not include a preview banner. The SDK is the **successor to the Bot Framework SDK v4**, rebranded and extended for M365 Copilot and Teams integration. It appears to be in production use, but the docs do **not** explicitly carry a "GA" badge as Foundry Agent Service does. **Treat as production-ready but verify GA label before committing to SLA-sensitive workloads.**

#### SDK language support

| Language | Package location |
|----------|-----------------|
| **C# / .NET** | .NET 8.0 SDK, samples in `microsoft/Agents/tree/main/samples/dotnet` |
| **JavaScript / Node.js** | Node.js 18+, samples in `…/samples/nodejs` |
| **Python** | Python 3.9–3.11, samples in `…/samples/python` |

([Source: agents-sdk-overview](https://learn.microsoft.com/en-us/microsoft-365/agents-sdk/agents-sdk-overview))

#### Tool and function-calling model
**Intentionally none.** The SDK's own description: *"The Agents SDK isn't an AI model, an orchestration engine, or a no-code builder. The Agents SDK doesn't decide what an agent says."* You plug in Azure OpenAI / Foundry / SK for the AI layer; the SDK handles message routing and state. Tool calling is entirely the responsibility of whatever AI component you wire in.

#### Conversation thread / state management
**Built-in turn and state management.** The SDK introduces the concept of a **turn** (one unit of conversation work) and provides built-in state and storage management across turns. Storage adapters connect to Azure Blob Storage, Cosmos DB, etc. It normalises incoming messages (from any channel) into an `Activity` object, routes to the right handler, and sends the response back in channel-native format.

#### Channel binding (especially Teams)
**This is the SDK's primary purpose.** Supported channels via Azure Bot Service adapters:
- **Microsoft Teams** ✓
- **Microsoft 365 Copilot** ✓
- Web Chat ✓
- Slack, Facebook Messenger, and other Bot Framework channels ✓

You write your agent logic once; the SDK translates to and from each channel's message format. Adding a new channel does not require rewriting core logic. The SDK connects to Azure Bot Service for channel registration and authentication.

#### Realtime / voice support
Not addressed in the main SDK overview. Voice scenarios would require you to bridge Azure Communication Services or Azure OpenAI Realtime API outside the SDK. The SDK's activity model focuses on text and rich-card channels.

#### Regional availability
**N/A — it is a library.** You deploy it as an Azure Bot Service registration (globally available) backed by an Azure-hosted compute resource in any region.

---

### 2D — Azure OpenAI direct + own orchestration

**Sources:**
- Function calling: <https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/function-calling>  
- Realtime API: <https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/realtime-audio>  
- Microsoft Foundry (platform home): <https://learn.microsoft.com/en-us/azure/foundry/what-is-foundry>

#### GA / preview status
**Chat Completions API with function calling: GA.** The function-calling page does not carry a preview label; uses the stable `/openai/v1/` endpoint. ([Source: function-calling](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/function-calling))

**Realtime API: GA for current models.** The realtime audio page lists several models available including `gpt-realtime-2` (2026-05-07) and `gpt-realtime-1.5` (2026-02-23). The page uses `/openai/v1` GA endpoint format. ([Source: realtime-audio](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/realtime-audio))

#### SDK language support
Any language that can make HTTP calls. Official client libraries:
- **Python**: `openai` package (OpenAI-compatible), `azure-ai-projects` for Foundry endpoint
- **C#**: `Azure.AI.OpenAI` NuGet
- **JavaScript / TypeScript**: `openai` npm package
- **Java**: `com.azure:azure-ai-openai`
- **REST**: Direct HTTPS against `https://<resource>.openai.azure.com/openai/v1/`

#### Tool and function-calling model
**OpenAI-compatible tool/function calling.** You describe tools as JSON schemas in the request; the model returns a `tool_calls` array when it decides to use a tool; you execute and return results; you call the API again with results appended to messages. Parallel function calling supported (multiple tools in one turn). ([Source: function-calling](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/function-calling))

You own the loop — no automatic retry, no automatic multi-turn management.

#### Conversation thread / state management
**Entirely developer responsibility.** There is no server-side thread object. You must:
- Maintain the `messages` array in your code or storage
- Decide how many turns of history to include (token budget)
- Persist and retrieve history for returning users

This is the highest-effort option for state management.

#### Channel binding (especially Teams)
**None provided.** You must build every channel adapter. For Teams you would integrate Azure Bot Service or the M365 Agents SDK on top of your OpenAI calls.

#### Realtime / voice support
**First-class, GA.** The Azure OpenAI Realtime API supports three connection methods:

| Method | Latency | Best for |
|--------|---------|---------|
| **WebRTC** | ~100ms | Browser / web apps |
| **WebSocket** | ~200ms | Server-to-server, backend |
| **SIP** | varies | Telephony / call centre |

Current GA realtime models (as of 2026-08-28):
- `gpt-realtime-2` (2026-05-07) — general speech-to-speech
- `gpt-realtime-1.5` (2026-02-23)
- `gpt-realtime-translate` (2026-05-06) — multilingual translation
- `gpt-realtime-whisper` (2026-05-06) — transcription
- `gpt-live-transcribe` (2026-07-29)

Realtime models are available as **global deployments** across regions. For per-region availability, see the [region availability matrix](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/concepts/models-sold-directly-by-azure-region-availability).

([Source: realtime-audio](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/realtime-audio))

#### Regional availability
Azure OpenAI is available in all major Azure regions. Global deployments (recommended for realtime) are region-agnostic from a quota standpoint. Refer to the [Azure OpenAI model availability page](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/concepts/models-sold-directly-by-azure-region-availability) for per-region model support.

---

## 3. Comparison matrix

| Dimension | A: Foundry Agent Service | B: Semantic Kernel + Agent Framework | C: M365 Agents SDK | D: Azure OpenAI Direct |
|-----------|--------------------------|--------------------------------------|--------------------|------------------------|
| **SDK languages** | Python, C#, TypeScript, Java, REST | C#, Python, Java (JS not in GA agent packages) | C#, JavaScript, Python | Any (Python, C#, JS, Java officially) |
| **Tool / function calling** | Fully managed (prompt agent) or self-managed (hosted); Toolbox for governance | Plugin model, SK handles loop; developer owns storage | Not the SDK's concern — delegate to AI layer | Developer owns loop; OpenAI-style JSON tool schemas |
| **Conversation state** | Fully managed server-side (Conversations API); BYO storage option | Developer responsibility; `ChatHistory` in-process; Foundry-backed AzureAIAgent delegates to Foundry | Built-in turn + storage management; adapters for Cosmos DB, Blob | Fully developer responsibility; messages[] array |
| **Teams channel binding** | Native 1-click publish from portal (GA) | None; needs M365 Agents SDK or Bot Framework wrapper | Native; SDK's primary purpose | None; needs Bot Service + adapter |
| **Multi-channel (email, web)** | Via M365 Agents SDK or custom adapter | Via M365 Agents SDK or custom adapter | Native (web chat, Teams, Slack, etc.) | Fully custom |
| **Realtime / voice** | Via hosted agent + Azure OpenAI Realtime API | Via Azure OpenAI Realtime API plugin (custom) | Not addressed; custom bridging needed | Native — WebRTC / WebSocket / SIP (GA) |
| **GA status** | **GA** (some sub-features preview) | **GA** v1.0+ for C#/Python/Java | Production-ready (no explicit GA badge found) | **GA** (Chat Completions, Responses API, Realtime) |
| **Regional availability** | ~30 regions + Azure Gov | N/A (library) — deploy anywhere | N/A (library) — Bot Service globally available | All Azure OpenAI regions; realtime via global deployment |
| **Infrastructure to manage** | None for prompt agents; container for hosted agents | Your hosting (App Service, Container Apps, etc.) | Your hosting + Azure Bot Service registration | Your hosting + Bot Service registration (for channels) |
| **Cost model** | Inference + tool usage (+ container compute for hosted) | Inference (you pay OpenAI/Foundry); compute = your choice | Bot Service (free tier available) + your compute | Pure inference + your compute |

---

## 4. These stacks compose, not compete

A key insight from the documentation: **these are not mutually exclusive choices.** Microsoft's own guidance shows them as layers:

```
┌──────────────────────────────────────────────────────────┐
│  Channel layer: M365 Agents SDK (Teams, web, Slack, …)   │
├──────────────────────────────────────────────────────────┤
│  Orchestration layer: Semantic Kernel (plugins, routing) │
│           OR Foundry Prompt/Hosted Agent                 │
├──────────────────────────────────────────────────────────┤
│  Inference + tools: Azure OpenAI / Foundry Responses API │
│  Toolbox: centralised tool governance (MCP endpoint)     │
└──────────────────────────────────────────────────────────┘
```

The Foundry portal's own [publish-to-Teams how-to](https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/publish-copilot) uses an Azure Bot Service resource under the hood — the same infrastructure that M365 Agents SDK targets. A Foundry Hosted Agent can use Microsoft Agent Framework (SK) internally. The SK `AzureAIAgent` type delegates thread state to Foundry. Everything converges on the same Azure primitives.

---

## 5. Recommendation

### Primary recommendation: **Foundry Agent Service (prompt or hosted) + M365 Agents SDK**

**Use Foundry Agent Service as the agent brain:**
- GA, fully managed, all four SDK languages supported.
- Manages conversation threads server-side — essential for a persisted inventory agent that must maintain context across sessions, channels, and users.
- Native Teams publish (1-click from portal or REST API) satisfies the Teams channel requirement.
- Toolbox provides the governed, versioned tool surface where inventory mutation functions live — avoiding per-agent credential duplication as the project scales.
- Hosted agent option lets you bring custom orchestration (SK, LangGraph, etc.) without losing Foundry's hosting, scaling, identity, and observability.

**Layer M365 Agents SDK for multi-channel reach:**
- Foundry's native publish handles Teams and M365 Copilot directly.
- For the email channel and the public website chat widget, use M365 Agents SDK adapters backed by Azure Bot Service; they normalise messages from those surfaces into the same activity format and hand off to the Foundry agent endpoint.
- Write channel-specific logic (email parsing, web widget UX) once per channel; agent logic stays in Foundry and is shared.

**For voice:** 
- Implement a hosted Foundry agent that opens a WebSocket session with the Azure OpenAI Realtime API (`gpt-realtime-2`). The hosted agent receives audio, streams it to the realtime model, executes inventory tool calls via Foundry Toolbox, and streams audio back. This keeps voice within the Foundry hosting envelope with the same identity, observability, and tool governance.

### When to reconsider

| If this is true… | …consider instead |
|-----------------|-------------------|
| You need complete control over orchestration logic (complex multi-step planning, custom retry, multi-agent graphs) and Foundry's hosted-agent container model feels heavy | Use **Semantic Kernel** as your orchestration layer inside a Foundry Hosted Agent, or on your own compute with M365 Agents SDK for channels. You keep SK's flexibility without losing channel reach. |
| Teams is the *only* channel and you want no managed service dependency | Use **M365 Agents SDK + SK** directly; deploy to Container Apps; register with Azure Bot Service for Teams. Lower cost at small scale. |
| Your team is heavily invested in SK already and wants gradual migration | SK's `AzureAIAgent` type delegates to Foundry under the covers, so you can adopt Foundry incrementally without rewriting SK plugins. |
| Strict data-residency requirements rule out Foundry Agent Service in a required region | Check the region table; Foundry covers ~30 regions and Azure Gov. If you need a region not listed, use Azure OpenAI direct + M365 Agents SDK with BYO persistence (Cosmos DB in your region). |
| You need a fully serverless / consumption-only pricing model with no standing infrastructure | Foundry prompt agents and Azure Bot Service both have per-call pricing models; this is compatible. Foundry standard setup requires standing BYO resources (Cosmos DB, Storage, AI Search) which have baseline costs. |

### Trade-offs that would overturn the recommendation

1. **Preview-feature dependencies**: If inventory mutations require Computer Use, A2A protocol, Tool Search, or Agent Optimizer — all currently **preview** — you accept preview SLA and potential breaking changes.
2. **Voice maturity**: The Realtime API + Foundry Hosted Agent combination is powerful but requires more integration work than a pre-built voice channel. If a no-code voice channel is required, Azure Communication Services + Azure AI Speech + Bot Service is an alternative path.
3. **Operational complexity of Standard Setup**: BYO Cosmos DB + AI Search + Storage adds infrastructure to manage. Start with Basic Setup for development; migrate to Standard when data-residency or CMK requirements are confirmed.

---

## 6. Sources summary

| Source | URL | Last verified |
|--------|-----|---------------|
| Foundry Agent Service overview | <https://learn.microsoft.com/en-us/azure/foundry/agents/overview> | 2026-08-28 (doc updated 2026-08-27) |
| Foundry runtime components | <https://learn.microsoft.com/en-us/azure/foundry/agents/concepts/runtime-components> | 2026-08-28 (doc updated 2026-08-27) |
| Foundry Toolbox overview | <https://learn.microsoft.com/en-us/azure/foundry/agents/concepts/toolbox-overview> | 2026-08-28 (doc updated 2026-07-31) |
| Foundry limits, quotas, regions | <https://learn.microsoft.com/en-us/azure/foundry/agents/concepts/limits-quotas-regions> | 2026-08-28 (doc updated 2026-08-20) |
| Foundry: publish to Teams / M365 | <https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/publish-copilot> | 2026-08-28 (doc updated 2026-07-15) |
| Foundry environment setup | <https://learn.microsoft.com/en-us/azure/foundry/agents/environment-setup> | 2026-08-28 (doc updated 2026-08-26) |
| Foundry prompt agent quickstart | <https://learn.microsoft.com/en-us/azure/foundry/agents/quickstarts/prompt-agent> | 2026-08-28 (doc updated 2026-08-26) |
| What is Microsoft Foundry | <https://learn.microsoft.com/en-us/azure/foundry/what-is-foundry> | 2026-08-28 (doc updated 2026-08-27) |
| Semantic Kernel overview | <https://learn.microsoft.com/en-us/semantic-kernel/overview/> | 2026-08-28 |
| Semantic Kernel Agent Framework | <https://learn.microsoft.com/en-us/semantic-kernel/frameworks/agent/> | 2026-08-28 |
| M365 Agents SDK overview | <https://learn.microsoft.com/en-us/microsoft-365/agents-sdk/agents-sdk-overview> | 2026-08-28 (doc updated 2026-08-19) |
| M365 Agents SDK landing page | <https://learn.microsoft.com/en-us/microsoft-365/agents-sdk/> | 2026-08-28 |
| Azure OpenAI function calling | <https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/function-calling> | 2026-08-28 (doc updated 2026-08-25) |
| Azure OpenAI Realtime API | <https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/realtime-audio> | 2026-08-28 (doc updated 2026-07-31) |
