# 0007. Every user brings their own developer app credentials

Status: Accepted

## Context

Five of the six platforms need an OAuth client id and secret belonging to a developer app.
The usual model is that the product registers one app and every user authorizes it. That means
the product holds the client secrets, the product's app is what gets rate limited, and the
product's app is what gets suspended if any user misbehaves.

## Decision

Each user enters their own credentials on the Connected accounts page. The app itself has no
platform credentials in configuration or in source. Each platform declares what it needs as a
list of `CredentialField` records, and the accounts page renders those generically, so the UI
never mentions a platform by name outside the display strings the platform itself supplies.

Mastodon is the special case in the other direction: there is no central registry, so the app
registers itself with whichever instance a user names and stores the generated credentials
against that user like any other.

## Consequences

The good:

- Nothing platform specific in configuration. A leaked `appsettings.json` leaks nothing.
- Rate limits, quotas and app review are per user, so one user cannot get everyone suspended.
- It avoids the app review treadmill entirely. Meta and LinkedIn both want a review before an
  app can act for users other than its own developers. Sidestepping that is the difference
  between this existing and not existing.
- Adding a platform is one class and one entry in DI. No UI change, because the forms are
  generated from the capabilities.

The bad:

- **Setup is real work for the user.** Registering a Meta app to post to Threads is not a two
  minute job. [platform-setup.md](../platform-setup.md) exists to make it survivable, and every
  form in the UI links to the right section of it.
- Bluesky is the only platform where somebody can be publishing in under a minute. That is why
  it is the one I point people at first and the one I use as the reference implementation.
- Each user also owns the redirect URI registration, so moving the app to a new hostname means
  every user updates their developer app.

## If this becomes a product

This would change. A hosted product would register one app per platform, go through review, and
keep per user credentials only for platforms that require it. That is in
[saas-roadmap.md](../saas-roadmap.md), and the shape of `SocialAccount` supports both: the
credential blob would simply be empty and the platform would fall back to configured values.
