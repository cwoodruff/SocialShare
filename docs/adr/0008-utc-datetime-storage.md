# 0008. DateTimeOffset is stored as a UTC DateTime

Status: Accepted

## Context

Every timestamp in the app is UTC and the entities model them as `DateTimeOffset`, which reads
well and makes the intent obvious. The SQLite provider maps `DateTimeOffset` to text that carries
its offset, and it refuses to translate an ordering comparison on that column. Every query the
scheduler depends on is an ordering comparison:

```csharp
db.Posts.Where(p => p.Status == PostStatus.Scheduled && p.ScheduledUtc <= now)
```

That threw `InvalidOperationException` at runtime on the very first scheduler tick. Evaluating
it client side would mean pulling every post into memory on a fifteen second loop, which is not
an option.

## Decision

A model wide convention converts every `DateTimeOffset` and `DateTimeOffset?` to a UTC
`DateTime` on the way to the database and back:

```csharp
protected override void ConfigureConventions(ModelConfigurationBuilder builder)
{
    builder.Properties<DateTimeOffset>().HaveConversion<UtcDateTimeOffsetConverter>();
}
```

The entities keep `DateTimeOffset`. Nothing above the `DbContext` knows this happened.

## Consequences

The good:

- Comparisons translate, so the scheduler asks SQLite "what is due" instead of asking for
  everything and deciding in memory.
- SQLite orders the stored text correctly, because every value is UTC with no offset to confuse
  the comparison.
- One convention, no per property configuration, and no change to any entity.

The bad:

- The offset is discarded. That is fine here because everything stored is UTC by construction
  and the user's zone lives on their profile, but it would be wrong in an app that needed to
  remember the offset a value was written in.
- It has to be remembered when moving to another provider. Azure SQL and Postgres both handle
  `DateTimeOffset` natively, so the convention could be dropped, but the stored format would
  change and that needs a data migration rather than just deleting the file.

There are tests over the claim and the stuck claim release specifically so this cannot regress
silently. A provider that stops translating these queries fails the test suite rather than
failing at three in the morning.
