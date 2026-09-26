# Deploying to Azure

One Linux App Service, one instance, deployed from GitHub Actions with OpenID Connect. About
thirteen dollars a month for the B1 plan and nothing else.

Everything below uses Central US and a B1 plan, which is what I documented for. Change the
region if you want, but do not drop below B1: Always On is not available on Free or Shared, and
without Always On the scheduler does not run.

## What you are creating

| Thing | Why |
|---|---|
| Resource group | Holds everything so you can delete it in one go. |
| App Service plan, Linux, B1 | Always On needs B1 or better. |
| Web App, .NET 10 | The app. |
| Entra ID app registration | The identity GitHub Actions assumes. |
| Federated credential | The trust between GitHub and that identity. |
| Role assignment | What that identity is allowed to do. |

No database, no storage account, no Key Vault. The SQLite file and the uploads live on the
`/home` share that comes with the App Service.

## 1. Create the App Service

```bash
RG=socialshare-rg
LOCATION=centralus
PLAN=socialshare-plan
APP=socialshare-woody          # has to be globally unique

az group create --name $RG --location $LOCATION

az appservice plan create \
  --name $PLAN \
  --resource-group $RG \
  --location $LOCATION \
  --is-linux \
  --sku B1

az webapp create \
  --name $APP \
  --resource-group $RG \
  --plan $PLAN \
  --runtime "DOTNETCORE:10.0"
```

## 2. Turn on Always On and stop overlapped recycling

```bash
az webapp config set \
  --name $APP \
  --resource-group $RG \
  --always-on true \
  --http20-enabled true \
  --min-tls-version 1.2
```

Always On stops App Service unloading the site after twenty minutes idle. Without it the
`BackgroundService` stops with the process and nothing publishes until somebody visits the site.
That is the single most important setting on this page.

Then stop App Service briefly running two processes during a deployment, which two SQLite writers
would not survive:

```bash
az webapp config appsettings set --name $APP --resource-group $RG --settings \
  WEBSITE_DISABLE_OVERLAPPED_RECYCLING=1
```

## 3. Application settings

```bash
az webapp config appsettings set --name $APP --resource-group $RG --settings \
  ASPNETCORE_ENVIRONMENT=Production \
  ConnectionStrings__Default="Data Source=/home/data/socialshare.db" \
  Storage__ImageRoot="/home/data/uploads" \
  DataProtection__KeyRingPath="/home/data/keys" \
  App__PublicBaseUrl="https://$APP.azurewebsites.net" \
  App__RegistrationEnabled=true \
  App__RequireConfirmedAccount=false \
  App__AdminEmails__0="you@example.com" \
  Scheduler__Enabled=true \
  Scheduler__PollSeconds=15
```

Double underscores are how App Service expresses nested configuration keys. `App__AdminEmails__0`
is the first element of an array.

Three of those matter more than the rest:

- **`DataProtection__KeyRingPath`.** Without it the key ring is generated fresh on every restart
  and every connected account has to be reconnected after every deployment. Pointing it at
  `/home` is what makes the encryption survive.
- **`App__PublicBaseUrl`.** Threads and Instagram fetch images from a URL rather than accepting
  an upload. If this is wrong or empty, image posts to those two fail.
- **`ConnectionStrings__Default`.** It must be under `/home`. The container filesystem is reset
  on every deployment.

If you want real email:

```bash
az webapp config appsettings set --name $APP --resource-group $RG --settings \
  Email__Provider=SendGrid \
  Email__SendGridApiKey="SG.xxxx" \
  Email__FromAddress="no-reply@yourdomain.com" \
  Email__FromName="SocialShare"
```

Leave `Email__Provider` at `Log` and confirmation links are written to the application log
instead, which is fine for a single user.

## 4. Health check

```bash
az webapp config set \
  --name $APP \
  --resource-group $RG \
  --generic-configurations '{"healthCheckPath": "/health"}'
```

`/health` checks two things: that SQLite answers a query, and that the image root is actually
writable, by writing and deleting a probe file. Both have to pass. A file share that has silently
gone read only is exactly the failure this catches.

With one instance, App Service health checks cannot replace the instance, so this mostly gives
you an alertable signal rather than automatic recovery. The deploy workflow polls it too, so a
deployment that starts but cannot reach its own database fails the build rather than sitting
there broken.

## 5. Set up OpenID Connect deployment

No publish profile. GitHub gets a short lived token each run.

### Create the app registration

