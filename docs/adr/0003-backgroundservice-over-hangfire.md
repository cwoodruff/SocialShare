# 0003. A BackgroundService instead of Hangfire, Quartz or Azure Functions

Status: Accepted

## Context

Scheduled posts have to publish while I am not signed in. That is the only background work in the
app. It runs at most a few times a day.

## Decision

A `BackgroundService` registered in the same process as the web app. It uses a `PeriodicTimer`
on a fifteen second interval and does four things per tick:

1. Release any post stuck in `Publishing` for longer than the stuck claim timeout.
2. Claim and publish anything `Scheduled` whose time has passed.
3. Retry any individual platform that failed and whose backoff has elapsed.
4. Purge old published posts, if purging is turned on. That one runs at most every six hours.

The claim is a single conditional `UPDATE`: move the post to `Publishing` only if it is still
`Scheduled`. One row updated means this caller owns it. Everything else backs off.

## Consequences

The good:

- No extra dependency, no dashboard to secure, no job store schema to migrate.
- The same code path serves Publish now and the scheduler, so there is one publishing
  implementation and one set of tests over it.
- It is drivable from a test. `RunOnceAsync` is public, so the scheduling behaviour is provable
  in milliseconds instead of waiting on a real timer.

The bad:

- **App Service must have Always On enabled.** Without it the site unloads after twenty minutes
  idle and nothing publishes until somebody visits. Always On needs a B1 plan or better. Free
  and Shared tiers will not run this.
- Polling the database every fifteen seconds forever is slightly wasteful. It is one indexed
  query against a tiny table, so it does not matter.
- Two instances would both poll. The claim makes that safe from a double post point of view, but
  SQLite makes two instances a non starter anyway. See [ADR 0001](0001-sqlite-on-app-service.md).
- A very long publish can outlive a deployment. The stuck claim release covers it: the post goes
  back to `Scheduled` and is tried again, and any platform that already succeeded is skipped
  because its target is already `Published`.

## What I would change if this grew

Hangfire once there is more than one kind of background job, or a real queue once there is more
than one instance. Neither is true today.
