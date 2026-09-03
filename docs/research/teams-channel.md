# Teams Channel: Survey of Azure and Microsoft 365 Options

**Research date:** 2026-08-28  
**Issue:** #5 — Survey Azure and Microsoft 365 options for a Teams channel  
**Context:** Multi-channel inventory agent on Azure — one backend with its own database, reachable over Teams, email, web, and voice.

---

## Executive Summary

As of August 2026, Microsoft offers three distinct paths for exposing a custom agent inside Microsoft Teams. The **Bot Framework SDK** (the original path) is now fully archived and unsupported — its GitHub repos are read-only and support tickets are no longer serviced as of 31 December 2025. The recommended replacements are:

1. **Teams SDK** (formerly "Teams AI Library") — Teams-exclusive, GA for TypeScript/JavaScript and C#, Developer Preview for Python. Best when the agent lives only in Teams.
2. **Microsoft 365 Agents SDK** — Multi-channel framework that is the direct successor to Bot Framework SDK, GA for C#, JavaScript, and Python. Best when the same agent must also serve email, web, or other channels.
3. **Declarative Agents in Microsoft 365 Copilot** — Configuration-only agents that run on Copilot's hosted LLM/orchestrator. No custom backend; require an M365 Copilot license. Unsuitable for this project.

**Bottom line:** For this project, use the **M365 Agents SDK + Teams SDK** combination — the Agents SDK as the multi-channel abstraction layer, and the Teams SDK for Teams-specific features. Register one Azure Bot resource to connect the Teams channel. Both Teams SDK and M365 Agents SDK are built on top of the same Azure Bot Service registration/channel infrastructure.

---

## Option 1 — Azure Bot Service + Bot Framework SDK

### Status