```bash
SUB=$(az account show --query id -o tsv)
TENANT=$(az account show --query tenantId -o tsv)

APP_ID=$(az ad app create --display-name "socialshare-github-deploy" --query appId -o tsv)
az ad sp create --id $APP_ID

echo "AZURE_CLIENT_ID       $APP_ID"
echo "AZURE_TENANT_ID       $TENANT"
echo "AZURE_SUBSCRIPTION_ID $SUB"
```

### Add the federated credential

The subject has to match the workflow exactly. The deploy job sets `environment: production`, so
the subject ends in `environment:production`. Change `cwoodruff/SocialShare` to your repository.

```bash
az ad app federated-credential create --id $APP_ID --parameters '{
  "name": "github-main",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:cwoodruff/SocialShare:environment:production",
  "audiences": ["api://AzureADTokenExchange"]
}'
```

If you also want pull request builds to be able to sign in, add a second credential with subject
`repo:cwoodruff/SocialShare:pull_request`. The workflow as written does not need it, because only
the deploy job signs in to Azure.

### Grant it the right to deploy

```bash
az role assignment create \
  --assignee $APP_ID \
  --role "Website Contributor" \
  --scope "/subscriptions/$SUB/resourceGroups/$RG/providers/Microsoft.Web/sites/$APP"
```

Website Contributor on the one site, not Contributor on the subscription. It can deploy this app
and nothing else.

## 6. GitHub secrets and variables

In the repository, Settings, Secrets and variables, Actions.

**Secrets:**

| Name | Value |
|---|---|
| `AZURE_CLIENT_ID` | the `$APP_ID` printed above |
| `AZURE_TENANT_ID` | the `$TENANT` printed above |
| `AZURE_SUBSCRIPTION_ID` | the `$SUB` printed above |

**Variables:**

| Name | Value |
|---|---|
| `AZURE_WEBAPP_NAME` | the `$APP` name, for example `socialshare-woody` |

None of those three secrets is a credential. They are identifiers. The credential is the OIDC
token GitHub mints per run, which lives for minutes.

Then create an environment called `production` under Settings, Environments. The federated
credential subject refers to it, so the job will not authenticate without it. This is also where
you can add a required reviewer if you want deployments to pause for approval.

## 7. Register your redirect URIs

Once the app has a hostname, every platform's developer app needs the matching redirect URI:

```
https://socialshare-woody.azurewebsites.net/app/accounts/callback/LinkedIn
https://socialshare-woody.azurewebsites.net/app/accounts/callback/Mastodon
https://socialshare-woody.azurewebsites.net/app/accounts/callback/X
https://socialshare-woody.azurewebsites.net/app/accounts/callback/Threads
https://socialshare-woody.azurewebsites.net/app/accounts/callback/Instagram
```

Each platform card in the app shows the exact string for the host it is running on, under
"What this platform expects".

If you put a custom domain in front, update `App__PublicBaseUrl` and re-register every redirect
URI, or the OAuth flows break with a mismatch.

## 8. Deploy

Push to `main`. The workflow restores, builds, tests, publishes and deploys, then polls
`/health` until it answers 200 or gives up after five minutes.

Pull requests run restore, build and test only. They never touch Azure.

## The single instance limitation

**Do not scale this out.** The App Service plan must stay at one instance.

SQLite is one file. `/home` is an SMB file share. Two instances writing to one SQLite file over
SMB is a corruption story. Both the database and the uploaded images live there, and the Data
Protection key ring does too.

If you need to scale out, three things change:

1. Move the database to Azure SQL or Postgres. The connection string is configuration, and the
   only provider specific thing in the code is the `DateTimeOffset` conversion described in
   [ADR 0008](adr/0008-utc-datetime-storage.md).
2. Move uploads to Blob Storage. Write a `BlobImageStore` implementing `IImageStore` and change
   one DI registration. No page changes.
3. Move the Data Protection key ring to Blob Storage with `PersistKeysToAzureBlobStorage`, so
   every instance shares one ring and a cookie issued by one is readable by all.

The scheduler is already safe for multiple instances: the claim is a conditional `UPDATE`, so
only one instance can win a given post. That part needs no change.

[saas-roadmap.md](saas-roadmap.md) has the rest of what a real product would need.

## Rough cost

| Item | Monthly |
|---|---|
| App Service plan B1, Linux, Central US | about 13 USD |
| Everything else | 0 |

Moving to Azure SQL Basic adds about 5 USD, Blob Storage adds pennies at this volume, and a
Key Vault adds about 3 USD once you have a few thousand operations.
