# Azure Email Channel Research

**Issue:** [#4 — Survey Azure options for an inbound and outbound email channel](https://github.com/JoranBergfeld/multi-channel-agent/issues/4)  
**Researched:** 2026-08-28  
**Author:** GitHub Copilot (research subagent)

---

## Executive Summary

A conversational agent hosted on Azure needs to **receive** email (inbound) and **reply in-thread** (outbound). Four options are evaluated:

| Option | Inbound? | Outbound? | M365 Required? |
|--------|----------|-----------|----------------|
| Microsoft Graph + Change-Notification Webhooks | ✅ Push | ✅ | Yes |
| Azure Communication Services (ACS) Email | ❌ **None** | ✅ | No |
| Exchange Online Transport Rules / Connectors | ✅ Forward/redirect | Via Exchange | Yes |
| Logic Apps / Power Automate as a bridge | ✅ Poll | ✅ | Yes |

**Bottom line:** Only the Graph webhook approach delivers a fully push-based, in-thread, bidirectional email channel natively within the Microsoft/Azure ecosystem. ACS Email is send-only and cannot be used to receive mail — that is its defining limitation.

---

## Option 1: Microsoft Graph Change Notifications on an Exchange Online Mailbox

### How it works

The application subscribes to `POST /subscriptions` on the Graph API, targeting a mailbox resource path such as:

```
/users/{mailbox-id}/mailFolders('inbox')/messages
```

With `changeType: created`. Microsoft Graph then POSTs a notification payload to the application's HTTPS webhook endpoint whenever a new message is delivered. The app calls back to Graph to read the message, handle attachments, and reply.

Delivery channels available: **webhooks**, **Azure Event Hubs**, or **Azure Event Grid**.  
Source: [Change notifications overview](https://learn.microsoft.com/en-us/graph/change-notifications-overview)

### Inbound Support

**Full support.** The Graph message subscription raises a `created` notification for every new message delivered to the subscribed folder. The `message` resource exposes the full RFC-5322 envelope: `from`, `toRecipients`, `ccRecipients`, `subject`, `body` (HTML or text), `receivedDateTime`, `hasAttachments`, `conversationId`, `conversationIndex`, `internetMessageId`, `internetMessageHeaders`, and `inReplyTo`.  
Source: [message resource type (v1.0)](https://learn.microsoft.com/en-us/graph/api/resources/message?view=graph-rest-1.0)

### Authentication Model

- **App registration** in Microsoft Entra ID.
- **Application permission** `Mail.Read` (minimum for reading; `Mail.ReadWrite` and `Mail.Send` additionally needed for composing and sending replies). All mail application permissions require **admin consent**.
- **Least privilege:** `Mail.ReadBasic.All` is available for envelope-only reads without body; `Mail.Read` is required if body/attachment content is needed.
- The subscription endpoint itself must also be validated by Graph (challenge-response validation on creation).
- Optionally, rich notifications (payload includes resource data in-line) require an **encryption certificate** registered with the subscription to protect message content in transit.

Source: [Create subscription – Permissions table](https://learn.microsoft.com/en-us/graph/api/subscription-post-subscriptions?view=graph-rest-1.0); [Microsoft Graph permissions reference](https://learn.microsoft.com/en-us/graph/permissions-reference)

### Threading and Conversation-ID Handling

The `message` resource carries two threading fields:
- **`conversationId`** — a string identifier shared across all messages in the same Outlook conversation (thread). Use this to correlate replies to the originating user message.
- **`conversationIndex`** — binary; encodes the message's position in the thread tree, compatible with Outlook's threading model.
- **`internetMessageId`** — the RFC-2822 `Message-ID` header. Use this as the `In-Reply-To` and `References` value when composing replies so that external mail clients display the messages as a thread.

To reply in-thread programmatically: call [`POST /messages/{id}/createReply`](https://learn.microsoft.com/en-us/graph/api/message-createreply) to create a draft, then [`POST /messages/{draft-id}/send`](https://learn.microsoft.com/en-us/graph/api/message-send). Graph populates `In-Reply-To` and `References` headers automatically.

Source: [message resource type – Properties](https://learn.microsoft.com/en-us/graph/api/resources/message?view=graph-rest-1.0)

### Attachment and Audio-Note Handling

- `hasAttachments: true` on the message resource when at least one non-inline attachment is present.
- Fetch attachments via `GET /messages/{id}/attachments`; each `attachment` resource returns `name`, `contentType`, and `contentBytes` (base64).
- Audio/voice-note attachments (e.g., `.m4a`, `.mp3`, `.wav`) arrive as `fileAttachment` resources with their MIME type; no special handling needed beyond checking `contentType` starts with `audio/`.
- Maximum total email + attachment size for Exchange Online messages: 150 MB (Exchange Online limit). The Graph API itself does not add an additional size limit for reading.

Source: [message resource type – Attachments](https://learn.microsoft.com/en-us/graph/api/resources/message?view=graph-rest-1.0); [Outlook mail API overview](https://learn.microsoft.com/en-us/graph/outlook-mail-concept-overview)

### Latency

| Metric | Value |
|--------|-------|
| Average notification delivery | **< 1 minute** |
| Maximum notification delivery | **3 minutes** |

Source: [Subscription resource type – Latency table](https://learn.microsoft.com/en-us/graph/api/resources/subscription?view=graph-rest-1.0)

### Quotas and Cost

- **Subscription lifetime:** 10,080 minutes (7 days) for basic notifications; 1,440 minutes (1 day) for rich notifications (those with resource data). Subscriptions **must be renewed** programmatically before expiry.
- **Subscription quota:** Max **1,000 active subscriptions per mailbox** (across all applications).
- **Cost:** Microsoft Graph API is included at no additional charge in all Microsoft 365 SKUs. An Exchange Online mailbox is required (Plan 1 ~$4/user/month, or a shared mailbox covered by certain M365 plans at no extra seat cost).

Source: [Subscription resource type – Subscription lifetime table](https://learn.microsoft.com/en-us/graph/api/resources/subscription?view=graph-rest-1.0)

### Tenant Setup Required

1. Microsoft 365 / Azure tenant with Exchange Online.
2. A **dedicated mailbox** (user or shared) as the agent's email address.
3. An **Entra ID app registration** with `Mail.Read`, `Mail.ReadWrite`, `Mail.Send` **application** permissions.
4. **Admin consent** granted by a Global Administrator or Exchange Administrator.
5. A publicly reachable HTTPS webhook endpoint (e.g., Azure Functions, Azure API Management, or a Container App) for notification delivery.
6. (Optional) Azure Event Hubs or Azure Event Grid as an alternative notification delivery channel for higher scale or resilience.

---

## Option 2: Azure Communication Services (ACS) Email

### How it works

ACS Email is an Azure-native, application-to-person (A2P) email service. The developer provisions an `Email Communication Services` resource, verifies a custom domain (or uses the Azure-managed `*.azurecomm.net` domain), and calls the ACS Email SDK or REST API to send messages.

Source: [Azure Communication Services email overview](https://learn.microsoft.com/en-us/azure/communication-services/concepts/email/email-overview)

### Inbound Support

**None. ACS Email is strictly an outbound (send-only) service.** There is no receive capability, no inbox, no webhook for incoming mail, and no MX record that routes mail into ACS for processing. The product documentation does not describe any inbound path because the feature does not exist.

> "Azure Communication Services facilitates high-volume transactional, bulk, and marketing emails. It supports application-to-person (A2P) use cases."  
> Source: [ACS email overview](https://learn.microsoft.com/en-us/azure/communication-services/concepts/email/email-overview)

This is a **disqualifying limitation** for any scenario that requires receiving user replies.

### Authentication Model

- ACS resources use **connection strings** or **Azure Managed Identity** (via Azure Role-Based Access Control).
- No Entra ID app registration with mail permissions is required.
- No Exchange Online tenant required.
- Admin setup: Azure subscription admin provisions the `Email Communication Services` resource and verifies domains.

Source: [Prepare an ACS email resource](https://learn.microsoft.com/en-us/azure/communication-services/concepts/email/prepare-email-communication-resource)

### Threading and Conversation-ID Handling

Because ACS has no inbound path, threading must be implemented manually by the application:
- Set `In-Reply-To` and `References` RFC-5322 headers in the outbound email payload.
- Store and pass through the original `Message-ID` to form a proper thread chain visible to external clients.
- No built-in conversation-ID concept exists in the ACS Email SDK — the developer owns this state.

### Attachment and Audio-Note Handling

**Outbound only.** Attachments are included as base64-encoded content in the API request body. Size limits:
- Default max: **10 MB** total request (including attachments).
- Support request can raise this to **30 MB**.
- For files > 30 MB, the recommendation is Azure Blob Storage + SAS URL in the email body.

Source: [ACS service limits – Email](https://learn.microsoft.com/en-us/azure/communication-services/concepts/service-limits)

### Latency

Near real-time delivery status via the `EmailClient.GetSendStatusAsync()` polling or Event Grid events (`EmailDeliveryReportReceived`). No inbound latency relevant since inbound is unsupported.

### Quotas and Cost

**Custom domain:**

| Operation | Per Subscription / 1 min | Per Subscription / 60 min |
|-----------|--------------------------|---------------------------|
| Send Email | 30 emails | 100 emails |
| (Higher quotas available via support request) | | |

**Azure-managed domain:**

| Operation | Per Subscription / 1 min | Per Subscription / 60 min |
|-----------|--------------------------|---------------------------|
| Send Email | 5 emails | 10 emails |
| (Fixed; cannot be raised) | | |

Other limits: max **50 recipients** per email, max **10 MB** attachment per request.  
High throughput (up to 1–2 million messages/hour) available on custom domains after quota increase and reputation building.

Source: [ACS service limits](https://learn.microsoft.com/en-us/azure/communication-services/concepts/service-limits)

**Cost:** Pay-per-use. See [Azure Communication Services pricing](https://azure.microsoft.com/en-us/pricing/details/communication-services/) for current per-email rates.

### Tenant Setup Required

1. Azure subscription.
2. `Email Communication Services` resource (separate from the ACS resource).
3. Verify a custom domain or use the Azure-managed domain.
4. Link the email domain to the ACS resource.
5. No M365/Exchange Online tenant required.

Source: [Prepare an ACS email resource](https://learn.microsoft.com/en-us/azure/communication-services/concepts/email/prepare-email-communication-resource)

---

## Option 3: Exchange Online Transport Rules and Connectors

### How it works

Exchange Online **transport rules** (also called mail flow rules) inspect every message in transit — before delivery to any mailbox — and can take actions such as forwarding, redirecting, or BCC-ing the message to another address or to an external HTTPS endpoint (via a custom connector or an outbound partner connector pointing to an Azure service).

Exchange Online **connectors** define trusted, TLS-authenticated mail routes between Exchange Online and external mail servers or Azure services (e.g., Azure Logic Apps, Azure Functions, Service Bus). An inbound connector can inject mail from a custom source into Exchange Online routing; an outbound connector can route copies to an Azure-hosted HTTPS endpoint.

Source: [Configure mail flow using connectors in Exchange Online](https://learn.microsoft.com/en-us/exchange/mail-flow-best-practices/use-connectors-to-configure-mail-flow/use-connectors-to-configure-mail-flow); [Mail flow rules (transport rules) in Exchange Online](https://learn.microsoft.com/en-us/exchange/security-and-compliance/mail-flow-rules/mail-flow-rules)

### Inbound Support

**Conditional.** Transport rules fire on messages *in transit* (before or during delivery). They can:
- **Redirect** the message to an alternative address (including an Azure webhook address or a shared mailbox).
- **Forward a copy (BCC)** to an external endpoint without disrupting normal delivery.
- **Invoke a webhook** by routing to an Azure Logic Apps HTTP trigger (using a partner/outbound connector) or by forwarding to a mailbox monitored by Graph.

This is an *infrastructure-level* fan-out approach rather than a direct inbound-email-to-agent path. It requires a mailbox or SMTP endpoint to ultimately receive the redirected mail, then a separate mechanism (Graph webhooks) to read it.

### Authentication Model

- Exchange Online admin configures rules in the **Exchange Admin Center (EAC)** or via Exchange Online PowerShell.
- No Entra ID app registration needed for the rules themselves.
- Connectors to external services use **TLS with certificate pinning** or **IP address restriction** to authenticate the external endpoint.
- No programmatic API for inbound routing without also using Graph or another receive mechanism.

### Threading and Conversation-ID Handling

All original RFC-5322 headers (`Message-ID`, `In-Reply-To`, `References`, `Thread-Topic`, `Thread-Index`) are preserved in forwarded/redirected mail. Exchange Online adds its own `X-MS-Exchange-Organization-*` headers that can be leveraged for correlation. Transport rules can also add **custom X-headers** using the "Set message header" action to inject tracking metadata.

### Attachment and Audio-Note Handling

Attachments are preserved in full when messages are redirected or forwarded by transport rules. Audio attachments pass through unchanged. Rules can inspect attachment type/size as conditions (e.g., block messages with attachments over a certain size), but the attachments themselves are not processed by the rule — they travel with the message.

### Latency

Transport rule processing typically adds **seconds** to mail flow. Total end-to-end latency from external SMTP delivery to the point where a connected Azure service receives the forwarded message is usually **< 2 minutes**, dependent on Exchange Online queue depth and connector health.

### Quotas and Cost

- **No additional cost** for transport rules beyond the Exchange Online license.
- Exchange Online receiving rate: default throttle of **30 received messages/minute** from the internet per domain (Exchange Online Protection). Higher throughput requires Microsoft support.
- Rules are **unlimited** in the number of conditions/exceptions, but there is a practical limit of **300 transport rules per organization**.
- Connector throughput mirrors Exchange Online mail flow limits.

Source: [Mail flow rules in Exchange Online](https://learn.microsoft.com/en-us/exchange/security-and-compliance/mail-flow-rules/mail-flow-rules)

### Tenant Setup Required

1. Microsoft 365 / Exchange Online tenant.
2. Exchange Administrator access to create transport rules and connectors.
3. A verified custom domain with MX records pointing to Exchange Online.
4. An Azure-hosted endpoint (Function, Logic App HTTP trigger, Service Bus) to receive forwarded messages.
5. (Optionally) A receiving mailbox monitored by Graph webhooks for reading the forwarded copy.

---

## Option 4: Logic Apps (or Power Automate) as a Bridge

### How it works

Azure Logic Apps with the **Office 365 Outlook connector** exposes a "When a new email arrives (V3)" trigger. The trigger internally **polls** the Exchange Online mailbox on a configurable interval and fires a workflow run when new messages are detected. The workflow can then call an Azure service (Function, Service Bus, HTTP action to the agent), and the Logic App can reply via the "Reply to email (V3)" action.

The connector is available in **Logic Apps (Consumption and Standard)**, **Power Automate**, and **Copilot Studio**.

Source: [Office 365 Outlook connector reference](https://learn.microsoft.com/en-us/connectors/office365/); [Azure Logic Apps overview](https://learn.microsoft.com/en-us/azure/logic-apps/logic-apps-overview)

### Inbound Support

**Yes, via polling.** The trigger polls the inbox at a configured interval (minimum **1 minute** in Logic Apps Consumption; can be set to **15 seconds** in Logic Apps Standard/ISE). This is **not push-based** — it introduces poll-cycle latency. There is no guarantee that every message triggers a run at sub-minute latency.

The connector also supports a "When a new email arrives in a shared mailbox (V2)" trigger, enabling a service account–free shared-mailbox approach.

### Authentication Model

- The Office 365 Outlook connector uses **OAuth 2.0 delegated permissions** by default (user identity, `Mail.Read`, `Mail.Send`).
- Alternatively, Logic Apps Standard supports **application-based connections** using service principal authentication, requiring `Mail.ReadWrite` application permissions with admin consent.
- The connection is created via the Azure portal or the Logic Apps designer and stored as an **API connection resource**.
- **Known limitation:** Action cards / approval emails are only supported for single-user mailboxes (not shared mailboxes or groups).

Source: [Office 365 Outlook connector – Known issues and limitations](https://learn.microsoft.com/en-us/connectors/office365/)

### Threading and Conversation-ID Handling

The email trigger output includes `ConversationId`, `MessageId`, `ReplyTo`, and the full email body and subject. The "Reply to email (V3)" action constructs a correctly threaded reply (sets `In-Reply-To` and `References` headers automatically). Logic Apps expressions can extract `ConversationId` to correlate multi-turn conversations.

**Known limitation:** The "Reply to email (V3)" action converts the original `Sent` datetime to UTC due to an underlying system limit; this is cosmetic only.

Source: [Office 365 Outlook connector – Known issues](https://learn.microsoft.com/en-us/connectors/office365/)

### Attachment and Audio-Note Handling

The trigger output indicates whether attachments are present (`Has Attachments: true`). Attachments are fetched via the "Get attachment (V2)" action, which returns attachment content (base64). Audio attachments are indistinguishable from other file attachments and can be passed to downstream actions (e.g., Speech-to-text via Azure AI Speech, or stored in Blob Storage).

**Known limitation:** Digitally signed emails may return incorrect attachment content from the connector.

Source: [Office 365 Outlook connector – Known issues](https://learn.microsoft.com/en-us/connectors/office365/)

### Latency

| Mode | Minimum Poll Interval |
|------|-----------------------|
| Logic Apps Consumption | 1 minute |
| Logic Apps Standard (ISE) | 15 seconds |
| Power Automate | 1 minute |

This polling model adds inherent latency vs. Graph push webhooks.

### Quotas and Cost

**Logic Apps Consumption:**
- First **4,000 action executions/month** free per Azure subscription.
- Above free tier: ~$0.000025/action execution.
- Office 365 Outlook connector is a **Standard connector**: included in Consumption at no extra connector charge, but each call is an action execution.
- Polling trigger fires on every interval even if no new email (counts as an action execution).

**Logic Apps Standard:**
- Priced by **vCPU-seconds + GB-seconds** (hosting cost), not per action.
- More predictable cost for steady-state workloads.

**Power Automate:**
- Requires **Power Automate Premium** or **per-flow** license for complex flows or premium connectors; the Office 365 Outlook connector itself is a Standard connector accessible with M365 licenses.

Source: [Azure Logic Apps pricing](https://learn.microsoft.com/en-us/azure/logic-apps/logic-apps-pricing)

### Tenant Setup Required

1. Azure subscription for Logic Apps resource.
2. Microsoft 365 / Exchange Online tenant with a mailbox for the agent.
3. An **API connection** resource in Azure connecting Logic Apps to the mailbox (either via a user account with `Mail.Read`/`Mail.Send` delegated consent, or a service principal with application permissions).
4. (Optional) Power Automate Premium license if using Power Automate instead of Logic Apps.

---

## Dimension-by-Dimension Comparison

| Dimension | Graph Webhooks (Option 1) | ACS Email (Option 2) | EXO Transport Rules (Option 3) | Logic Apps Bridge (Option 4) |
|---|---|---|---|---|
| **Inbound** | ✅ Push, near real-time | ❌ Not supported | ✅ Forward/redirect on transit | ✅ Polling (1 min min) |
| **Outbound** | ✅ via `message-send` | ✅ Native | ✅ via Exchange | ✅ via "Reply to email" action |
| **Auth model** | App registration, `Mail.Read`/`Mail.Send` app permissions, admin consent | ACS connection string / managed identity | Exchange admin EAC/PS, connector cert/IP | OAuth delegated or app permission, admin consent |
| **Threading** | `conversationId` + `createReply` API | Manual via RFC-5322 headers (outbound only) | RFC-5322 headers preserved in forwarded mail | `ConversationId` in trigger; "Reply to email" action |
| **Attachments** | Full via `attachments` resource | Outbound only (10 MB default, 30 MB on request) | Preserved in forwarded mail | Via "Get attachment (V2)" action; audio accessible |
| **Voice notes** | ✅ as `fileAttachment` | N/A (outbound only) | ✅ preserved in transit | ✅ fetchable; pass to Azure AI Speech |
| **Latency** | < 1 min avg, 3 min max | Near real-time send only | < 2 min (transit + connector) | 1 min poll minimum |
| **Sub lifetime** | 7 days (basic), 1 day (rich); must renew | N/A | Permanent (rules persist) | N/A (trigger always-on) |
| **Cost** | Included in M365; AzFunc/Event Hubs extra | Pay per email | Included in M365 | $0.000025/action (Consumption) or hosting (Standard) |
| **M365 required** | Yes (Exchange Online mailbox) | No | Yes (Exchange Online) | Yes (Exchange Online mailbox) |
| **Admin consent** | Yes (Global/Exchange Admin) | No | Yes (Exchange Admin) | Yes (delegated or app) |
| **Complexity** | Medium (subscription renewal, certificate for rich notifications) | Low (send-only) | Medium-High (connector + downstream receiver) | Low (designer-based) |

---

## Recommendation

### Primary: Microsoft Graph Change-Notification Webhooks on a Dedicated Exchange Online Mailbox

**Use Graph webhooks (Option 1) as the inbound channel, with Graph `message-send` for outbound replies.**

Rationale:
1. **The only true push-based inbound path** in the Microsoft ecosystem — no polling latency, no intermediate hop.
2. **Native threading:** `conversationId` + `createReply` API maintains proper RFC-5322 thread headers automatically; external clients see a coherent conversation.
3. **Full attachment access:** voice notes and other attachments are retrievable via a single API call; content can be passed directly to Azure AI Speech or Blob Storage.
4. **All-Azure control plane:** Entra ID app registration → Azure Functions/Container Apps webhook endpoint → Graph API. No additional Microsoft 365 product dependencies beyond the Exchange Online mailbox.
5. **Cost-effective:** no per-message charge for reading/writing email; only the compute for the webhook receiver.

**What is required to make this work:**
- Microsoft 365 tenant (Exchange Online Plan 1 or higher, or a shared mailbox on an E1/E3/E5 plan).
- Entra ID app registration with `Mail.Read`, `Mail.ReadWrite`, `Mail.Send` **application** permissions (admin consent required).
- A subscription renewal process (must renew within 7 days for basic subscriptions; cron job or Durable Functions orchestration recommended).
- A publicly accessible HTTPS webhook receiver (Azure Functions or Azure Container Apps).

### Complementary: ACS Email for High-Volume Outbound

Use **ACS Email (Option 2)** in addition to — not instead of — Graph webhooks, if the agent needs to send proactive notifications or marketing-style outbound emails at high volume (e.g., order confirmations). ACS Email offers higher throughput (up to 1–2 M messages/hour after quota increase) and does not consume Exchange Online per-mailbox throttle.

For transactional agent replies (replying to user email threads), **use Graph `message-send` rather than ACS** so that the `In-Reply-To`/`References` headers and `conversationId` are maintained automatically.

### Fallback: Logic Apps Bridge (Option 4) for Low-Code Teams

If the development team wants to avoid managing webhook subscription renewal or infrastructure, **Logic Apps Standard** with the Office 365 Outlook connector is a viable fallback with minimal code. The polling latency (minimum 1 minute) is acceptable for most customer-service email scenarios. The "Reply to email (V3)" action handles threading automatically.

---

## Trade-offs That Would Overturn the Recommendation

| Condition | Implication |
|-----------|-------------|
| **No Microsoft 365 / Exchange Online tenant** | Graph webhooks and Logic Apps are unavailable. ACS Email can send but cannot receive. The only available Microsoft-native inbound path would be a hybrid architecture (e.g., ACS + a third-party IMAP mailbox provider), which is outside the pure Microsoft stack. |
| **Strict no-polling, no-M365 requirement** | No suitable Microsoft-native option exists for inbound email; would require re-evaluating the constraint. |
| **< 1 minute latency is unacceptable** | Graph webhooks already average < 1 minute; if strict sub-second SLA is required, email is not the right channel and real-time channels (Teams, ACS Chat) should be used instead. |
| **Low-code / no-code preference with polling latency acceptable** | Logic Apps Standard (Option 4) is easier to maintain and deploy without a dedicated webhook infrastructure. Switch to Option 4. |
| **Very high inbound volume (> 1,000 subs/mailbox)** | The 1,000-subscription limit per mailbox is rarely hit for a single agent mailbox; if it is, use multiple mailboxes with routing rules or migrate to Azure Event Hubs as the notification delivery channel (supported by Graph webhooks). |
| **Multi-tenant / white-label SaaS** | App-level (application permission) subscriptions targeting `users/{id}` require per-tenant admin consent. Use delegated permissions + on-behalf-of flow if consent cannot be obtained per tenant, but this requires a signed-in user. |

---

## Sources

| Source | URL | Date |
|--------|-----|------|
| Microsoft Graph Change Notifications Overview | https://learn.microsoft.com/en-us/graph/change-notifications-overview | Updated 2026-04-07 |
| Subscription resource type (subscription lifetime, latency tables) | https://learn.microsoft.com/en-us/graph/api/resources/subscription?view=graph-rest-1.0 | Updated 2026-04-07 |
| Create subscription – Permissions | https://learn.microsoft.com/en-us/graph/api/subscription-post-subscriptions?view=graph-rest-1.0 | Updated 2026-04-07 |
| message resource type (v1.0) | https://learn.microsoft.com/en-us/graph/api/resources/message?view=graph-rest-1.0 | Updated 2026-04-20 |
| Rich (resource data) notifications – Supported resources | https://learn.microsoft.com/en-us/graph/change-notifications-with-resource-data | Updated 2026-04-17 |
| Microsoft Graph permissions reference | https://learn.microsoft.com/en-us/graph/permissions-reference | Updated 2026-08-25 |
| Outlook mail API overview | https://learn.microsoft.com/en-us/graph/outlook-mail-concept-overview | Updated 2026-05-11 |
| ACS email overview | https://learn.microsoft.com/en-us/azure/communication-services/concepts/email/email-overview | Updated 2026-03-25 |
| Prepare an ACS email communication resource | https://learn.microsoft.com/en-us/azure/communication-services/concepts/email/prepare-email-communication-resource | Updated 2026-03-25 |
| ACS SMTP support overview | https://learn.microsoft.com/en-us/azure/communication-services/concepts/email/email-smtp-overview | Updated 2026-03-25 |
| ACS service limits (email quotas) | https://learn.microsoft.com/en-us/azure/communication-services/concepts/service-limits | Updated 2026-03-05 |
| Exchange Online connectors overview | https://learn.microsoft.com/en-us/exchange/mail-flow-best-practices/use-connectors-to-configure-mail-flow/use-connectors-to-configure-mail-flow | Updated 2026-08-03 |
| Mail flow rules (transport rules) in Exchange Online | https://learn.microsoft.com/en-us/exchange/security-and-compliance/mail-flow-rules/mail-flow-rules | Updated 2026-08-03 |
| Office 365 Outlook connector reference | https://learn.microsoft.com/en-us/connectors/office365/ | Updated 2026-07-11 |
| Azure Logic Apps pricing | https://learn.microsoft.com/en-us/azure/logic-apps/logic-apps-pricing | Updated 2026-07-10 |
| Azure Logic Apps overview | https://learn.microsoft.com/en-us/azure/logic-apps/logic-apps-overview | Updated 2026-06-11 |