> ⚠️ **Deprecated / Archived.**  
> The Bot Framework SDK GitHub repositories are archived and no longer maintained. Support tickets are no longer serviced as of **31 December 2025**.  
> Source: [What is the Bot Framework SDK — Important notice](https://learn.microsoft.com/en-us/azure/bot-service/bot-service-overview?view=azure-bot-service-4.0) (updated 2026-01-08)

Microsoft's official guidance: *"To build agents with your choice of AI services, orchestration, and knowledge, consider using the Microsoft 365 Agents SDK."*

The **Azure Bot Service** Azure resource (used for bot registration and channel connection) still exists and is still required as an infrastructure component by both the M365 Agents SDK and Teams SDK. The deprecated part is the **BotBuilder SDK** packages (`botbuilder-*`, `Microsoft.Bot.Builder.*`).

### Hosting and Auth Requirements

- **Hosting:** Any internet-accessible HTTPS endpoint (Azure App Service, Azure Container Apps, etc.). Azure Bot Service acts as the broker; your backend listens at `/api/messages`.
- **Auth:** Azure Bot Service issues a service-to-service JWT. Your app validates incoming requests with the Bot Service authentication middleware. User authentication uses the Bot Framework Token Service + Microsoft Entra ID.

### User Identity / SSO / OBO

Teams SSO for bots works via the Bot Framework Token Service:

1. Teams sends an OAuth token to the bot.
2. The bot exchanges it at the Bot Framework Token Service for a user-level access token.
3. This token can be used with the On-Behalf-Of (OBO) flow to call Microsoft Graph or your own Entra-protected APIs on behalf of the signed-in user.

> ⚠️ **Scope limitation:** SSO is supported in **personal scope (1:1 chat) and group chat scope only**. It is **not** supported in channel (team) scope.  
> Source: [Enable SSO with Microsoft Entra ID — Teams](https://learn.microsoft.com/en-us/microsoftteams/platform/bots/how-to/authentication/bot-sso-overview) (updated 2026-08-18)

### Proactive Messages

Fully supported. The bot calls the Bot Service to open or resume a conversation outside of an incoming activity. Requirements:

- App must be pre-installed for the target user/channel, or installed via Microsoft Graph.
- Bot must store the `tenantId` + `userId`/`channelId` from an earlier activity.
- Global service URL for public cloud: `https://smba.trafficmanager.net/teams/`.

Source: [Send proactive messages — Teams](https://learn.microsoft.com/en-us/microsoftteams/platform/bots/how-to/conversations/send-proactive-messages) (updated 2026-08-20)

### Voice / Calling in Teams

Full audio/video calling support via the **Microsoft Graph Calls API**. Requires:

1. Set `bots[0].supportsCalling: true` and/or `bots[0].supportsVideo: true` in the app manifest.
2. Enable the **Calling** tab in the Azure Bot Service Teams channel configuration and provide a calling webhook URL.
3. Request and receive **admin consent** for the relevant Microsoft Graph application permissions:
   - `Calls.Initiate.All`, `Calls.InitiateGroupCall.All`, `Calls.JoinGroupCall.All`, `Calls.JoinGroupCallasGuest.All`, `Calls.AccessMedia.All` — all require admin consent.

> ℹ️ Bot Framework SDK / legacy approach. The same mechanism works with Teams SDK and M365 Agents SDK because calling is configured at the Teams channel (Azure resource) and manifest level, not in the SDK layer.

Source: [Register Calls & Meetings Bot — Teams](https://learn.microsoft.com/en-us/microsoftteams/platform/bots/calls-and-meetings/registering-calling-bot) (updated 2026-08-03)

### App Packaging and Tenant Admin Approval

- Create a `.zip` app package containing:
  - `manifest.json` (Teams app manifest, references your bot's Microsoft App ID)
  - `color.png` (192×192) and `outline.png` (32×32) icons
- **Org-internal distribution:** Upload to the Teams Admin Center → Manage apps → Org app catalog. A Teams admin must approve it before users can install it.
- **Sideloading (dev/test):** Upload directly in Teams if tenant allows custom app uploads. No admin approval required.
- **Teams Store (public):** Requires Microsoft's own app review process.

Source: [Package your app — Teams](https://learn.microsoft.com/en-us/microsoftteams/platform/concepts/build-and-test/apps-package), [Publish overview — Teams](https://learn.microsoft.com/en-us/microsoftteams/platform/concepts/deploy-and-publish/apps-publish-overview)

---

## Option 2 — Microsoft 365 Agents SDK / M365 Agents Toolkit

This option encompasses two related but distinct components:

| Component | What it is | Status |
|---|---|---|
| **Microsoft 365 Agents SDK** | Runtime framework (successor to Bot Framework SDK) | **GA** — C#, JavaScript, Python |
| **Teams SDK** (formerly Teams AI Library) | Teams-specific SDK with Teams APIs, Adaptive Cards, AI orchestration | **GA** — JavaScript/C#; **Developer Preview** — Python |
| **Microsoft 365 Agents Toolkit** | VS Code/Visual Studio tooling for scaffolding, packaging, deployment, SSO | **GA** (some features Preview) |

Source: [What is the Microsoft 365 Agents SDK](https://learn.microsoft.com/en-us/microsoft-365/agents-sdk/agents-sdk-overview) (updated 2026-08-19), [Teams SDK Welcome](https://learn.microsoft.com/en-us/microsoftteams/platform/teams-sdk/welcome) (updated 2026-07-27), [M365 Agents Toolkit Overview](https://learn.microsoft.com/en-us/microsoftteams/platform/toolkit/overview-agents-toolkit) (updated 2026-01-29)

### Relationship Between the Two SDKs

- **Teams SDK** is Teams-exclusive: it handles Teams-specific events, Adaptive Cards, meeting APIs, and Teams SSO natively. Use it when your agent lives only in Teams.
- **M365 Agents SDK** is the multi-channel abstraction layer. It normalises incoming `Activity` objects from Teams, web chat, email, Slack, etc., and routes them to your handler. Use it when the same agent must serve multiple surfaces.
- These are **not mutually exclusive.** The M365 Agents Toolkit page shows a "write once, run everywhere" architecture where both can be layered: the Agents SDK handles multi-channel routing while Teams SDK plugins handle Teams-specific behaviour.

### Hosting and Auth Requirements

**M365 Agents SDK:**
- Any HTTPS-accessible backend (Azure App Service, Azure Container Apps, Azure Functions, etc.).
- Still requires an **Azure Bot resource** (same Azure resource as the old Bot Service registration) to connect to channels.
- Auth for incoming requests: the SDK validates the service-to-service JWT from Azure Bot Service.

**Teams SDK:**
- Same hosting requirements.
- Auth is handled by the `App` class — configure with `builder.AddTeams()` (C#) / `new App({})` (TypeScript). Set credentials in environment variables (`CLIENT_ID`, `CLIENT_SECRET`, `CONNECTION_NAME`).
- `skipAuth: true` available for local development only.

Source: [Teams SDK Quickstart](https://learn.microsoft.com/en-us/microsoftteams/platform/teams-sdk/getting-started/quickstart) (updated 2026-07-27), [Update App Manifest to Enable SSO](https://learn.microsoft.com/en-us/microsoftteams/platform/bots/how-to/authentication/bot-sso-code) (updated 2026-08-03)

### User Identity / SSO / OBO

The SSO and OBO flow is the same mechanism as described under Option 1, now surfaced more cleanly via the Teams SDK `App` class:

```typescript
// TypeScript — initialize with OAuth connection name from Entra ID
const app = new App({
  oauth: { defaultConnectionName: process.env.CONNECTION_NAME ?? 'graph' }
});
```

```csharp
// C# — initialize with OAuth
var appBuilder = App.Builder().AddOAuth(connectionName);
builder.AddTeams(appBuilder);
```

At runtime:
1. The Teams client sends a request with the user's Teams identity.
2. Teams SDK / Bot Framework Token Service exchanges for a user token from Entra ID.
3. Your backend receives an access token you can use with OBO to call Graph or your own Entra-protected APIs on behalf of the user.
4. Consent is shown once per user (personal scope) or once per first-@mention user (group chat scope).

> ⚠️ Same scope restriction applies: SSO is not supported in Teams channel (team) scope.  
> Source: [Enable SSO with Microsoft Entra ID — Teams](https://learn.microsoft.com/en-us/microsoftteams/platform/bots/how-to/authentication/bot-sso-overview) (updated 2026-08-18)

For channel scope (posting in a channel), the bot can receive and respond to messages without SSO. User identity is available from the activity's `from.id` field (the user's Teams AAD Object ID).

### Proactive Messages

Fully supported, with the Teams SDK providing a cleaner API:

```typescript
// Send proactive message outside an activity handler
await app.Send(conversationId, { type: 'message', text: 'Inventory updated!' });
```

The SDK creates the conversation automatically and resolves the service URL. Requires the app to be installed in the target context first. For org-wide proactive messaging, use Microsoft Graph to pre-install the app.

Source: [Send proactive messages — Teams](https://learn.microsoft.com/en-us/microsoftteams/platform/bots/how-to/conversations/send-proactive-messages) (updated 2026-08-20)

### Voice / Calling in Teams

Same mechanism as described under Option 1 — configured at the manifest (`supportsCalling`/`supportsVideo`) and Azure Bot Service channel level, with Graph permissions for calling APIs. The Teams SDK and M365 Agents SDK do not add new calling abstractions; the Graph Calls API is used directly.

Calling bots can:
- Initiate outbound 1:1 and group calls (`Calls.Initiate.All`, admin consent required)
- Join scheduled meetings (`Calls.JoinGroupCall.All`, admin consent required)
- Access media streams (`Calls.AccessMedia.All`, admin consent required)

Source: [Register Calls & Meetings Bot — Teams](https://learn.microsoft.com/en-us/microsoftteams/platform/bots/calls-and-meetings/registering-calling-bot) (updated 2026-08-03)

### App Packaging and Tenant Admin Approval

Identical to Option 1. The M365 Agents Toolkit automates this:
- Scaffolds `manifest.json` template and icons.
- `teams project new` CLI command bootstraps the package.
- Toolkit provisions the Azure Bot resource and sets up SSO automatically.
- Same org-approval or sideload paths for distribution.

Source: [Teams SDK Quickstart](https://learn.microsoft.com/en-us/microsoftteams/platform/teams-sdk/getting-started/quickstart), [M365 Agents Toolkit Overview](https://learn.microsoft.com/en-us/microsoftteams/platform/toolkit/overview-agents-toolkit)

### Migration from Bot Framework SDK

Microsoft provides official migration guidance for all three languages:

- [Bot Framework SDK → M365 Agents SDK migration guidance](https://learn.microsoft.com/en-us/microsoft-365/agents-sdk/bf-migration-guidance) (updated 2026-04-29)
- Unsupported features in the migration: Adaptive Dialogs, Application Insights (legacy), Bot Framework Composer artifacts, `BotFrameworkAdapter` (replaced by cloud adapter), LUIS, QnA Maker, streaming connections (redesigned).

---

## Option 3 — Declarative Agents in Microsoft 365 Copilot

### What They Are

Declarative agents customise Microsoft 365 Copilot by providing instructions, knowledge sources, and actions (plugins/API calls). They run entirely on **Copilot's hosted orchestrator and foundation models** — no custom backend or LLM hosting required.

Source: [Declarative Agents for Microsoft 365 Copilot](https://learn.microsoft.com/en-us/microsoft-365/copilot/extensibility/overview-declarative-agent) (updated 2026-07-02), [Agents for Microsoft 365 Copilot](https://learn.microsoft.com/en-us/microsoft-365/copilot/extensibility/agents-overview) (updated 2026-08-11)

### Status

**GA.** Declarative agents are generally available and surfaced in Microsoft 365 Copilot Chat and within Microsoft 365 apps (Teams chat, Word, Outlook, etc.).

### Hosting and Auth Requirements

- **Hosting:** None for the agent runtime — Copilot hosts it. External APIs/data sources your agent calls via plugins must be HTTPS-accessible and Entra-authenticated.
- **Auth for users:** Users authenticate via their existing M365 session. The agent inherits Copilot's security, compliance, and RAI guarantees.
- **License requirement:** Users must have an **M365 Copilot add-on license** OR access via M365 Copilot Chat (possibly with usage-based billing for tenant-data access). No M365 Copilot license → cannot use declarative agents.

Source: [Licensing and Cost Considerations](https://learn.microsoft.com/en-us/microsoft-365/copilot/extensibility/cost-considerations) (updated 2026-07-02)

### User Identity / SSO / OBO

The signed-in M365 user's identity is used implicitly. Plugins (API connectors) can be configured with Entra authentication so the agent can call external APIs on the user's behalf. However, the OBO flow to a fully custom backend is only possible through an API plugin, not directly.

### Proactive Messages

**Not supported.** Declarative agents are user-initiated only — they respond to prompts within the Copilot UI. They cannot proactively send messages to users.

Source: [Agents for Microsoft 365 Copilot — comparison table](https://learn.microsoft.com/en-us/microsoft-365/copilot/extensibility/agents-overview) (updated 2026-08-11): *"Proactive interactions: Not supported; rely on user-initiated interactions."*

### Voice / Calling in Teams

**Not applicable.** Declarative agents operate within the Copilot text/chat interface. They do not participate in Teams audio or video calls.

### App Packaging and Tenant Admin Approval

- Packaged as a Teams app (same `.zip` format with manifest + icons) or deployed via Copilot Studio / Agent Builder.
- Published to the org app catalog (Teams admin approval) or to the Microsoft commercial marketplace.
- Admin must enable M365 Copilot extensibility features in the Microsoft 365 Admin Center.

### Summary Verdict for This Project

> ❌ **Declarative agents are not suitable** for this project.  
> Reasons:
> 1. We own the backend and the database — declarative agents cannot use a custom backend LLM or orchestrator.
> 2. No proactive messaging — inventory change notifications cannot be pushed to users.
> 3. Requires M365 Copilot license for every user — an additional per-seat cost not assumed in the project.
> 4. No voice/calling support.

---

## Feature Comparison Matrix

| Feature | Bot Framework SDK | M365 Agents SDK + Teams SDK | Declarative Agents |
|---|---|---|---|
| **Status** | ⛔ Archived (support ended Dec 2025) | ✅ GA (JS/C#); Python GA (Agents SDK) / Preview (Teams SDK) | ✅ GA |
| **Custom backend** | ✅ Yes | ✅ Yes | ❌ No (Copilot's orchestrator) |
| **Custom database** | ✅ Yes | ✅ Yes | ❌ No |
| **Hosting** | Your own Azure infra + Azure Bot resource | Your own Azure infra + Azure Bot resource | Microsoft-hosted |
| **User license required** | M365 (Teams access) | M365 (Teams access) | M365 Copilot add-on or Copilot Chat |
| **SSO / OBO to custom API** | ✅ Via Bot Framework Token Service + Entra OBO | ✅ Via Teams SDK `App` class + Entra OBO | ⚠️ Partial (via API plugin only) |
| **SSO in channel scope** | ❌ Not supported | ❌ Not supported | N/A |
| **Proactive messages** | ✅ Yes | ✅ Yes | ❌ No |
| **Voice / calling** | ✅ Via Graph Calls API + manifest flags | ✅ Via Graph Calls API + manifest flags | ❌ No |
| **Multi-channel (email, web, Slack)** | ✅ Via Bot Service channel adapters | ✅ Via M365 Agents SDK channel abstraction | ❌ M365 apps only |
| **App packaging** | Manifest + icons (.zip) | Manifest + icons (.zip), Toolkit automates | Manifest + icons or via Copilot Studio |
| **Tenant admin approval** | Required for org distribution | Required for org distribution | Required (M365 Admin Center) |
| **Calling admin consent** | Required (Graph app permissions) | Required (Graph app permissions) | N/A |

---

## Recommendation

### ✅ Use the M365 Agents SDK + Teams SDK

For this project — a custom inventory agent with its own backend and database, reachable over Teams, email, a website, and voice — the right choice is:

**Primary runtime:** [Microsoft 365 Agents SDK](https://learn.microsoft.com/en-us/microsoft-365/agents-sdk/agents-sdk-overview)  
**Teams-specific layer:** [Teams SDK](https://learn.microsoft.com/en-us/microsoftteams/platform/teams-sdk/welcome)  
**Tooling:** [Microsoft 365 Agents Toolkit](https://learn.microsoft.com/en-us/microsoftteams/platform/toolkit/overview-agents-toolkit) (VS Code extension or CLI)

**Why:**

1. **Multi-channel native.** The M365 Agents SDK is the only Microsoft-first-party framework designed to serve Teams, email, web chat, and other channels from the same codebase — which exactly matches this project's requirement.

2. **Future-proof.** It is the direct, fully supported successor to the archived Bot Framework SDK. The Bot Framework SDK cannot be used for new development.

3. **Custom backend and database.** Both SDKs assume your code, your LLM, your orchestrator, your storage — unlike declarative agents which hand control to Copilot's infrastructure.

4. **Proactive messaging.** Inventory mutations can trigger notifications to users even without a user-initiated conversation.

5. **Voice in Teams.** Calling is supported via the Graph Calls API and manifest flags; this is orthogonal to SDK choice.

6. **SSO / OBO.** The Teams SDK `App` class ships first-class OAuth/SSO support. The bot receives a per-user Entra token it can exchange via the OBO flow to call inventory APIs on the user's behalf.

7. **Toolkit automates packaging.** The M365 Agents Toolkit handles Azure Bot resource provisioning, SSO Entra app registration, manifest generation, and deployment.

### Trade-offs That Could Overturn This Recommendation

| Trade-off | Counter-scenario |
|---|---|
| **Calling complexity.** Voice bots require Graph app permissions, a dedicated HTTPS calling webhook, and admin consent. | If voice is a Phase 2 feature and Teams text chat is sufficient for Phase 1, you can defer calling setup and add it later without architectural changes. |
| **Azure Bot Service still required.** Even with Teams SDK / M365 Agents SDK, you must provision an Azure Bot resource. | Not a reason to avoid the recommendation; it is unavoidable for any custom-backend Teams bot path. |
| **No SSO in channel (team) scope.** Users posting in a Teams channel cannot be silently authenticated via SSO. | If every Teams interaction happens in personal or group chat (not channel), this is not an issue. For channel-scoped mutations, use the user's AAD Object ID from the activity and require explicit sign-in when the OBO token is first needed. |
| **Declarative agent simplicity.** If the inventory database were replaced by SharePoint/Graph data and voice were dropped, a declarative agent + API plugin would be dramatically simpler. | This is not the current spec; if requirements change to remove custom storage and voice, revisit. |
| **M365 Copilot license.** If all target users already have M365 Copilot add-on licenses and the org is standardised on Copilot, a declarative + custom engine agent hybrid via Copilot Studio could be considered. | Current spec does not assume Copilot licensing. |

---

## Implementation Checklist (Teams Channel Only)

1. **Create Azure Bot resource** (Azure Portal → Azure AI Bot Service → "Azure Bot" resource type). Record the Microsoft App ID.
2. **Register a Microsoft Entra app** for your bot (or let Agents Toolkit do this). Configure OAuth connection settings in the Azure Bot resource for SSO.
3. **Implement the agent** using Teams SDK (`@microsoft/teams.apps` / `Microsoft.Teams.Apps` NuGet). Wire up message handlers and OAuth.
4. **For calling/voice:** Enable the Calling tab in the Azure Bot → Teams channel settings. Add `bots[0].supportsCalling: true` to the manifest. Request `Calls.*` Graph app permissions and arrange admin consent.
5. **Build the app package:** `manifest.json` + icons. Use Agents Toolkit (`teams project new`) to scaffold.
6. **Distribute:** Upload the app package to the Teams Admin Center org app catalog. A Teams admin approves it. Alternatively sideload for testing.
7. **For proactive messages:** On first install, capture and persist the `conversationId`/`tenantId` from the `onMembersAdded` activity.

---

## Sources

All sources are official Microsoft Learn documentation fetched on 2026-08-28:

| Topic | URL | Last updated (as seen) |
|---|---|---|
| Bot Framework SDK archived notice | https://learn.microsoft.com/en-us/azure/bot-service/bot-service-overview?view=azure-bot-service-4.0 | 2026-01-08 |
| BF SDK → M365 Agents SDK migration | https://learn.microsoft.com/en-us/microsoft-365/agents-sdk/bf-migration-guidance | 2026-04-29 |
| M365 Agents SDK overview | https://learn.microsoft.com/en-us/microsoft-365/agents-sdk/agents-sdk-overview | 2026-08-19 |
| Teams SDK welcome | https://learn.microsoft.com/en-us/microsoftteams/platform/teams-sdk/welcome | 2026-07-27 |
| Teams SDK quickstart | https://learn.microsoft.com/en-us/microsoftteams/platform/teams-sdk/getting-started/quickstart | 2026-07-27 |
| M365 Agents Toolkit overview | https://learn.microsoft.com/en-us/microsoftteams/platform/toolkit/overview-agents-toolkit | 2026-01-29 |
| Teams bots overview (archived) | https://learn.microsoft.com/en-us/previous-versions/microsoftteams/platform/bots/overview | 2026-07-27 |
| Bot SSO overview | https://learn.microsoft.com/en-us/microsoftteams/platform/bots/how-to/authentication/bot-sso-overview | 2026-08-18 |
| Bot SSO code | https://learn.microsoft.com/en-us/microsoftteams/platform/bots/how-to/authentication/bot-sso-code | 2026-08-03 |
| Proactive messages | https://learn.microsoft.com/en-us/microsoftteams/platform/bots/how-to/conversations/send-proactive-messages | 2026-08-20 |
| Registering calling bot | https://learn.microsoft.com/en-us/microsoftteams/platform/bots/calls-and-meetings/registering-calling-bot | 2026-08-03 |
| Connect bot to Teams channel | https://learn.microsoft.com/en-us/azure/bot-service/channel-connect-teams?view=azure-bot-service-4.0 | 2025-12-16 |
| Apps for Teams meetings | https://learn.microsoft.com/en-us/microsoftteams/platform/apps-in-teams-meetings/teams-apps-in-meetings | 2026-07-20 |
| App package | https://learn.microsoft.com/en-us/microsoftteams/platform/concepts/build-and-test/apps-package | 2026-06-30 |
| Publish overview | https://learn.microsoft.com/en-us/microsoftteams/platform/concepts/deploy-and-publish/apps-publish-overview | 2026-07-22 |
| Declarative agents overview | https://learn.microsoft.com/en-us/microsoft-365/copilot/extensibility/overview-declarative-agent | 2026-07-02 |
| Agents for M365 Copilot (CEA vs DA) | https://learn.microsoft.com/en-us/microsoft-365/copilot/extensibility/agents-overview | 2026-08-11 |
| Licensing and cost considerations | https://learn.microsoft.com/en-us/microsoft-365/copilot/extensibility/cost-considerations | 2026-07-02 |
