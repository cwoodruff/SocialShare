# Testing

```
dotnet test
```

92 tests, about five seconds. No database server, no network, no fixtures to install.

## What is covered

| Area | File | Count |
|---|---|---|
| Bluesky, the reference platform | `BlueskyPlatformTests.cs` | 9 |
| LinkedIn, Mastodon, X, Threads, Instagram | `OAuthPlatformTests.cs` | 21 |
| Image sniffing, rich text, time zones, feature flags | `CoreHelperTests.cs` | 23 |
| The publisher | `PublishingServiceTests.cs` | 10 |
| The scheduler | `SchedulerBackgroundServiceTests.cs` | 5 |
| The atomic claim on SQLite | `SchedulerClaimTests.cs` | 2 |
| Tenant isolation, end to end | `TenantIsolationTests.cs` | 2 |
| Registration and email confirmation gates | `AccountGateTests.cs` | 5 |
| The SendGrid sender and provider selection | `EmailSenderTests.cs` | 15 |

## How the platforms are tested

With a hand written `HttpMessageHandler`, not a mocking framework.

That is a deliberate choice, and the justification is one sentence: all these tests need is a
canned response matched on the URL plus a record of what was sent, and writing that by hand is
about sixty lines and reads better than the equivalent setup calls.

`StubHttpMessageHandler` takes rules like "when the URL contains `createRecord`, return this
JSON" and records every request that goes through it, so a test can assert on the body, the
`Authorization` header and the content type:

```csharp
var stub = new StubHttpMessageHandler()
    .RespondJson("uploadBlob", """{"blob":{"$type":"blob","ref":{"$link":"bafkreiabc"}}}""")
    .RespondJson("createRecord", """{"uri":"at://did:plc:abc/app.bsky.feed.post/withimage"}""");

var result = await Platform(stub).PublishAsync(request, credentials, tokens, CancellationToken.None);

var upload = stub.Requests.First(r => r.UriEndsWith("com.atproto.repo.uploadBlob"));
Assert.Equal("Bearer access", upload.Headers.Authorization);
```

Per platform this covers the OAuth authorization URL and its scopes, the token exchange, the
refresh path, a read only connection test, publishing with and without an image, and at least one
real failure with the platform's own status code and body.

The things worth calling out because they are easy to get quietly wrong:

- **Bluesky facets.** The test asserts the exact UTF-8 byte offsets for a link and a mention, not
  just that facets exist. Character offsets would pass a naive test and produce a mangled post.
- **Mastodon dynamic registration.** One test proves a new instance gets registered and the
  generated client credentials are handed back for storage, another proves a known instance is
  not registered a second time.
- **X PKCE.** The authorization URL carries the challenge and `S256`, and completing the flow
  without a verifier fails loudly rather than trying anyway.
- **Instagram without an image** is refused before any HTTP call is made. The test asserts the
  stub saw zero requests.
- **Threads and Instagram image posts** are asserted to send a public URL, not bytes.

## How the scheduler is tested

The worker exposes `RunOnceAsync`, so tests drive a tick at a time instead of waiting on a timer.
The "publishes two minutes in the future" case from the brief is covered by scheduling two
minutes out, asserting nothing happens, then moving the post's timestamp back and asserting it
publishes. That tests the behaviour in milliseconds rather than making the suite take two
minutes, and it tests it more precisely, because it also proves the post was left alone first.

Also covered: two ticks over the same post publish it once, a post abandoned mid publish by a
restart is released and retried, and a failed platform is only retried once its backoff has
elapsed.

## How tenant isolation is tested

`TenantIsolationTests` boots the real application through `WebApplicationFactory` against a
throwaway SQLite file, with the scheduler off and the image store in memory. Everything else runs
exactly as it does in production, including Identity, the cookie, antiforgery, the query filters
and the authorization policies.

Two real browser sessions register, connect a Bluesky account, upload an image and save a post.
Then it proves that user B:

