# Operations

Everything I need to know when this breaks or when I want to move it somewhere else.

## What the state actually is

Three things on disk under `/home/data`, all on the Azure Files share that survives deployments:

| Path | What | Lose it and |
|---|---|---|
| `/home/data/socialshare.db` | Everything. Identity, accounts, posts, image metadata, publish logs. | You start over. |
| `/home/data/uploads/` | The image bytes, in a two level fan out of random names. | Posts keep their metadata but the images 404. |
| `/home/data/keys/` | The Data Protection key ring. | Every connected account has to be reconnected, and everyone gets signed out. |

Locally the same three live under `src/SocialShare.Web/App_Data`, except the key ring, which in
development goes to the user profile because `DataProtection:KeyRingPath` is empty.

All three matter. A backup of the database without the key ring gives you a database full of
ciphertext you cannot decrypt.

## Creating the first account

Sign ups ship closed (`App:RegistrationEnabled` is `false`) and email confirmation ships required
(`App:RequireConfirmedAccount` is `true`). A brand new deployment therefore has nobody in it and
no sign up link anywhere, which is deliberate and is also a chicken and egg problem the first
time.

Open the door, walk through it, close it behind you.

```bash
RG=socialshare-rg
APP=socialshare-woody

# 1. Open sign ups. The app restarts, which takes a few seconds.
az webapp config appsettings set --name $APP --resource-group $RG --settings \
  App__RegistrationEnabled=true

# 2. Go to https://$APP.azurewebsites.net/register and create your account.

# 3. Find the confirmation link. With Email:Provider left at Log it is written to the
#    application log rather than sent, so tail the log and look for "Confirm your SocialShare
#    account". Open the link, then sign in.
az webapp log tail --name $APP --resource-group $RG

# 4. Close sign ups again.
az webapp config appsettings set --name $APP --resource-group $RG --settings \
  App__RegistrationEnabled=false

# 5. Make yourself an admin, if you have not already.
az webapp config appsettings set --name $APP --resource-group $RG --settings \
  App__AdminEmails__0="you@example.com"
```

Do the same thing again, briefly, each time you want to let somebody else in. There is no invite
system. That is a deliberate omission rather than an oversight: an invite flow is real work and
this has one user.

If you configure a real email provider, steps 2 and 3 collapse into "register and click the link
in your inbox" and you never touch the log.

### If you get locked out

You confirmed nothing, registration is closed, and you cannot sign in. Two ways back:

- **The resend page.** `/resend-confirmation` is reachable signed out and does not need
  registration to be open. Enter your address and a fresh link goes out, or to the log.
- **Confirm directly in the database.** Last resort, and it skips the check rather than passing
  it:
  ```sql
  UPDATE AspNetUsers SET EmailConfirmed = 1 WHERE Email = 'you@example.com';
  ```

## Backing up

### Manual, and good enough

```bash
RG=socialshare-rg
APP=socialshare-woody
STAMP=$(date +%Y%m%d-%H%M%S)

# Put the app into a quiet state first. SQLite will happily be copied mid write, and a
# mid write copy is how you get a backup that looks fine and restores broken.
az webapp stop --name $APP --resource-group $RG

az webapp deploy --name $APP --resource-group $RG --type zip --src-path /dev/null 2>/dev/null || true

# Pull the whole /home share down.
az webapp config backup list --resource-group $RG --webapp-name $APP

az webapp start --name $APP --resource-group $RG
```

In practice the simplest reliable route is the Kudu console. Browse to
`https://YOUR-APP.scm.azurewebsites.net/api/zip/home/data/` and it streams the whole `data`
folder as a zip. Do that with the site stopped, or after the daily quiet period, and store the
zip somewhere that is not Azure.

### Built in App Service backups

The B1 plan includes scheduled backups of the site and its `/home` content. Turn it on:

```bash
az webapp config backup update \
  --resource-group $RG \
  --webapp-name $APP \
  --container-url "<a blob container SAS URL>" \
  --frequency 1d \
  --retain-one true \
  --retention 30
```

That needs a storage account and a container SAS. It is the least effort option and it captures
all three folders together, which is the important part.

### What a restore looks like

1. Stop the app.
2. Replace `/home/data` wholesale from the backup. Do not merge: a database from one point in
   time with a key ring from another is worse than either.
3. Start the app. Migrations run on startup and are a no-op if the schema already matches.
4. Sign in and press Test connection on each account. That proves the key ring and the tokens
   came back together.

## Rotating Data Protection keys

Data Protection rolls its own key every ninety days by default and keeps the old ones, so a roll
is invisible: new data is encrypted with the new key, old data still decrypts with the old one.
You do not have to do anything.

You only need to act if you think the key ring leaked.

### If the key ring leaked

Assume every stored token is compromised, because it is. Rotating the key does not help on its
own: an attacker with the old key and a copy of the database already has the plaintext tokens.

1. Revoke the tokens at the source. That is the actual fix.
   - Bluesky: Settings, Privacy and security, App passwords, revoke the SocialShare one.
   - LinkedIn: your Page's app permissions, or just remove the app.
   - Mastodon: Preferences, Account, Authorized apps, revoke SocialShare.
   - X: Settings, Security and account access, Apps and sessions, revoke.
   - Threads and Instagram: Meta account settings, Apps and websites, remove.
