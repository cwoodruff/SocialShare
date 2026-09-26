# 0006. The DbContext lives in Core, migrations live in Data

Status: Accepted

## Context

The suggested layout in the brief put the `DbContext` in `SocialShare.Data` and the domain
services and the scheduler in `SocialShare.Core`. Those services query and write the database
directly, which is the whole point of not having a repository layer. That makes Core depend on
Data, and Data already depends on Core for the entities. That is a cycle.

The usual ways out are all worse:

- Put an interface over the `DbContext` in Core. That is a repository wearing a different hat,
  and the brief says no repository over EF.
- Move the services into Data. Then Data holds the domain logic and Core holds only types, which
  makes the names lie.
- Merge Core and Data. Reasonable, but then migrations and the web project's design time tooling
  live in the same assembly as the domain, and the EF tooling dependency spreads.

## Decision

`SocialShare.Core` owns the entities, the enums, the `DbContext`, `ISocialPlatform`, the
services and the scheduler. `SocialShare.Data` owns the migrations, the design time context
factory, the disk `IImageStore` and the Data Protection `ISecretProtector`. Data references Core.

`UseSqlite` is configured with `MigrationsAssembly("SocialShare.Data")`, which is the supported
way to keep migrations out of the assembly that holds the context.

## Consequences

The good:

- No cycle, no interface invented purely to break one.
- Core's description in the brief stays exactly true: entities, `ISocialPlatform`, services,
  scheduling.
- Data holds the two things that pull in external infrastructure packages, so Core stays close
  to plain EF plus Identity.

The bad:

- `dotnet ef` needs both projects named, which is easy to forget:
  ```
  dotnet ef migrations add Name -p src/SocialShare.Data -s src/SocialShare.Web
  ```
- `SocialShare.Data` is a thin project. If it ever stops earning its keep, folding it into Core
  is a small change.
