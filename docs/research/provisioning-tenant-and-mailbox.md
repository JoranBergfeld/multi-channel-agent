# Provisioning: Azure Subscription, Microsoft 365 Tenant, and Agent Mailbox

**Research date:** 2026-09-02  
**Issue:** #19 — "Provision the Azure subscription, Microsoft 365 tenant and agent mailbox"  
**Context:** Multi-channel inventory agent on Azure — confirms what must be in place before any channel or AI work begins: a development M365 tenant where Joran is Global Administrator with Teams and Exchange Online licensed, Teams custom-app sideloading enabled, a shared `inventory-agent` mailbox, and the existing Azure Sandbox subscription verified.

---

## Executive Summary

Everything in this issue is purely procedural provisioning — no architectural decisions remain open. The official routes are: (a) the **Microsoft 365 Developer Program instant sandbox** (M365 E5, 90-day auto-renewing, free) as the canonical dev-tenant path; (b) a four-step setup in the Teams admin center to enable custom-app sideloading; (c) a two-step wizard in the Microsoft 365 admin center to create the shared mailbox `inventory-agent`; and (d) two Azure CLI one-liners to verify the Sandbox subscription's role assignments and registered resource providers. Regional and quota limits for Azure OpenAI are now subscription-scoped and tier-based since May 2026. All steps below are sourced exclusively from official Microsoft/Azure primary documentation.

---

## 1. Establishing a Microsoft 365 Development Tenant

### 1.1 Official route — M365 Developer Program instant sandbox

The **Microsoft 365 Developer Program** provides a free M365 E5 developer subscription (25 user licences, Exchange Online Plan 2, Teams) that auto-renews every 90 days while the account shows development activity.

**Eligibility** (at least one must apply):

| Category | Qualification |
|----------|--------------|
| Visual Studio subscribers | Visual Studio Professional or Enterprise subscription |
| ISV Success / MAICPP partners | Azure Expert MSP, Solutions Partner, Action Pack, Gold/Silver legacy, etc. |
| Premier / Unified Support customers | Contact CSAM or PDM to request the subscription |