- sees only their own post in the list,
- gets a 404 on user A's post detail and edit pages,
- cannot delete user A's post, and that the post is still in the database afterwards,
- sees only their own handle on the accounts page,
- never gets a reference to user A's uploaded image,
- and gets bounced from `/admin`.

A second test walks every authenticated route signed out and asserts a redirect to sign in.

## How the sign up gates are tested

`AccountGateTests` uses the same real host, with `App:RegistrationEnabled` and
`App:RequireConfirmedAccount` set per test rather than inherited, because both ship on and a
configuration flag that is read in the wrong place fails silently.

It proves that with registration closed the page says so, a form post straight at the handler
still creates nothing, and no page offers a sign up link. It proves that an unconfirmed account
registers but cannot sign in, is told why, is pointed at the resend page, and is still shut out
of `/app`. It proves that confirming the email lets that same account straight in.

The last one is the one I would not have thought to write: the resend page has to answer
identically whether or not the address belongs to an account, otherwise it is an account
enumeration oracle. The test compares the two whole rendered pages with the per request
antiforgery tokens stripped out, so any future divergence fails rather than quietly leaking.

This is the test that has to keep passing if this ever becomes a product, so it goes through real
HTTP rather than calling page models directly. A page model test would pass even if the
authorization convention were removed.

## What is not covered, and why

- **No real network calls.** Nothing in the suite talks to a social platform. Credentials for six
  platforms in CI would be a liability, and the tests would fail for reasons that have nothing to
  do with the code.
- **No browser automation.** There is no Playwright suite. The htmx interactions are each one
  handler returning one partial, and those are covered by the integration tests asserting on the
  returned HTML. A browser suite would be more machinery than this app justifies.
- **No load testing.** Single user, single instance.
- **The five non Bluesky platforms have never published a real post from this code.** Their
  request shapes are built from the current developer documentation, with the doc URL in a
  comment above each call, and asserted against in tests. But mocked HTTP proves the shape I
  believe is right, not that the platform agrees. Bluesky is the reference platform precisely
  because it is the one I could verify end to end without a paid tier or an app review.

On that last point, I did verify the full Bluesky path against a local AT Protocol stub
server during development: connect, test, schedule, the worker claiming and publishing, a blob
upload, computed facets, and a manual retry after a failure. That is not in the committed suite
because it needs a second process, but it is how the reference platform was proven.

## How the email sender is tested

`SendGridClient` takes an `HttpClient`, so `SendGridEmailSenderTests` puts the same stub handler
underneath it and reads the exact JSON SendGrid would have received. No network, no API key.

It asserts the things that are invisible until they are wrong in production: that both a plain
text and an HTML part are sent and in that order, that the from address and name are the
configured ones, that the reply-to appears only when configured, that sandbox mode reaches the
payload, and that click and open tracking are off. That last one matters more than it looks.
With click tracking on, SendGrid rewrites the confirmation URL into a redirect through its own
domain, which reads as phishing to a recipient and to a spam filter, and there is no way to
notice from inside the app.

It also asserts both failure shapes: a refused message and a dead socket each raise
`EmailSendException` carrying what actually went wrong, rather than returning quietly.

`EmailOptionsTests` covers provider selection, including that an unrecognised provider name
throws instead of falling back to the logger. That fallback used to be the behaviour, and with
email confirmation required it meant a typo in configuration turned into nobody being able to
register, with no error anywhere.

## Running a subset

```bash
dotnet test --filter "BlueskyPlatformTests"
dotnet test --filter "FullyQualifiedName~TenantIsolation"
dotnet test --filter "Scheduler"
```

## In CI

`.github/workflows/build-test-deploy.yml` runs restore, build and test on every pull request, and
again on a push to `main` before it publishes. The deploy job is skipped until the
`AZURE_WEBAPP_NAME` variable exists, so the workflow stays green before Azure is set up. The build has `TreatWarningsAsErrors` on, so a
warning fails the run. Test results are uploaded as a `.trx` artifact either way.
