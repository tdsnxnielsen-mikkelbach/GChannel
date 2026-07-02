# Regional deployment guide (per-country reseller instance)

This app is a distributor/reseller dashboard on top of the **Google Cloud Channel API**. Everything it
shows is scoped to **one Cloud Channel reseller account** (`accounts/C0xxxxxxx`), which is tied to a
specific country/legal entity, its Google Cloud project, its OAuth app, and its service account.

If a colleague in another country wants to run this setup, they deploy a **separate, independent
instance**: the **application code is identical**, but every credential and id below is **region-specific**.
This guide is the step-by-step for standing up a new regional instance — what to configure in
[console.cloud.google.com](https://console.cloud.google.com) and the [Google Admin console](https://admin.google.com),
and the parameters to pass to the deployment.

> Nothing here changes the Danish (or any existing) instance. Each region has its **own** Google project,
> OAuth client, service account, Channel account id, and **its own Azure environment** — they never share
> state (separate SQL, Redis, Key Vault). Two regions can run side by side from the same source repo, each
> with its own `azd` environment.

---

## 0. What is region-specific vs shared

| Thing | Shared across regions | Region-specific (set per instance) |
| --- | --- | --- |
| Application source code (this repo) | ✅ Same | — |
| Cloud Channel **reseller account id** | — | ✅ `accounts/C0xxxxxxx` for that country |
| Google Cloud **project** (APIs, quotas, Pub/Sub, WIF pool) | — | ✅ One per region |
| **OAuth 2.0 client** (sign-in) | — | ✅ One per region (its own redirect URIs) |
| **Service account** + domain-wide delegation | — | ✅ One per region (authorized in that region's Workspace) |
| **Azure** subscription / resource group / Key Vault / SQL / Redis | — | ✅ One `azd` environment per region |
| Reseller admin user the SA impersonates | — | ✅ An admin in that region's Workspace tenant |

---

## 1. Prerequisites

The person deploying needs:

- **Google side**
  - Owner/Editor on (or the ability to create) a **Google Cloud project** for the region.
  - **Super Admin** on the region's **Google Workspace** tenant (required to authorize domain-wide
    delegation).
  - The region's **Cloud Channel reseller account id** (`accounts/C0xxxxxxx`). Ask the Channel account
    owner, or read it from the [Partner Sales Console](https://partners.cloud.google.com).
- **Azure side**
  - An Azure **subscription** and permission to create resource groups + Container Apps.
- **Tooling** (same as [configuration.md](configuration.md#prerequisites))
  - [.NET 10 SDK](https://dotnet.microsoft.com/), [Azure Developer CLI (`azd`)](https://aka.ms/azd),
    the [gcloud CLI](https://cloud.google.com/sdk/docs/install), and (for local runs) Docker Desktop.

---

## 2. Google Cloud setup (`console.cloud.google.com`)

### 2.1 Create / select the region's project

1. In the console's project picker, **New Project** — name it clearly per region, e.g. `gchannel-us`,
   `gchannel-de`, `gchannel-uk`. Note the **Project ID** and **Project number** (both shown on the
   project's *Home / Dashboard*). You'll use the id for Pub/Sub and the number for Workload Identity.

### 2.2 Enable the required APIs

**APIs & Services → Enable APIs and services**, enable:

- **Cloud Channel API** (`cloudchannel.googleapis.com`) — required.
- **Cloud Pub/Sub API** (`pubsub.googleapis.com`) — only if you want change notifications (§7).

Or via CLI:

```powershell
gcloud config set project <region-project-id>
gcloud services enable cloudchannel.googleapis.com
gcloud services enable pubsub.googleapis.com   # optional (notifications)
```

### 2.3 Confirm the reseller account id

The app talks to a single reseller account. Confirm the region's id is `accounts/C0xxxxxxx`. This becomes
the **`GoogleChannelAccountId`** parameter. (It is **not** created here — it already exists as the
region's Channel partnership; you're just recording it.)

### 2.4 Create the OAuth client (user sign-in)

Users sign in with Google; the app forwards their token to the Channel API (scope
`https://www.googleapis.com/auth/apps.order`).

1. **APIs & Services → OAuth consent screen** — configure it (Internal is fine if all users are in the
   region's Workspace; otherwise External). Add the scope
   `https://www.googleapis.com/auth/apps.order`.
2. **APIs & Services → Credentials → Create credentials → OAuth client ID → Web application.**
3. **Authorized redirect URIs** — add both:
   - Local dev: `http://localhost:<web-port>/signin-google`
   - Deployed: `https://<your-web-app-url>/signin-google` (you'll get the real URL after the first
     `azd up`; come back and add it — see [step 5.2](#52-post-deploy-add-the-redirect-uri)).
4. Record the **Client ID** → **`GoogleClientId`** and **Client secret** → **`GoogleClientSecret`**
   (secret).

### 2.5 Create the service account (background refresh, read-model, Pub/Sub)

The dashboard/read-model background worker runs **without a signed-in user**, so it needs a service
account. Because the Channel API has **no service-account identity of its own**, the SA impersonates a
reseller admin via **domain-wide delegation (DWD)**.

1. **IAM & Admin → Service Accounts → Create service account**, e.g.
   `gchannel-dashboard@<region-project-id>.iam.gserviceaccount.com`.
2. On the SA, **enable domain-wide delegation** and note its **Client ID** (a long numeric id — needed in
   the next step).
3. **Keys → Add key → JSON** → download. This JSON is **`GoogleChannelServiceAccountKeyJson`** (secret).
   Store it safely; on Azure it is put in Key Vault.
   > Prefer **key-less Workload Identity Federation** for the Pub/Sub path (see
   > [configuration.md → key-less auth](configuration.md#key-less-auth-with-workload-identity-federation-recommended)).
   > DWD (the dashboard/read-model refresh) still needs the downloaded key — the Google .NET auth library
   > only supports domain-wide delegation on a downloaded key.

### 2.6 Authorize domain-wide delegation (Workspace Admin console)

In [admin.google.com](https://admin.google.com) of the **region's** Workspace tenant (needs Super Admin):

1. **Security → Access and data control → API controls → Manage Domain Wide Delegation.**
2. **Add new** — enter the SA's **Client ID** (from step 2.5) and the scope:
   `https://www.googleapis.com/auth/apps.order`
3. Save. Pick a reseller **admin user** in this tenant for the SA to impersonate, e.g.
   `admin@<region-domain>` → this is **`GoogleChannelImpersonateUser`**.

### 2.7 (Optional) Pub/Sub notifications

If you want live change events, follow the full walkthrough in
[configuration.md → Pub/Sub notifications](configuration.md#pubsub-notifications-7). In short, per region:

1. Register the topic from the app's **Notifications** page (grants your SA subscriber access to Google's
   topic).
2. Create a **subscription** in the region's project against that topic.
3. Grant the SA `roles/pubsub.subscriber` on the subscription.
4. Set **`GoogleChannelPubSubProjectId`** = region project id and **`GoogleChannelPubSubSubscriptionId`** =
   the subscription id.
5. (Recommended) Configure **Workload Identity Federation** for key-less Pub/Sub auth →
   **`GoogleChannelWorkloadIdentityCredentialJson`**. WIF is per region (its own pool/provider trusting
   that region's Entra tenant + the region's ACA managed identity object id).

### 2.8 Channel API quotas

Channel API quotas are **per Google project** and are typically **low** (commonly ~**24/min** each for
`ListEntitlements` and `ListCustomers`). The app paces itself to stay under them, but for a large estate
request an increase under **IAM & Admin → Quotas** for the region's project. The pacing/read-model knobs
(`DashboardRequestsPerMinute`, `DashboardCustomerListRequestsPerMinute`, `ReadModelLinksPerCycle`,
`BackgroundRefreshSeconds`) let you tune to the region's actual quota — see the table in
[configuration.md](configuration.md#optional-tuning-defaults-shown).

---

## 3. Parameters reference

These map 1:1 to the `azd` parameters exposed by the AppHost. Each is `azd env set <Name> "<value>"`
(or answered at the `azd up` prompt). Env-var / config-key mapping is `GoogleChannel__X` → `GoogleChannel:X`.

### Required

| Parameter | Region-specific value | Secret | Where it goes |
| --- | --- | --- | --- |
| `GoogleClientId` | OAuth **Client ID** (step 2.4) | No | Web app env var |
| `GoogleClientSecret` | OAuth **Client secret** (step 2.4) | **Yes** | Key Vault |
| `GoogleChannelAccountId` | `accounts/C0xxxxxxx` (step 2.3) | No | API + Worker env var |

### Optional — background dashboard refresh + read-model (recommended for real use)

| Parameter | Region-specific value | Secret |
| --- | --- | --- |
| `GoogleChannelServiceAccountKeyJson` | SA key JSON (step 2.5) | **Yes** → Key Vault |
| `GoogleChannelImpersonateUser` | reseller admin email (step 2.6), e.g. `admin@<region-domain>` | No |
| `GoogleChannelBackgroundRefreshSeconds` | e.g. `900` (≥ 900 recommended) — `0` disables | No |
| `GoogleChannelUseReadModel` | `true` to serve the dashboard/estate from durable SQL | No |
| `GoogleChannelReadModelLinksPerCycle` | e.g. `18` (per-cycle `ListCustomers` budget) | No |
| `GoogleChannelDashboardRequestsPerMinute` | e.g. `18` (just under the `ListEntitlements` quota) | No |
| `GoogleChannelDashboardCustomerListRequestsPerMinute` | e.g. `18` (just under the `ListCustomers` quota) | No |

### Optional — Pub/Sub notifications (§7)

| Parameter | Region-specific value | Secret |
| --- | --- | --- |
| `GoogleChannelPubSubProjectId` | region project id (step 2.7) | No |
| `GoogleChannelPubSubSubscriptionId` | subscription id (step 2.7) | No |
| `GoogleChannelWorkloadIdentityCredentialJson` | `external_account` config JSON (WIF, step 2.7) | No |

### Not `azd` parameters (tune via `appsettings.json` only)

`CacheSeconds`, `MaxRetryAttempts`, `MaxRetryDelaySeconds`, `DashboardMaxConcurrency`,
`DashboardBudgetSeconds`, `ReadModelCustomersPerCycle`, `ReadModelDashboardCacheSeconds`,
`PubSubMaxNotifications`. These have code defaults; override in
`src/GChannel.ApiService/appsettings.json` (and `src/GChannel.Worker/appsettings.json`) if needed. See
[configuration.md → optional tuning](configuration.md#optional-tuning-defaults-shown).

> Leave any optional parameter you're not using set to an empty string (`""`) or `0` so `azd` doesn't
> prompt for it and the related feature stays off.

---

## 4. Azure setup (per region)

Each region is a separate `azd` environment (its own resource group + Key Vault + SQL + Redis).

1. **Sign in** to the region's Azure context:

   ```powershell
   azd auth login
   az login   # for the az CLI used by post-provision hooks
   ```

2. **Create a new azd environment** for the region (keeps it isolated from other regions):

   ```powershell
   azd env new gchannel-us          # pick a per-region name
   azd env select gchannel-us
   ```

3. **Set the parameters** for this environment (required + whichever optional ones you're enabling):

   ```powershell
   # required
   azd env set GoogleClientId "<oauth-client-id>"
   azd env set GoogleClientSecret "<oauth-client-secret>"
   azd env set GoogleChannelAccountId "accounts/C0xxxxxxx"

   # optional: background refresh + read-model
   $saKey = Get-Content -Raw -Path "C:\keys\gchannel-<region>-sa.json"
   azd env set GoogleChannelServiceAccountKeyJson "$saKey"
   azd env set GoogleChannelImpersonateUser "admin@<region-domain>"
   azd env set GoogleChannelBackgroundRefreshSeconds "900"
   azd env set GoogleChannelUseReadModel "true"
   azd env set GoogleChannelReadModelLinksPerCycle "18"
   azd env set GoogleChannelDashboardRequestsPerMinute "18"
   azd env set GoogleChannelDashboardCustomerListRequestsPerMinute "18"

   # optional: Pub/Sub notifications
   azd env set GoogleChannelPubSubProjectId "<region-project-id>"
   azd env set GoogleChannelPubSubSubscriptionId "<subscription-id>"
   # set the WIF config once you've provisioned (needs the ACA managed-identity object id)
   ```

---

## 5. Deploy

### 5.1 First deploy

```powershell
azd up
```

`azd` provisions the Container Apps environment, **Key Vault**, serverless **Azure SQL**, **Azure Managed
Redis**, then deploys the three container apps (`webfrontend` external, `apiservice` + `worker` internal).
Answer any parameter prompts for values you didn't `azd env set` (empty / `0` keeps a feature off).

> **Deploy-time parameter gotcha.** `azd deploy`'s container-app manifest resolves parameters **only**
> from `.azure/<env>/config.json` under `infra.parameters`. Answering the `azd up` / `azd provision`
> prompt persists a value there. If a later `azd deploy <service>` fails with
> `parameter <Name> not found`, make sure **every** parameter (not just secrets) is present in
> `.azure/<env>/config.json` `infra.parameters`. JSON-valued secrets (the SA key) are best stored there
> as a typed string rather than via the `AZURE_*` env-var form. See
> [architecture.md](architecture.md) / repo notes for the full azd resolution rules.

### 5.2 Post-deploy: add the redirect URI

`azd up` prints the `webfrontend` URL. Go back to the OAuth client (step 2.4) and add
`https://<that-url>/signin-google` to **Authorized redirect URIs**, or sign-in will fail with
`redirect_uri_mismatch`.

### 5.3 (Optional) Enable WIF for Pub/Sub after provisioning

WIF needs the ACA **managed-identity object id**, which exists only after the first provision. Follow
[configuration.md → key-less auth](configuration.md#key-less-auth-with-workload-identity-federation-recommended),
then:

```powershell
$wif = Get-Content -Raw -Path ".\wif-credential.json"
azd env set GoogleChannelWorkloadIdentityCredentialJson "$wif"
azd up   # config update, not a re-provision
```

### 5.4 Redeploying code only

After the first full `azd up`, you can push code to individual services without re-provisioning:

```powershell
azd deploy apiservice
azd deploy worker
azd deploy webfrontend
```

---

## 6. Verify

1. Open the `webfrontend` URL and sign in with a Google account in the region's Workspace.
2. **Customers** / **Channel partners** should list the region's estate (live from the signed-in token).
3. If you enabled the read-model + background refresh, the **Home dashboard** fills in over the next few
   sync cycles (indirect estate, seats, estimated value). It reads from durable SQL, so it survives
   redeploys and only adds deltas — see [architecture.md](architecture.md) and
   [docs/todos/10-persistent-read-model.md](todos/10-persistent-read-model.md).
4. If you enabled Pub/Sub, register the topic from **Notifications**, then trigger a change (e.g. a
   license-assignment change) and watch the feed.

Diagnostics: the Aspire dashboard + Log Analytics workspace `azd` provisions carry logs/traces for all
three apps — see [deployment.md → Aspire dashboard in Azure](deployment.md#aspire-dashboard-in-azure).

---

## 7. Running a region locally (optional)

For local development against a region's credentials, use user-secrets (Web + API) and the AppHost
`Parameters:` section (Worker background/Pub/Sub), exactly as in
[configuration.md](configuration.md#configuration):

```powershell
dotnet user-secrets --project src/GChannel.Web set "Authentication:Google:ClientId" "<client-id>"
dotnet user-secrets --project src/GChannel.Web set "Authentication:Google:ClientSecret" "<client-secret>"
dotnet user-secrets --project src/GChannel.ApiService set "GoogleChannel:AccountId" "accounts/C0xxxxxxx"

$saKey = Get-Content -Raw -Path "C:\keys\gchannel-<region>-sa.json"
dotnet user-secrets --project src/GChannel.AppHost set "Parameters:GoogleChannelServiceAccountKeyJson" "$saKey"
dotnet user-secrets --project src/GChannel.AppHost set "Parameters:GoogleChannelImpersonateUser" "admin@<region-domain>"
dotnet user-secrets --project src/GChannel.AppHost set "Parameters:GoogleChannelBackgroundRefreshSeconds" "900"
```

Then `dotnet run --project src/GChannel.AppHost` (needs Docker for the local SQL + Redis containers).

---

## 8. Checklist

- [ ] Google Cloud project created; **Cloud Channel API** (and **Pub/Sub** if used) enabled.
- [ ] Reseller **account id** (`accounts/C0xxxxxxx`) recorded.
- [ ] **OAuth client** created; client id + secret recorded; local redirect URI added.
- [ ] **Service account** created; DWD enabled; **JSON key** downloaded; **Client ID** noted.
- [ ] DWD **authorized** in the Workspace Admin console for the `apps.order` scope; impersonation admin
      chosen.
- [ ] (Optional) Pub/Sub topic registered, subscription created, `pubsub.subscriber` granted; WIF
      configured.
- [ ] Azure: new **`azd env`** created; all parameters `azd env set`.
- [ ] `azd up` succeeded.
- [ ] **Deployed redirect URI** added to the OAuth client.
- [ ] Signed in; estate lists; dashboard/notifications verified.