> **Caveat:** Government-cloud tenants are not eligible.  
> Source: [Welcome to the Microsoft 365 Developer Program](https://learn.microsoft.com/en-us/office/developer-program/microsoft-365-developer-program)

**Sign-up journey (September 2026 UI):**

1. Go to <https://developer.microsoft.com/microsoft-365/dev-program> and click **Join now**.
2. Sign in with a Microsoft or Entra-enabled email — **not** a `*.onmicrosoft.com` address.
3. Fill in contact email, country/region, and company; accept terms; click **Join**.
4. On the developer program dashboard, click **Set up E5 subscription** and choose **Instant sandbox (Add-on purchases enabled)** (the default).
5. Select data-centre **Country/region** — this cannot be changed after sign-up; choose the region closest to the production deployment target (e.g. West Europe).
6. Provide or create a billing account (required for verification; no charge for the E5 subscription itself).
7. Set **Admin username** and **Admin password**; record these — this is your tenant's Global Administrator credential.
8. Click **Set up**. Provisioning completes in a few minutes.

> **What you get:** Domain is pre-configured as `<yourtenant>.onmicrosoft.com` and cannot be customised. The instant sandbox ships with 16 sample users, Teams sample data packs, and Graph/SharePoint sample data.  
> Source: [Set up a Microsoft 365 developer sandbox subscription](https://learn.microsoft.com/en-us/office/developer-program/microsoft-365-developer-program-get-started)

**Custom-app upload is already enabled by default** in an instant sandbox provisioned with the Teams sample data pack. The sideloading steps in Section 3 below are still required when starting from any non-sandbox tenant.

---

## 2. Verifying Global Administrator Role and Licences

### 2.1 Confirm Global Administrator status

1. Sign in to <https://admin.microsoft.com> with the admin credentials recorded in step 7 above.  
   — If you can enter the admin centre, you hold at minimum an admin role.
2. Navigate to **Users → Active users** (or go directly to <https://go.microsoft.com/fwlink/p/?linkid=834822>).
3. Select your user account. In the detail pane, the **Roles** section lists all assigned roles. Confirm **Global Administrator** appears.

> The account created during sandbox setup is automatically assigned Global Administrator.  
> Source: [About administrator roles in the Microsoft 365 admin center](https://learn.microsoft.com/en-us/microsoft-365/admin/add-users/about-admin-roles)

### 2.2 Confirm Exchange Online and Teams licences

1. In the admin centre, go to **Billing → Licenses**.
2. Confirm that **Microsoft 365 E5 Developer** (or equivalent) is listed and that at least one unit is assigned to the admin account.
3. The M365 E5 Developer licence bundles both **Exchange Online Plan 2** and **Microsoft Teams**; no separate licence purchase is needed for the dev sandbox.

> Shared mailboxes up to 50 GB require **no additional licence**; the delegate accounts accessing the mailbox each need a licensed Exchange Online mailbox (included in E5).  
> Source: [About shared mailboxes](https://learn.microsoft.com/en-us/microsoft-365/admin/email/about-shared-mailboxes)

---

## 3. Enabling Teams Custom-App Upload (Sideloading)

> **Skip if using an instant sandbox** — the "Upload custom apps" toggle is pre-enabled. Confirm by attempting to upload a `.zip` package; if it succeeds, this section is already done.

Three settings must be turned on; all require **Teams Administrator** (or Global Administrator) access to the Teams admin centre at <https://admin.teams.microsoft.com>.

### 3.1 Enable upload in the Global app setup policy

1. Sign in to the Teams admin centre.
2. Navigate to **Teams apps → Setup Policies → Global (Org-wide default)**.
3. Toggle **Upload custom apps** to **On**.
4. Click **Save**.

> It can take up to 24 hours for the setting to propagate. In the interim, use the **Upload for \<tenant\>** option in Teams to test.  
> Source: [Prepare your Microsoft 365 tenant — Teams](https://learn.microsoft.com/en-us/microsoftteams/platform/concepts/build-and-test/prepare-your-o365-tenant)

### 3.2 Enable org-wide custom-app settings

1. In the Teams admin centre, go to **Teams apps → Manage apps**.
2. Click **Actions → Org-wide app settings**.
3. Under **Custom apps**, turn on both toggles:
   - **Let users install and use available apps by default**
   - **Let users interact with custom apps in preview**
4. Click **Save**.

> Source: [Manage custom app policies and settings in Teams](https://learn.microsoft.com/en-us/microsoftteams/teams-custom-app-policies-and-settings)

### 3.3 Effect of the two settings together

| Setup-policy Upload toggle | Org-wide "interact with preview" toggle | Who can upload for personal/team use |
|---|---|---|
| On | On | Any user assigned the policy (default = all users with Global policy) |
| On | Off | No user (upload toggle is overridden) |
| Off | On | No user |

The Global policy covers all users unless a custom policy is assigned. For a developer-only tenant this is fine.

---

## 4. Creating the `inventory-agent` Shared Mailbox

A shared mailbox lets the agent send and receive email as `inventory-agent@<tenant>.onmicrosoft.com` without a paid user licence and without an interactive sign-in account.

### 4.1 Create the mailbox

Requires **Exchange Administrator** (or Global Administrator) role.

1. Sign in to <https://admin.microsoft.com>.
2. Navigate to **Teams & Groups → Shared mailboxes** (select **Show all** in the left pane if the section is hidden), or go directly to <https://go.microsoft.com/fwlink/p/?linkid=2066847>.
3. Click **+ Add a shared mailbox**.
4. Enter display name **inventory-agent**. The email address field auto-populates as `inventory-agent@<tenant>.onmicrosoft.com`; edit if needed.
5. Click **Save changes**. Wait a few minutes before adding members.
6. Under **Next steps**, click **Add members to this mailbox**.
7. Members added here are **human delegates** who can open the mailbox in Outlook. Do not add any app or service account here.

> Source: [Create a shared mailbox](https://learn.microsoft.com/en-us/microsoft-365/admin/email/create-a-shared-mailbox)

### 4.2 Exchange delegation vs. Graph application permissions — important distinction

**Exchange mailbox delegation** (Full Access, Send As, Send on Behalf — configured via the Exchange admin centre) applies only to **user/delegate** scenarios: a human account accessing another mailbox through Outlook or Exchange. It is not how an application reads or sends mail via Microsoft Graph.

**Graph API access** by the agent (reading inbox events, sending replies) requires an **Entra ID app registration** with tenant-wide application permissions (`Mail.Read`, `Mail.ReadWrite`, `Mail.Send`) and **admin consent**. No Exchange delegation entry is needed on the shared mailbox.

> Graph application permission `Mail.ReadWrite` description: *"Allows the app to create, read, update, and delete email in all mailboxes without a signed-in user."*  
> Source: [Microsoft Graph permissions reference — Mail permissions](https://learn.microsoft.com/en-us/graph/permissions-reference#mail-permissions)

**This provisioning step is deferred.** The Entra app registration, permission assignment, and admin-consent grant are implementation details that belong in the channel specification ticket, not here. Nothing in the Exchange admin centre needs to be changed to provision the mailbox itself.

### 4.3 Licensing limits and caveats

| Scenario | Licence required |
|---|---|
| Shared mailbox ≤ 50 GB | **None** — no licence needed |
| Shared mailbox > 50 GB (up to 100 GB) | **Exchange Online Plan 2** assigned to the mailbox |
| In-place archiving | Exchange Online Plan 2 or Plan 1 + Exchange Online Archiving |
| Litigation hold | Exchange Online Plan 2 |

Keep sign-in **blocked** on the shared mailbox account (default, and unchanged throughout the project). Do not unblock it.

> Source: [About shared mailboxes — Licensing and mailbox storage limits](https://learn.microsoft.com/en-us/microsoft-365/admin/email/about-shared-mailboxes)

---

## 5. Obtaining the Tenant ID

The tenant ID is a GUID needed for Entra app registrations and Graph API calls.

**Via Microsoft Entra admin centre (portal UI):**
1. Sign in to <https://entra.microsoft.com> as at least Global Reader.
2. Navigate to **Entra ID → Overview → Properties**.
3. Scroll to **Tenant ID** — copy the GUID.

**Via Azure portal:**
1. Navigate to **Microsoft Entra ID → Properties → Tenant ID**.

**Via Azure CLI:**
```bash
az login
az account list --query "[].{Name:name, TenantId:tenantId}" --output table
# or specifically:
az account tenant list
```

> Source: [How to find your tenant ID — Microsoft Entra](https://learn.microsoft.com/en-us/entra/fundamentals/how-to-find-tenant)

---

## 6. Azure Subscription Checks (Azure Sandbox)

The decision is to use the already-authenticated **Azure Sandbox** subscription. The following CLI commands verify role access, resource-provider registration, and quota posture without modifying anything.

### 6.1 Verify subscription and role assignment

```bash
# Show current subscription context
az account show --query "{Name:name, Id:id, TenantId:tenantId, State:state}"

# List your own role assignments on the subscription (Owner/Contributor needed for most deploys)
az role assignment list \
  --scope "/subscriptions/$(az account show --query id -o tsv)" \
  --assignee "$(az account show --query user.name -o tsv)" \
  --output json \
  --query '[].{Role:roleDefinitionName, Scope:scope}'
```

> Source: [List Azure role assignments using Azure CLI](https://learn.microsoft.com/en-us/azure/role-based-access-control/role-assignments-list-cli)

### 6.2 Check registration status of required resource providers

The providers below are needed for the planned AI stack; register any that are not already `Registered`:

```bash
az provider list \
  --query "[?namespace=='Microsoft.CognitiveServices' || namespace=='Microsoft.BotService' || namespace=='Microsoft.MachineLearningServices' || namespace=='Microsoft.Web' || namespace=='Microsoft.ContainerRegistry' || namespace=='Microsoft.App'].{Provider:namespace, Status:registrationState}" \
  --output table
```

To register a missing provider (requires `Contributor` or `Owner`):
```bash
az provider register --namespace Microsoft.CognitiveServices
```

> Registering a provider adds a first-party app to your Entra tenant. Register only providers you intend to use.  
> Source: [Azure resource providers and types](https://learn.microsoft.com/en-us/azure/azure-resource-manager/management/resource-providers-and-types)

### 6.3 View quota posture

**Portal (recommended for a visual overview):**
1. Sign in to the Azure portal.
2. Search for **Quotas** and select it.
3. Click **My quotas**; filter by **Provider** (e.g., `Microsoft.CognitiveServices`) and **Location**.

**CLI (for scripting or CI):**
```bash
# List Azure OpenAI quota for a region (example: westeurope)
az cognitiveservices account list-usage \
  --location westeurope \
  --query "[].{Name:name.localizedValue, Current:currentValue, Limit:limit}"
```

> Source: [View quotas in the Azure portal](https://learn.microsoft.com/en-us/azure/quotas/view-quotas)

### 6.4 Azure OpenAI quota model (post-May 2026)

Since **7 May 2026**, Azure OpenAI quota is tracked at **subscription scope** rather than per-resource or per-region. All resources and regions in a subscription share one quota pool per deployment tier:

- **Global Standard** — shared across all regions in the subscription.
- **Data Zone Standard** — shared within a data zone (US or EU).

Quota auto-upgrades through seven tiers (Free → Tier 1 → … → Tier 6) based on consumption. Enterprise Agreement (EA/MCA-E) customers start at higher tiers. To check the current tier programmatically:

```bash
curl -X GET \
  "https://management.azure.com/subscriptions/$(az account show --query id -o tsv)/providers/Microsoft.CognitiveServices/quotaTiers?api-version=2025-10-01-preview" \
  -H "Authorization: Bearer $(az account get-access-token --resource https://management.azure.com --query accessToken -o tsv)"
```

To request additional quota: <https://aka.ms/oai/stuquotarequest>

> Source: [Azure OpenAI in Microsoft Foundry Models — Quotas and Limits](https://learn.microsoft.com/en-us/azure/foundry/openai/quotas-limits)

---

## 7. Summary of What to Record After Provisioning

| Item | Where to find it | What to record |
|---|---|---|
| M365 tenant domain | Developer program dashboard | `<yourtenant>.onmicrosoft.com` |
| Tenant ID (GUID) | Entra admin centre → Entra ID → Properties | GUID string |
| Global Admin UPN | Admin centre → Active users | `admin@<tenant>.onmicrosoft.com` |
| Admin password | Set during sandbox setup | Lives in the Global Administrator's personal password manager; never recorded in any project artefact or shared credential store |
| Shared mailbox address | Exchange admin centre → Recipients → Mailboxes | `inventory-agent@<tenant>.onmicrosoft.com` |
| Azure Subscription ID | `az account show --query id` | GUID string |
| Azure Subscription role | `az role assignment list` output | Expect `Owner` for sandbox |
| Azure region (M365 data centre) | Chosen at sandbox setup; immutable | e.g. `West Europe` |
| Azure OpenAI quota tier | `/quotaTiers` API or portal | Tier number + limit per model |

---

## 8. Regional and Quota Limits Subsequent Tickets Must Respect

1. **M365 tenant region is immutable** — once chosen at sandbox setup it cannot be changed. All Exchange Online and Teams data is stored in the selected geography.
2. **Azure Sandbox region** — deployments must use regions where both Foundry Agent Service and Azure OpenAI are available. As of research date, `West Europe` and `East US` cover the full tool set (see `azure-agent-runtimes.md` Section 2A for the region matrix).
3. **Shared mailbox storage cap** — 50 GB without an additional licence; 100 GB with Exchange Online Plan 2. The dev sandbox E5 licence covers Plan 2.
4. **Teams custom-app upload propagation** — up to 24 hours after toggling. Do not treat immediate failure as a configuration error within that window.
5. **Graph API subscription renewals** — mail subscriptions expire after 7 days (basic) or 1 day (rich/encrypted). Ticket #23 (Graph subscription renewal) must account for this.
6. **Azure OpenAI sandbox quota** — new sandboxes start at Free Tier or Tier 1; sufficient for development but may throttle load-test volumes. Request increases at <https://aka.ms/oai/stuquotarequest> if needed before performance testing.

---

## Sources

| Source | URL |
|--------|-----|
| M365 Developer Program overview | <https://learn.microsoft.com/en-us/office/developer-program/microsoft-365-developer-program> |
| Set up M365 E5 sandbox subscription | <https://learn.microsoft.com/en-us/office/developer-program/microsoft-365-developer-program-get-started> |
| About administrator roles in M365 admin centre | <https://learn.microsoft.com/en-us/microsoft-365/admin/add-users/about-admin-roles> |
| Prepare your Microsoft 365 tenant for Teams | <https://learn.microsoft.com/en-us/microsoftteams/platform/concepts/build-and-test/prepare-your-o365-tenant> |
| Manage custom app policies and settings in Teams | <https://learn.microsoft.com/en-us/microsoftteams/teams-custom-app-policies-and-settings> |
| Manage apps in Teams admin centre | <https://learn.microsoft.com/en-us/microsoftteams/manage-apps> |
| Create a shared mailbox | <https://learn.microsoft.com/en-us/microsoft-365/admin/email/create-a-shared-mailbox> |
| About shared mailboxes | <https://learn.microsoft.com/en-us/microsoft-365/admin/email/about-shared-mailboxes> |
| Manage permissions for recipients in Exchange Online (user/delegate delegation — **not** used for Graph app access) | <https://learn.microsoft.com/en-us/exchange/recipients-in-exchange-online/manage-permissions-for-recipients> |
| Microsoft Graph permissions reference — Mail permissions | <https://learn.microsoft.com/en-us/graph/permissions-reference#mail-permissions> |
| How to find your tenant ID — Microsoft Entra | <https://learn.microsoft.com/en-us/entra/fundamentals/how-to-find-tenant> |
| List Azure role assignments using Azure CLI | <https://learn.microsoft.com/en-us/azure/role-based-access-control/role-assignments-list-cli> |
| Azure resource providers and types | <https://learn.microsoft.com/en-us/azure/azure-resource-manager/management/resource-providers-and-types> |
| View quotas in the Azure portal | <https://learn.microsoft.com/en-us/azure/quotas/view-quotas> |
| Azure OpenAI quotas and limits | <https://learn.microsoft.com/en-us/azure/foundry/openai/quotas-limits> |
