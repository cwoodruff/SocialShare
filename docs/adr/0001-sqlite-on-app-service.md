# 0001. SQLite on a single App Service instance

Status: Accepted

## Context

I need a database for Identity, my connected accounts, my posts, image metadata and a publish
log. This is a personal tool with one user to start with and maybe a handful later. Azure SQL is
about forty dollars a month at the smallest useful tier. Postgres Flexible Server is similar. The
whole app is a single B1 App Service instance costing about thirteen dollars a month.

## Decision

One SQLite file at `/home/data/socialshare.db`, through EF Core, with migrations run on startup.

`/home` on App Service is an Azure Files share mounted into the container. It survives
deployments, restarts and scale operations, which the app's own filesystem does not. Uploaded
images sit next to it at `/home/data/uploads`.

## Consequences

The good:

- Zero extra cost and zero extra infrastructure.
- Backups are a file copy.
- The same provider runs locally, in CI and in production, so nothing behaves differently in
  the one place I cannot debug easily.
- Local development needs no database server at all.

The bad:

- **This design requires exactly one instance.** SQLite over an SMB share with two writers is a
  corruption story, not a scaling story. App Service must stay at one instance, and
  `WEBSITE_DISABLE_OVERLAPPED_RECYCLING` must be set to `1` so a deployment does not briefly run
  two processes against the same file.
- Writes go over a network file share, so they are slower than a local disk. At this scale it
  does not matter.
- Scaling out means moving to Azure SQL or Postgres and moving uploads to Blob Storage.

I made that second point cheap on purpose. The connection string and the image root are both
configuration, and images go through `IImageStore`, so the move is a settings change plus one new
`IImageStore` implementation, not a rewrite. See [saas-roadmap.md](../saas-roadmap.md).

Two things I found the hard way and want recorded:

- The SQLite provider will not translate an ordering comparison on a `DateTimeOffset` column.
  See [ADR 0008](0008-utc-datetime-storage.md).
- `ExecuteUpdateAsync` cannot translate the tenant query filter, so the two places that use it
  call `IgnoreQueryFilters` and carry an explicit predicate instead.
