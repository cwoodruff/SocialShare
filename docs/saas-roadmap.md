# What it would take to sell this

Today this is a good single user tool with the tenancy already modelled. That is further along
than most side projects, and a long way from a product. Here is the honest list, roughly in the
order I would do it.

## Already done, and worth knowing

- Every owned row carries `UserId` and `OrganizationId`.
- The `DbContext` applies a global query filter on `UserId`, so a forgotten `Where` clause does
  not leak another user's rows.
- An `Organization` exists per user with a `Plan` of `Free` or `Pro`, and `FeatureGate` is the
  single place that answers whether a plan allows something.
- An integration test proves user A's posts, accounts and images are invisible to user B through
  real HTTP.
- An admin role and an `/admin` page exist behind a policy.
- Secrets are encrypted at rest and the app holds no platform credentials of its own.
- `IImageStore` abstracts where images live, and the connection string is configuration.

None of that is the hard part. It just means the hard part does not start with a migration of
every table.

## 1. Get off a single instance

This is the blocker for everything else, because right now one process going down is total
downtime and there is nowhere to put a second one.

- **Database.** Azure SQL Basic or Postgres Flexible Server. The connection string is already
  configuration. The one provider specific thing is the `DateTimeOffset` to UTC `DateTime`
  conversion from [ADR 0008](adr/0008-utc-datetime-storage.md), which exists because SQLite
  cannot order a `DateTimeOffset`. Both of those can, so the convention comes out, and the stored
  format changes, so it is a data migration not just a config change.
- **Images.** A `BlobImageStore` implementing `IImageStore`. One class and one DI registration,
  no page changes. That was the whole reason for the abstraction.
- **Data Protection key ring.** `PersistKeysToAzureBlobStorage` plus
  `ProtectKeysWithAzureKeyVault`, so every instance shares one ring and a cookie from one is
  readable by all.
- **The scheduler.** Already safe. The claim is a conditional `UPDATE`, so only one instance can
  win a post. What it needs is a cap on concurrent publishes per instance so a busy minute does
  not open three hundred sockets.

Estimate: a week, most of it the data migration and testing it.

## 2. Billing

- Stripe Checkout for the subscribe flow, Stripe Customer Portal for everything after. Do not
  build plan management screens.
- A webhook endpoint that moves `Organization.Plan` on `checkout.session.completed`,
  `customer.subscription.updated` and `customer.subscription.deleted`, with idempotency on the
  event id because Stripe retries.
- Decide what happens at the end of a failed payment. My instinct: stop the scheduler publishing
  for that organization and keep everything readable, rather than deleting anything.
- `FeatureGate` already exists. Today it gates a scheduled post ceiling. Give it real limits:
  number of connected accounts, scheduled posts in flight, posts per month, image storage.

Estimate: a week, and most of the risk is in the webhook being correct rather than the UI.

## 3. Multi tenant hardening

The query filters are good. They are not the whole job.

- **Rate limit per user, not per IP.** ASP.NET Core rate limiting, keyed on the user id, on the
  publish and upload endpoints in particular.
- **Quota the uploads.** Today a user can fill the disk. Cap total bytes per organization and
  refuse past it with a clear message.
- **Background work fairness.** One user with two hundred scheduled posts at noon should not
  starve everyone else. Round robin the claim query by user rather than taking the oldest 25.
- **Audit log.** Who connected what, who deleted what, when. `PublishLog` covers publishing.
  Nothing covers account and credential changes, and that is the part a customer will ask about.
- **Per organization encryption.** Right now one Data Protection purpose protects every user's
  secrets. Deriving a per organization protector means a bug that leaks one blob leaks one
  tenant, not all of them.
- **Delete means delete.** A real account deletion that removes rows, images and tokens, and
  revokes at the platform where the API allows it. GDPR makes this non optional the moment you
  have a European customer.

## 4. Abuse controls

The uncomfortable one. The moment anyone can sign up, this is a tool for posting to many
platforms at once on a schedule, which is also a description of a spam tool.

- Email confirmation is enforced, sign ups are closed by default, and SendGrid is wired up, so
  this one is mostly done. What is left is operational: a bounce and complaint webhook, so a
  repeatedly bouncing address gets flagged rather than silently failing forever.
- A hold on new accounts before they can publish, or a low first week limit.
- Content rate limits per organization, not just request rate limits.
- A kill switch: suspend an organization, stop its scheduler, keep the data.
- Somewhere for platforms to complain to, and a process for when they do. If a platform decides
  SocialShare is a spam vector, every user is affected.
- Reconsider [ADR 0007](adr/0007-user-supplied-platform-credentials.md). Per user developer apps
  are actually an abuse control: a spammer burns their own app, not yours. If the product moves
  to shared apps for convenience, it inherits that risk.

## 5. Per platform quota and failure handling

Today a failure gets three retries with backoff and then waits. That is fine for one person.

- Honour `Retry-After` instead of using a fixed backoff table.
- Treat 429 differently from 500. A rate limit should back off much further and should not burn
  an attempt.
- Track per platform quota where the platform exposes it, and warn before a user hits it rather
  than failing at publish time.
- Circuit break per platform. If LinkedIn is down, stop hammering it for every user and tell
  everyone at once.
- Dead letter anything that has exhausted its retries, so support can see it without a database
  query.

## 6. The product gaps

Things a paying customer asks for in the first week:

- Threads on the same platform, not just single posts.
- Video. Every one of the six supports it, none of it is implemented.
- Multiple images per post. The schema has one `ImageId` per target, so this is a migration.
- Preview per platform, showing what it will actually look like.
- A calendar view. The posts list is a table.
- Best time to post. Everybody asks.
- Analytics. Nobody needs it, everybody asks for it.
- Team features. The `Organization` is there, but there is no membership table, no roles, no
  invitations and no approval workflow.

## 7. The operational gaps in what already exists

Smaller, but they are real and I know about them:

- **Orphaned images.** Retention deletes old published posts but not the image files they
  referenced. Needs a sweep that deletes `StoredImage` rows and blobs with no remaining target.
- **No email change.** The settings page says so. Changing a sign in email needs a confirmation
  round trip on both addresses.
- **The LinkedIn API version is a constant.** It needs bumping about once a year and there is
  nothing that will remind me except a 400.
- **No structured telemetry.** Application Insights or OpenTelemetry, with the publish outcome
  as a metric, so "is publishing working" is a dashboard rather than a log grep.
- **No staging environment.** Deployments go straight to production. A slot swap would cost
  nothing extra on a B-tier plan above B1.

## What I would not do

- Rewrite the front end. htmx plus Razor partials scales fine to this, and the absence of a build
  step is worth more than anything a SPA would add. See [ADR 0002](adr/0002-htmx-over-spa.md).
- Split it into services. It is one process doing one job.
- Add a job framework before there is more than one kind of job.
