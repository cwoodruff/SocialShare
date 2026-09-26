# Architecture

SocialShare is a modular monolith. One solution, one process, one SQLite file. That is not a
compromise I am apologising for, it is the right size for what this does.

## C4 level 1: context

```mermaid
C4Context
    title SocialShare, system context

    Person(woody, "Me", "Writes one post in six versions and publishes or schedules it")

    System(socialshare, "SocialShare", "ASP.NET Core Razor Pages app with a background scheduler")

    System_Ext(linkedin, "LinkedIn", "Versioned Posts and Images APIs")
    System_Ext(bluesky, "Bluesky", "AT Protocol XRPC on a PDS")
    System_Ext(mastodon, "Mastodon instance", "Per instance REST API")
    System_Ext(x, "X", "v2 API, paid tier required to post")
    System_Ext(threads, "Threads", "Meta Graph API")
    System_Ext(instagram, "Instagram", "Instagram Graph API via a Facebook Page")
    System_Ext(email, "Email provider", "Account confirmation and password reset")

    Rel(woody, socialshare, "Writes, schedules and reviews posts", "HTTPS")
    Rel(socialshare, linkedin, "Publishes, uploads images", "HTTPS")
    Rel(socialshare, bluesky, "Publishes, uploads blobs", "HTTPS")
    Rel(socialshare, mastodon, "Registers itself, publishes", "HTTPS")
    Rel(socialshare, x, "Publishes, chunked media upload", "HTTPS")
    Rel(socialshare, threads, "Creates and publishes containers", "HTTPS")
    Rel(socialshare, instagram, "Creates and publishes containers", "HTTPS")
    Rel(threads, socialshare, "Fetches the image by URL", "HTTPS")
    Rel(instagram, socialshare, "Fetches the image by URL", "HTTPS")
    Rel(socialshare, email, "Sends account mail", "HTTPS")
```

The two arrows pointing back at SocialShare are the reason uploaded images are served from a
public address. Meta does not accept an upload for these two endpoints, it fetches the image
itself. See [operations.md](operations.md) for the privacy tradeoff that creates.

## C4 level 2: containers

```mermaid
C4Container
    title SocialShare, containers

    Person(woody, "Me")

    Container_Boundary(app, "Azure App Service, Linux, single instance") {
        Container(web, "SocialShare.Web", "ASP.NET Core Razor Pages, htmx", "Serves every page, handles OAuth callbacks, serves uploaded images")
        Container(worker, "Scheduler", "BackgroundService in the same process", "Polls for due posts, claims them, publishes, retries failures")
        ContainerDb(db, "socialshare.db", "SQLite on /home/data", "Identity, organizations, accounts, posts, image metadata, publish logs")
        Container(files, "Uploads", "Files on /home/data/uploads", "The image bytes")
        Container(keys, "Data Protection key ring", "Files on /home/data/keys", "Encrypts every stored token and app password")
    }

    System_Ext(platforms, "Six social platforms")

    Rel(woody, web, "HTTPS")
    Rel(web, db, "EF Core")
    Rel(worker, db, "EF Core")
    Rel(web, files, "IImageStore")
    Rel(worker, files, "IImageStore")
    Rel(web, keys, "Protect and unprotect")
    Rel(worker, keys, "Unprotect")
    Rel(web, platforms, "Connect, test, publish now")
    Rel(worker, platforms, "Publish on schedule, retry")
    Rel(platforms, web, "Fetch image by URL")
```

The worker is not a separate container. It is a `BackgroundService` in the same process, which
is why the App Service plan needs Always On.

## Projects and why they are split that way

```
src/SocialShare.Core/       Entities, enums, the DbContext, ISocialPlatform, services, the scheduler
src/SocialShare.Platforms/  One folder and one class per platform, plus shared HTTP plumbing
src/SocialShare.Data/       Migrations, the disk image store, Data Protection secret encryption
src/SocialShare.Web/        Razor Pages, htmx, CSS, Identity pages, health checks, the image endpoint
tests/SocialShare.Tests/    Unit and integration tests
```

The reference split in the brief put the `DbContext` in `SocialShare.Data`. I moved it to
`SocialShare.Core` and left migrations in `SocialShare.Data`, because the services and the
scheduler live in Core and they talk to the database directly. With the `DbContext` in Data, Core
would have had to depend on Data or I would have had to invent an interface over EF just to break
the cycle. That interface would be a repository in a hat, and the brief says no repository over
EF. So Core owns the model and the context, Data owns migrations and the two infrastructure
implementations that need external packages. That is recorded in
[ADR 0006](adr/0006-dbcontext-in-core.md).

`SocialShare.Platforms` references only Core, so a platform implementation can never reach the
database. It is handed the decrypted credentials and tokens, the body, and an image. It hands
back a result. That is the whole contract.

## How a publish actually flows

```mermaid
sequenceDiagram
    autonumber
    participant W as Scheduler
    participant DB as SQLite
    participant P as PublishingService
    participant S as ISocialPlatform
    participant X as The platform

    W->>DB: UPDATE posts SET status='Publishing' WHERE id=? AND status='Scheduled'
    Note over W,DB: One row updated means this worker owns the post
    W->>P: PublishClaimedPostAsync(postId)
    loop each enabled platform
        P->>DB: load the account, decrypt credentials and tokens
        P->>S: RefreshAsync, if the token is near expiry
        S->>X: refresh
        P->>S: PublishAsync(body, image)
        S->>X: upload the image, then create the post
        X-->>S: remote id and URL, or an error
        S-->>P: PlatformPublishResult
        P->>DB: record the target result and a publish log row
    end
    P->>DB: roll the target results up into the post status
```

The claim is the whole reason a restart in the middle of a send cannot double post. It is a
single conditional `UPDATE`, so only one caller can ever win it. Anything left in `Publishing`
for longer than `Scheduler:StuckClaimMinutes` is released on a later tick and tried again.

## Tenancy

Every owned row carries a `UserId` and an `OrganizationId`. The `DbContext` applies a global
query filter on `UserId` driven by a scoped `TenantContext`, which request middleware sets from
the signed in user. The background worker explicitly puts the context into system mode, which is
the only way past the filter, along with the admin page and a handful of deliberate
`IgnoreQueryFilters` calls that are each commented.

Every user gets an `Organization` on registration, with a `Plan` of `Free` or `Pro`. Team
features are not built. The schema is shaped for them so adding them later is additive rather
than a migration of every table.

## What is deliberately not here

- No microservices, no message bus, no CQRS, no MediatR.
- No repository or unit of work over EF Core. Pages query the `DbContext`.
- No SPA framework and no build step. htmx plus Razor partials.
- No job scheduler library. A `BackgroundService` and a `PeriodicTimer`.
- No imaging library. A hand written magic byte reader, because all it has to do is tell a real
  JPEG, PNG or WebP from something renamed to look like one, and read the dimensions.
