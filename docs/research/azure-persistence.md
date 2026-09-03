# Research: Azure persistence options for the multi-channel inventory agent

**Research date:** 2026-08-28  
**Issue:** #7  
**Question:** Which Azure data services best fit (a) inventory as the system of record and (b) conversation/session state shared across channels?

## Executive summary

For the **inventory system of record**, **Azure SQL Database** is the strongest default. It is a fully managed relational database, has first-party Microsoft Entra authentication, offers built-in SQL auditing, and uniquely combines a **serverless** consumption model with a **lifetime free offer** that can realistically cover a hobby or early-startup workload. ([Azure SQL overview](https://learn.microsoft.com/en-us/azure/azure-sql/database/sql-database-paas-overview?view=azuresql), [Entra auth](https://learn.microsoft.com/en-us/azure/azure-sql/database/authentication-aad-overview?view=azuresql), [auditing](https://learn.microsoft.com/en-us/azure/azure-sql/database/auditing-overview?view=azuresql), [serverless](https://learn.microsoft.com/en-us/azure/azure-sql/database/serverless-tier-overview?view=azuresql), [free offer](https://learn.microsoft.com/en-us/azure/azure-sql/database/free-offer?view=azuresql))

For **shared conversation/session state across channels**, the best default is **Azure Table Storage** if you want a cheap, app-owned store for turn metadata, channel/user/session indexes, cursors, and light snapshots. It is schemaless, key-oriented, low-cost, and supported by first-party SDKs in .NET, JavaScript/TypeScript, and Python, while still supporting Microsoft Entra ID plus managed identity for data access on storage endpoints. ([Table overview](https://learn.microsoft.com/en-us/azure/storage/tables/table-storage-overview), [Storage Entra auth](https://learn.microsoft.com/en-us/azure/storage/blobs/authorize-access-azure-active-directory), [.NET Tables SDK](https://learn.microsoft.com/en-us/dotnet/api/overview/azure/data.tables-readme?view=azure-dotnet), [JS Tables SDK](https://learn.microsoft.com/en-us/javascript/api/overview/azure/data-tables-readme?view=azure-node-latest), [Python Tables SDK](https://learn.microsoft.com/en-us/python/api/overview/azure/data-tables-readme?view=azure-python))

Two important caveats change the answer. First, **Azure Cache for Redis is retiring** (Basic/Standard/Premium by 2028-09-30 and Enterprise/Enterprise Flash by 2027-03-31), so it is a poor fresh choice for new durable session architecture even though Redis is technically good for ephemeral session caching. Second, **Foundry Agent Service already has managed conversation/thread storage**: in basic setup the platform stores agent state itself, and in standard setup it stores files, threads, and vector stores in your Azure resources. If every channel is funneled through Foundry conversations, a separate conversation store can be unnecessary; if you need application-owned cross-channel state, analytics, portability, or non-Foundry workflows, keep your own store. ([Redis retirement FAQ](https://learn.microsoft.com/en-us/azure/azure-cache-for-redis/retirement-faq), [Foundry runtime components](https://learn.microsoft.com/en-us/azure/foundry/agents/concepts/runtime-components), [Foundry environment setup](https://learn.microsoft.com/en-us/azure/foundry/agents/environment-setup))

> **Pricing note:** Microsoft pricing pages were usable for billing models and SKU floors, but the static fetch output did not return stable region-specific dollar figures. This write-up therefore compares **billing shape** and **cost floor** rather than quoting fragile point-in-time prices.

## Inventory store candidates

### Azure SQL Database

**1. Fit for a small relational inventory with audit/history trail**  
Excellent fit. Azure SQL Database is a fully managed relational PaaS engine, and Microsoft explicitly positions it as the data storage layer for applications and modern cloud apps. For a small inventory with products, stock movements, channel listings, and audit rows, the relational model is a natural match. Azure SQL also has native SQL auditing that can retain an audit trail of selected events and write logs to Azure Storage, Log Analytics, or Event Hubs. ([Azure SQL overview](https://learn.microsoft.com/en-us/azure/azure-sql/database/sql-database-paas-overview?view=azuresql), [auditing overview](https://learn.microsoft.com/en-us/azure/azure-sql/database/auditing-overview?view=azuresql))

**2. Entra managed-identity support**  
Yes. Azure SQL supports Microsoft Entra users, groups, applications, service principals, and both system-assigned and user-assigned managed identities for passwordless authentication. Microsoft explicitly recommends managed identities for services that connect to the database. ([Entra auth overview](https://learn.microsoft.com/en-us/azure/azure-sql/database/authentication-aad-overview?view=azuresql))

**3. Cost at low scale (<1,000 ops/day)**  
Best of the inventory candidates. Azure SQL serverless bills compute **per second**, can **auto-pause**, and charges **zero compute** while paused; only storage is billed during inactivity. That billing shape strongly favors a sporadic, low-traffic inventory app. ([serverless overview](https://learn.microsoft.com/en-us/azure/azure-sql/database/serverless-tier-overview?view=azuresql))

**4. Serverless and free tiers**  
This is the only candidate here with both. The serverless tier is available for single databases. The free offer gives each database **100,000 vCore seconds**, **32 GB of data**, and **32 GB of backup storage** free per month, for the lifetime of the subscription, for up to **10** General Purpose databases. ([serverless overview](https://learn.microsoft.com/en-us/azure/azure-sql/database/serverless-tier-overview?view=azuresql), [free offer](https://learn.microsoft.com/en-us/azure/azure-sql/database/free-offer?view=azuresql))

**5. ORM/driver support in .NET, TypeScript, and Python**  
Strong. Microsoft documents first-party connectivity for **C#** via ADO.NET / SqlClient, **Node.js** drivers, and **Python** via `mssql-python`; it also lists ORM examples including **Entity Framework / EF Core** for .NET, **Sequelize** for Node.js, **Django** for Python, and other frameworks. ([connect/query guide](https://learn.microsoft.com/en-us/azure/azure-sql/database/connect-query-content-reference-guide?view=azuresql), [development overview](https://learn.microsoft.com/en-us/azure/azure-sql/database/develop-overview?view=azuresql))

**Bottom line:** best default for inventory SoR unless you have a strong PostgreSQL preference or a specific global-scale document-data requirement.

### Azure Database for PostgreSQL Flexible Server

**1. Fit for a small relational inventory with audit/history trail**  
Also an excellent fit. PostgreSQL Flexible Server is a fully managed relational service with granular configuration control, built-in backups, PITR, and standard PostgreSQL semantics. For inventory plus history tables, it is technically very suitable; the trade-off versus Azure SQL is mostly ecosystem preference and cost shape, not capability. ([service overview](https://learn.microsoft.com/en-us/azure/postgresql/overview), [backup & restore](https://learn.microsoft.com/en-us/azure/postgresql/backup-restore/concepts-backup-restore))

**2. Entra managed-identity support**  
Yes. Microsoft documents Microsoft Entra authentication for Flexible Server and explicitly allows a Microsoft Entra **managed identity** to be configured as an admin or connection identity. Clients use Entra access tokens as the PostgreSQL password. ([Entra auth for PostgreSQL](https://learn.microsoft.com/en-us/azure/postgresql/security/security-entra-configure))

**3. Cost at low scale (<1,000 ops/day)**  
Good, but usually worse than Azure SQL serverless for idle-heavy usage. Flexible Server is billed on a **predictable hourly rate** for provisioned compute and storage, and you are billed for each **full hour** the server exists. The main cost relief is that you can **stop** the server and then pay only for storage and excess backup storage while stopped. ([pricing FAQ](https://azure.microsoft.com/en-us/pricing/details/postgresql/flexible-server/), [service overview](https://learn.microsoft.com/en-us/azure/postgresql/overview))

**4. Serverless and free tiers**  
No clearly documented true serverless tier. Microsoft instead highlights the **Burstable** tier for low-cost development and the ability to **stop and start** the server to reduce TCO. I did **not** find a current, service-specific lifetime free tier comparable to Azure SQL or Cosmos DB; the current docs only point to the general Azure free account and a “try for free” entry on the landing page, so treat any PostgreSQL free-tier assumption as unverified. ([service overview](https://learn.microsoft.com/en-us/azure/postgresql/overview), [landing page](https://learn.microsoft.com/en-us/azure/postgresql/), [create-server quickstart](https://learn.microsoft.com/en-us/azure/postgresql/configure-maintain/quickstart-create-server))

**5. ORM/driver support in .NET, TypeScript, and Python**  
Strong, using mainstream PostgreSQL ecosystem drivers. Microsoft lists **Npgsql** for .NET, **pg** for Node.js, and **psycopg** for Python. That is enough to support EF Core/Npgsql, Prisma/TypeORM/Drizzle via `pg`, and SQLAlchemy/Django via PostgreSQL drivers, even though Microsoft’s page focuses on drivers rather than ORMs. ([connection libraries](https://learn.microsoft.com/en-us/azure/postgresql/connectivity/concepts-connection-libraries))

**Bottom line:** best choice if your team strongly prefers PostgreSQL, extensions, or SQL portability; otherwise Azure SQL is usually cheaper/simpler at hobby scale.

### Azure Cosmos DB (NoSQL API)

**1. Fit for a small relational inventory with audit/history trail**  
Usable, but not the best fit. Cosmos DB is a fully managed **NoSQL** and vector database, and Microsoft explicitly says it is a **poor fit for highly relational apps**, suggesting Azure SQL or Azure Database for MySQL instead. Its modeling guidance emphasizes denormalized JSON documents and embedding/reference trade-offs instead of relational joins. It does support change-friendly history patterns and continuous backup/PITR, but for a small relational inventory you would be taking on document-modeling complexity without getting much value from Cosmos DB’s global-scale strengths. ([Cosmos overview](https://learn.microsoft.com/en-us/azure/cosmos-db/overview), [data modeling](https://learn.microsoft.com/en-us/azure/cosmos-db/modeling-data), [continuous backup](https://learn.microsoft.com/en-us/azure/cosmos-db/continuous-backup-restore-introduction))

**2. Entra managed-identity support**  
Yes. Azure Cosmos DB for NoSQL supports role-based access control with Microsoft Entra ID, and Microsoft documents disabling local key auth so applications are required to use Entra authentication. The .NET and Python quickstarts also show `DefaultAzureCredential` with `CosmosClient`. ([RBAC with Entra](https://learn.microsoft.com/en-us/azure/cosmos-db/how-to-connect-role-based-access-control), [.NET quickstart](https://learn.microsoft.com/en-us/azure/cosmos-db/quickstart-dotnet), [Python quickstart](https://learn.microsoft.com/en-us/azure/cosmos-db/quickstart-python))

**3. Cost at low scale (<1,000 ops/day)**  
Potentially very good **if** you use **serverless**. Cosmos DB serverless charges only for consumed RUs and storage, has **no minimum charge**, and is explicitly positioned for intermittent and unpredictable traffic with long idle times. Provisioned or autoscale modes are less attractive at hobby scale unless you need their guarantees or want the lifetime free tier. ([serverless](https://learn.microsoft.com/en-us/azure/cosmos-db/serverless), [pricing page](https://azure.microsoft.com/en-us/pricing/details/cosmos-db/))

**4. Serverless and free tiers**  
Yes, but not together. Cosmos DB has a **serverless** account type, but the **lifetime free tier is not available for serverless accounts**. The lifetime free tier instead gives **1000 RU/s** and **25 GB** of storage on one account per subscription for provisioned/autoscale accounts. ([serverless](https://learn.microsoft.com/en-us/azure/cosmos-db/serverless), [free tier](https://learn.microsoft.com/en-us/azure/cosmos-db/free-tier))

**5. ORM/driver support in .NET, TypeScript, and Python**  
Strong SDK support, but document-database oriented rather than ORM-centric. Microsoft documents `Microsoft.Azure.Cosmos` for .NET, `@azure/cosmos` for JavaScript/TypeScript, and `azure-cosmos` for Python, all with first-party quickstarts and SDK repos. ([.NET quickstart](https://learn.microsoft.com/en-us/azure/cosmos-db/quickstart-dotnet), [JS SDK](https://learn.microsoft.com/en-us/javascript/api/overview/azure/cosmos-readme?view=azure-node-latest), [Python SDK](https://learn.microsoft.com/en-us/python/api/overview/azure/cosmos-readme?view=azure-python))

**Bottom line:** attractive if you want one globally scalable JSON/document store, but a weak default for a small relational inventory ledger.

## Session / conversation state candidates

### Azure Table Storage

**1. Fit for shared session/conversation state**  
Good default for app-owned state. Azure Table Storage is a schemaless key/attribute store for structured NoSQL data and is intended for datasets that do **not** require complex joins, foreign keys, or stored procedures. That maps well to session envelopes, channel+user indexes, per-thread metadata, checkpoint rows, and compact denormalized turn summaries. It is a worse fit for rich transcript querying or highly relational analytics. ([Table overview](https://learn.microsoft.com/en-us/azure/storage/tables/table-storage-overview))

**2. Entra managed-identity support**  
Yes, for Azure Storage endpoints. Microsoft recommends Microsoft Entra ID with managed identities for blob, queue, and **table** data whenever possible. The JavaScript and Python Tables SDK pages also document `TokenCredential`/AAD support for Azure Storage accounts. ([Storage Entra auth](https://learn.microsoft.com/en-us/azure/storage/blobs/authorize-access-azure-active-directory), [JS Tables SDK](https://learn.microsoft.com/en-us/javascript/api/overview/azure/data-tables-readme?view=azure-node-latest), [Python Tables SDK](https://learn.microsoft.com/en-us/python/api/overview/azure/data-tables-readme?view=azure-python))

**3. Cost at low scale (<1,000 ops/day)**  
Very good. Table Storage is part of a standard general-purpose v2 storage account and is positioned by Microsoft as fast and cost-effective, typically lower cost than traditional SQL for similar volumes. The billing shape is consumption-based rather than dedicated-instance based. Exact current per-operation pricing should still be checked in the Azure pricing calculator because the static pricing page output was incomplete. ([Table overview](https://learn.microsoft.com/en-us/azure/storage/tables/table-storage-overview), [storage account overview](https://learn.microsoft.com/en-us/azure/storage/common/storage-account-overview), [pricing page](https://azure.microsoft.com/en-us/pricing/details/storage/tables/))

**4. Serverless and free tiers**  
Effectively serverless/consumption-based, but I found **no dedicated Table Storage free tier** in current docs. The applicable free offer is the general Azure free account, not a service-specific lifetime free plan. ([storage account create](https://learn.microsoft.com/en-us/azure/storage/common/storage-account-create), [pricing page](https://azure.microsoft.com/en-us/pricing/details/storage/tables/))

**5. ORM/driver support in .NET, TypeScript, and Python**  
Strong first-party SDK support via **Azure.Data.Tables**, **@azure/data-tables**, and **azure-data-tables**. The SDKs target both Azure Table Storage and Cosmos DB Table API. ([.NET Tables SDK](https://learn.microsoft.com/en-us/dotnet/api/overview/azure/data.tables-readme?view=azure-dotnet), [JS Tables SDK](https://learn.microsoft.com/en-us/javascript/api/overview/azure/data-tables-readme?view=azure-node-latest), [Python Tables SDK](https://learn.microsoft.com/en-us/python/api/overview/azure/data-tables-readme?view=azure-python))

**Bottom line:** recommended default for shared channel/session state you own yourself.

### Azure Blob Storage

**1. Fit for shared session/conversation state**  
Mixed. Blob Storage is object storage optimized for **unstructured data** such as text, binary data, logs, backups, and archives. It works well for raw transcript blobs, audio attachments, export snapshots, and append-only archives, but it is awkward as the primary store for “find me the session for this user/channel/thread” or “resume from last checkpoint” workflows unless paired with an index store. ([Blob introduction](https://learn.microsoft.com/en-us/azure/storage/blobs/storage-blobs-introduction))

**2. Entra managed-identity support**  
Yes. Microsoft supports Microsoft Entra authorization for blob data and explicitly recommends managed identities. ([Blob Entra auth](https://learn.microsoft.com/en-us/azure/storage/blobs/authorize-access-azure-active-directory))

**3. Cost at low scale (<1,000 ops/day)**  
Excellent for archive-heavy or attachment-heavy workloads. Blob Storage pricing is pay-as-you-go by GB and transactions, with multiple access tiers. The billing model has effectively no dedicated instance floor, so low-traffic archives are cheap. ([Blob pricing](https://azure.microsoft.com/en-us/pricing/details/storage/blobs/))

**4. Serverless and free tiers**  
Effectively serverless/consumption-based, but no dedicated Blob-only lifetime free tier was evident in the current docs I reviewed. ([Blob pricing](https://azure.microsoft.com/en-us/pricing/details/storage/blobs/), [storage account create](https://learn.microsoft.com/en-us/azure/storage/common/storage-account-create))

**5. ORM/driver support in .NET, TypeScript, and Python**  
Strong first-party SDK support via **Azure.Storage.Blobs**, **@azure/storage-blob**, and **azure-storage-blob**. ([.NET Blob SDK](https://learn.microsoft.com/en-us/dotnet/api/overview/azure/storage.blobs-readme?view=azure-dotnet), [JS Blob SDK](https://learn.microsoft.com/en-us/javascript/api/overview/azure/storage-blob-readme?view=azure-node-latest), [Python Blob SDK](https://learn.microsoft.com/en-us/python/api/overview/azure/storage-blob-readme?view=azure-python))

**Bottom line:** best as the **payload/archive layer**, not usually as the only conversation-state database.

### Azure Cache for Redis

**1. Fit for shared session/conversation state**  
Technically good for **ephemeral** session state, but poor as the long-term recommendation here. Microsoft explicitly lists **session store** as a key Redis pattern because associating user information in an in-memory cache is faster than hitting a relational database. But Redis is still an in-memory cache first, and Microsoft warns that persistence is for resilience, **not** backup or PITR. ([Cache overview](https://learn.microsoft.com/en-us/azure/azure-cache-for-redis/cache-overview), [persistence](https://learn.microsoft.com/en-us/azure/azure-cache-for-redis/cache-how-to-premium-persistence))

**2. Entra managed-identity support**  
Yes. Azure Cache for Redis supports Microsoft Entra authentication; client apps can use service principals or managed identities and refresh tokens over time. ([Redis Entra auth](https://learn.microsoft.com/en-us/azure/azure-cache-for-redis/cache-azure-active-directory-for-authentication))

**3. Cost at low scale (<1,000 ops/day)**  
Weakest of the session candidates for hobby scale because it has a dedicated cache-instance cost floor. Even the pricing pages are organized around fixed cache SKUs rather than pure consumption. ([legacy cache pricing](https://azure.microsoft.com/en-us/pricing/details/cache/), [managed redis pricing](https://azure.microsoft.com/en-us/pricing/details/managed-redis/))

**4. Serverless and free tiers**  
No serverless tier and no current free tier surfaced in the reviewed docs. More importantly, the service is being retired: Basic/Standard/Premium retire on **2028-09-30**, and Enterprise/Enterprise Flash retire on **2027-03-31**. ([retirement FAQ](https://learn.microsoft.com/en-us/azure/azure-cache-for-redis/retirement-faq), [cache overview](https://learn.microsoft.com/en-us/azure/azure-cache-for-redis/cache-overview))

**5. ORM/driver support in .NET, TypeScript, and Python**  
Good client support, but via Redis ecosystem libraries rather than a storage-style Azure SDK. Microsoft recommends **StackExchange.Redis** for .NET, **node_redis** / **ioredis** for Node.js, and its Python quickstart uses `redis` plus `redis-entraid`; the .NET quickstart uses `StackExchange.Redis` plus `Microsoft.Azure.StackExchangeRedis`. ([client-library guidance](https://learn.microsoft.com/en-us/azure/redis/best-practices-client-libraries), [.NET quickstart](https://learn.microsoft.com/en-us/azure/redis/dotnet), [TypeScript quickstart](https://learn.microsoft.com/en-us/azure/redis/nodejs-get-started), [Python quickstart](https://learn.microsoft.com/en-us/azure/redis/python-get-started))

**Bottom line:** only choose Redis as a **cache in front of** another store, not as the recommended primary shared conversation store for a new build.

## Does Foundry Agent Service already provide conversation-thread storage?

Yes—**to a point**. Foundry Agent Service defines **agents**, **conversations**, and **responses** as first-class runtime components, and a **conversation persists history across turns**. Microsoft also says Agent Runtime “manages conversations” and, for hosted agents, provides **session-level state persistence**. ([runtime components](https://learn.microsoft.com/en-us/azure/foundry/agents/concepts/runtime-components), [Foundry agents overview](https://learn.microsoft.com/en-us/azure/foundry/agents/overview))

The storage model depends on setup. In **Basic Setup**, Foundry manages agent states using the platform’s built-in storage. In **Standard Setup**, all customer data—including **files, threads, and vector stores**—are stored in your own Azure resources; Microsoft specifically calls out Azure Storage, Azure Cosmos DB, and Azure AI Search as BYO resources used to store that data. ([environment setup](https://learn.microsoft.com/en-us/azure/foundry/agents/environment-setup))

So a separate conversation store is **not automatically necessary** if:
- every channel interaction is routed into Foundry conversations,
- Foundry’s conversation model matches your product’s state model, and
- you are comfortable coupling state lifecycle to Foundry.

A separate store is still warranted if you need:
- channel-agnostic session state that exists **outside** Foundry threads,
- app-owned analytics or replay,
- cross-agent/business workflow state,
- portability away from Foundry, or
- richer lookup/indexing than “conversation history” alone.

## Recommendation

### Recommended architecture

1. **Inventory system of record:** **Azure SQL Database (serverless, General Purpose)**. It is the cleanest fit for a small relational inventory plus audit/history, supports passwordless Entra/managed identity, and has the best hobby-scale economics because of serverless auto-pause plus the lifetime free offer. ([Azure SQL overview](https://learn.microsoft.com/en-us/azure/azure-sql/database/sql-database-paas-overview?view=azuresql), [serverless](https://learn.microsoft.com/en-us/azure/azure-sql/database/serverless-tier-overview?view=azuresql), [free offer](https://learn.microsoft.com/en-us/azure/azure-sql/database/free-offer?view=azuresql), [auditing](https://learn.microsoft.com/en-us/azure/azure-sql/database/auditing-overview?view=azuresql))

2. **Shared channel/session state:** **Azure Table Storage** for app-owned metadata/state, plus **Azure Blob Storage** only if you want cheap raw transcript or attachment archival. Table Storage gives the lowest-friction key/value-ish shared state store with first-party SDKs and Entra/managed identity. ([Table overview](https://learn.microsoft.com/en-us/azure/storage/tables/table-storage-overview), [Storage Entra auth](https://learn.microsoft.com/en-us/azure/storage/blobs/authorize-access-azure-active-directory), [Blob intro](https://learn.microsoft.com/en-us/azure/storage/blobs/storage-blobs-introduction))

3. **If using Foundry Agent Service end-to-end:** start by using **Foundry-managed conversations/threads** instead of building a separate conversation store prematurely. Add your own Table/Blob layer only when you have concrete requirements around cross-channel business state, custom analytics, or portability. ([runtime components](https://learn.microsoft.com/en-us/azure/foundry/agents/concepts/runtime-components), [environment setup](https://learn.microsoft.com/en-us/azure/foundry/agents/environment-setup))

### Trade-offs that would overturn this recommendation

- Pick **PostgreSQL Flexible Server** instead of Azure SQL if PostgreSQL compatibility, extensions, or team familiarity matters more than Azure SQL’s free/serverless advantage. ([PostgreSQL overview](https://learn.microsoft.com/en-us/azure/postgresql/overview), [connection libraries](https://learn.microsoft.com/en-us/azure/postgresql/connectivity/concepts-connection-libraries))
- Pick **Cosmos DB NoSQL** for inventory only if the model is genuinely document-centric, globally distributed, and latency-sensitive enough to justify denormalized JSON over relational design. ([Cosmos overview](https://learn.microsoft.com/en-us/azure/cosmos-db/overview), [data modeling](https://learn.microsoft.com/en-us/azure/cosmos-db/modeling-data))
- Pick **Blob-first** session storage only if most state is large immutable payloads and you already have a separate index elsewhere. ([Blob intro](https://learn.microsoft.com/en-us/azure/storage/blobs/storage-blobs-introduction))
- Use **Redis** only as an acceleration/cache layer in front of another source of truth, never as the new long-term primary store here because of retirement and lack of serverless economics. ([retirement FAQ](https://learn.microsoft.com/en-us/azure/azure-cache-for-redis/retirement-faq), [cache overview](https://learn.microsoft.com/en-us/azure/azure-cache-for-redis/cache-overview))

## References

| Service / topic | URL |
|---|---|
| Azure SQL Database landing | https://learn.microsoft.com/en-us/azure/azure-sql/database/ |
| Azure SQL overview | https://learn.microsoft.com/en-us/azure/azure-sql/database/sql-database-paas-overview?view=azuresql |
| Azure SQL serverless | https://learn.microsoft.com/en-us/azure/azure-sql/database/serverless-tier-overview?view=azuresql |
| Azure SQL free offer | https://learn.microsoft.com/en-us/azure/azure-sql/database/free-offer?view=azuresql |
| Azure SQL Entra auth | https://learn.microsoft.com/en-us/azure/azure-sql/database/authentication-aad-overview?view=azuresql |
| Azure SQL auditing | https://learn.microsoft.com/en-us/azure/azure-sql/database/auditing-overview?view=azuresql |
| Azure SQL connect/query guide | https://learn.microsoft.com/en-us/azure/azure-sql/database/connect-query-content-reference-guide?view=azuresql |
| Azure SQL pricing page | https://azure.microsoft.com/en-us/pricing/details/azure-sql-database/single/ |
| PostgreSQL landing | https://learn.microsoft.com/en-us/azure/postgresql/ |
| PostgreSQL overview | https://learn.microsoft.com/en-us/azure/postgresql/overview |
| PostgreSQL Entra auth | https://learn.microsoft.com/en-us/azure/postgresql/security/security-entra-configure |
| PostgreSQL connection libraries | https://learn.microsoft.com/en-us/azure/postgresql/connectivity/concepts-connection-libraries |
| PostgreSQL backup & restore | https://learn.microsoft.com/en-us/azure/postgresql/backup-restore/concepts-backup-restore |
| PostgreSQL pricing page | https://azure.microsoft.com/en-us/pricing/details/postgresql/flexible-server/ |
| Cosmos DB overview | https://learn.microsoft.com/en-us/azure/cosmos-db/overview |
| Cosmos DB RBAC / Entra | https://learn.microsoft.com/en-us/azure/cosmos-db/how-to-connect-role-based-access-control |
| Cosmos DB free tier | https://learn.microsoft.com/en-us/azure/cosmos-db/free-tier |
| Cosmos DB serverless | https://learn.microsoft.com/en-us/azure/cosmos-db/serverless |
| Cosmos DB data modeling | https://learn.microsoft.com/en-us/azure/cosmos-db/modeling-data |
| Cosmos DB PITR / continuous backup | https://learn.microsoft.com/en-us/azure/cosmos-db/continuous-backup-restore-introduction |
| Cosmos DB .NET quickstart | https://learn.microsoft.com/en-us/azure/cosmos-db/quickstart-dotnet |
| Cosmos DB Python quickstart | https://learn.microsoft.com/en-us/azure/cosmos-db/quickstart-python |
| Cosmos DB JavaScript SDK | https://learn.microsoft.com/en-us/javascript/api/overview/azure/cosmos-readme?view=azure-node-latest |
| Cosmos DB pricing page | https://azure.microsoft.com/en-us/pricing/details/cosmos-db/ |
| Table Storage landing | https://learn.microsoft.com/en-us/azure/storage/tables/ |
| Table Storage overview | https://learn.microsoft.com/en-us/azure/storage/tables/table-storage-overview |
| Storage account overview | https://learn.microsoft.com/en-us/azure/storage/common/storage-account-overview |
| Storage account creation | https://learn.microsoft.com/en-us/azure/storage/common/storage-account-create |
| Blob / Table Entra auth | https://learn.microsoft.com/en-us/azure/storage/blobs/authorize-access-azure-active-directory |
| Azure Tables .NET SDK | https://learn.microsoft.com/en-us/dotnet/api/overview/azure/data.tables-readme?view=azure-dotnet |
| Azure Tables JS SDK | https://learn.microsoft.com/en-us/javascript/api/overview/azure/data-tables-readme?view=azure-node-latest |
| Azure Tables Python SDK | https://learn.microsoft.com/en-us/python/api/overview/azure/data-tables-readme?view=azure-python |
| Table Storage pricing page | https://azure.microsoft.com/en-us/pricing/details/storage/tables/ |
| Blob Storage intro | https://learn.microsoft.com/en-us/azure/storage/blobs/storage-blobs-introduction |
| Blob Storage pricing page | https://azure.microsoft.com/en-us/pricing/details/storage/blobs/ |
| Azure Blob .NET SDK | https://learn.microsoft.com/en-us/dotnet/api/overview/azure/storage.blobs-readme?view=azure-dotnet |
| Azure Blob JS SDK | https://learn.microsoft.com/en-us/javascript/api/overview/azure/storage-blob-readme?view=azure-node-latest |
| Azure Blob Python SDK | https://learn.microsoft.com/en-us/python/api/overview/azure/storage-blob-readme?view=azure-python |
| Azure Cache for Redis overview | https://learn.microsoft.com/en-us/azure/azure-cache-for-redis/cache-overview |
| Azure Cache for Redis Entra auth | https://learn.microsoft.com/en-us/azure/azure-cache-for-redis/cache-azure-active-directory-for-authentication |
| Azure Cache for Redis persistence | https://learn.microsoft.com/en-us/azure/azure-cache-for-redis/cache-how-to-premium-persistence |
| Azure Cache for Redis retirement FAQ | https://learn.microsoft.com/en-us/azure/azure-cache-for-redis/retirement-faq |
| Legacy Azure Cache pricing page | https://azure.microsoft.com/en-us/pricing/details/cache/ |
| Azure Managed Redis overview | https://learn.microsoft.com/en-us/azure/redis/overview |
| Azure Managed Redis client libraries | https://learn.microsoft.com/en-us/azure/redis/best-practices-client-libraries |
| Azure Managed Redis .NET quickstart | https://learn.microsoft.com/en-us/azure/redis/dotnet |
| Azure Managed Redis TypeScript quickstart | https://learn.microsoft.com/en-us/azure/redis/nodejs-get-started |
| Azure Managed Redis Python quickstart | https://learn.microsoft.com/en-us/azure/redis/python-get-started |
| Azure Managed Redis pricing page | https://azure.microsoft.com/en-us/pricing/details/managed-redis/ |
| Foundry Agent Service overview | https://learn.microsoft.com/en-us/azure/foundry/agents/overview |
| Foundry runtime components | https://learn.microsoft.com/en-us/azure/foundry/agents/concepts/runtime-components |
| Foundry environment setup | https://learn.microsoft.com/en-us/azure/foundry/agents/environment-setup |