2. Delete `/home/data/keys/` and restart, which generates a fresh ring.
3. In SocialShare, press Forget everything on each platform card, then set each one up again
   with new credentials.

Step 1 is the one that matters. Steps 2 and 3 stop the damage continuing.

### Moving the key ring to Key Vault

Described in [ADR 0004](adr/0004-data-protection-for-secrets.md). It is one call in `Program.cs`
plus a Key Vault, a key, and wrap/unwrap granted to the App Service managed identity.

## Reading logs

Turn on filesystem logging and tail it:

```bash
az webapp log config \
  --name $APP --resource-group $RG \
  --application-logging filesystem --level information

az webapp log tail --name $APP --resource-group $RG
```

Filesystem logging on App Service switches itself off after twelve hours. For anything ongoing,
wire up Application Insights or ship to a Log Analytics workspace.

### The log lines worth grepping for

| Line | Means |
|---|---|
| `Scheduler started, polling every 15 seconds.` | The worker came up. If you never see this, Always On is off or `Scheduler:Enabled` is false. |
| `Claimed scheduled post {PostId}.` | A post's time came and this process won the claim. |
| `Published post {PostId} to {Platform} as {RemotePostId}.` | It worked. |
| `Publishing post {PostId} to {Platform} failed on attempt {N}: ...` | It did not. The full platform response is in the message. |
| `Released {N} post(s) that were stuck in Publishing.` | Something died mid publish and the post is being tried again. |
| `Could not decrypt a stored secret.` | The key ring changed or was lost. Accounts need reconnecting. |
| `Database migration failed.` | The app refused to start rather than run against a half migrated database. |
| `The image root {Root} is not writable.` | The file share is read only or full. `/health` is failing too. |

Everything the platforms say comes through verbatim, status code and body. That is on purpose:
a platform error is far easier to search for exactly as written.

## Common failures

### Scheduled posts never publish

Check `/health` first, then look for `Scheduler started` in the log.

1. Is Always On on? `az webapp config show --name $APP --resource-group $RG --query alwaysOn`.
   If false, the site unloads when idle and the worker stops with it.
2. Is `Scheduler:Enabled` true?
3. Is the post actually `Scheduled` and not `Draft`? Saving a draft with a date set does not
   schedule it. The Schedule button does.
4. Is the scheduled time in the past in UTC? The dashboard shows your zone, the database stores
   UTC. Compare `ScheduledUtc` to `datetime('now')`.

### A post is stuck in Publishing

It will release itself after `Scheduler:StuckClaimMinutes`, fifteen by default, and be retried.
Any platform that already succeeded is skipped on the retry, because its target is `Published`.

If you want it back immediately:

```sql
UPDATE Posts SET Status = 'Scheduled', ClaimedUtc = NULL WHERE Id = 'the-guid';
```

### Everything suddenly needs reconnecting

The key ring changed. Almost always `DataProtection:KeyRingPath` is unset, so a fresh ring was
generated on restart. Set it to `/home/data/keys`, restart, and reconnect each account once. It
will not happen again.

### Image posts to Threads or Instagram fail, everything else is fine

Those two fetch the image from a URL rather than accepting an upload.

1. Is `App:PublicBaseUrl` set and correct, including the scheme?
2. Is the app reachable from the public internet? This never works from localhost.
3. Open `https://YOUR-HOST/i/<key>` yourself. If you cannot, Meta cannot.

### The database is locked

Two writers. Either the plan scaled past one instance, or a deployment ran two processes at once.
Scale back to one and set `WEBSITE_DISABLE_OVERLAPPED_RECYCLING=1`. See
[ADR 0001](adr/0001-sqlite-on-app-service.md).

### Out of disk

`/home` is one gigabyte on B1. Uploads are the only thing that grows without bound.

```sql
SELECT COUNT(*), SUM(ByteSize)/1024/1024 AS MB FROM StoredImages;
```

Turn on retention to prune old posts:

```
Retention__PurgeEnabled=true
Retention__PurgePublishedAfterDays=365
```

That deletes published posts older than the cutoff. Note that it does not currently delete the
image files those posts referenced, so orphaned uploads accumulate. That is a known gap and it is
in [saas-roadmap.md](saas-roadmap.md).

## Uploaded images are public

Uploads are served from `/i/{key}` with no authentication. That is not an oversight, it is a
requirement: Threads and Instagram fetch images by URL and will not accept an upload.

The mitigation is that the key is 16 random bytes, 128 bits, with no relationship to the user,
the post or the original file name, and there is no listing endpoint. Guessing one is not
feasible. But anyone who has the exact URL can open the image, forever, without signing in, and
that includes anyone the platform's own CDN shared it with.

So: do not upload anything through this app that would be a problem if it were public. It is
about to be public on a social network anyway, which is the point, but it is worth saying out
loud because the image becomes reachable the moment it is uploaded, not the moment it is
published.

If you only use Bluesky, LinkedIn, Mastodon and X, none of which need this, you could lock
`/i/{key}` behind authentication and nothing else would break.

## Health endpoint

`GET /health` returns `Healthy` or `Unhealthy` and checks:

- SQLite answers `SELECT 1`.
- The image root accepts a write and a delete.

Both must pass. It is wired to App Service health checks and polled by the deploy workflow.
