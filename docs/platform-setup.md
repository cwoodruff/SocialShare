# Platform setup

One section per platform. Each one tells you what to register, what to paste into which field on
the Connected accounts page, and what will bite you. Every form in the app links straight to the
section it needs.

If you only want to see the app work, start with [Bluesky](#bluesky). It takes about a minute and
needs nothing registered.

## Why no passwords are stored

I originally described this as "store my username and password for each platform". I did not
build that, and it is worth saying why.

None of these six platforms lets you post by sending a username and password to a supported API.
They all use OAuth, where you approve an app in your browser and the app receives a token that
can do one specific thing on your behalf. That is not a hoop to jump through, it is the whole
point: the token is scoped, it can be revoked from your account settings without changing your
password, and it never gives the app the ability to read your email, change your password or lock
you out.

Bluesky is the one exception, and even there it is not your account password. Bluesky issues
**app passwords**: separate strings you generate per application, which cannot change your
account settings and which you can revoke individually. That is the supported way in for the AT
Protocol, so that is what SocialShare stores.

Storing a real account password for a third party service would mean holding something that can
take over an account, cannot be scoped, and cannot be revoked without a password change. If I
ever sold this, that would be the first thing an acquirer's security review killed. So the app
stores tokens and app passwords, each encrypted with ASP.NET Core Data Protection before it
reaches the database, and nothing else.

## The redirect URI

Five of the six use a browser redirect. The URI SocialShare uses is always the same shape:

```
https://YOUR-HOST/app/accounts/callback/PLATFORM
```

For example, running locally:

```
https://localhost:7204/app/accounts/callback/LinkedIn
https://localhost:7204/app/accounts/callback/Mastodon
https://localhost:7204/app/accounts/callback/X
https://localhost:7204/app/accounts/callback/Threads
https://localhost:7204/app/accounts/callback/Instagram
```

The exact string for your host is shown on each platform card, under "What this platform
expects". Copy it from there rather than typing it, because these have to match character for
character including the scheme and any trailing slash.

---

## Bluesky

**What you need:** your handle and an app password. Nothing to register.

1. Open Bluesky, go to Settings, then Privacy and security, then App passwords.
2. Add an app password. Name it SocialShare. Copy the value it shows you, it is only shown once.
3. In SocialShare, open Accounts and find Bluesky.
4. Fill in the form:
   - **Handle**: your full handle, for example `woodruff.dev` or `name.bsky.social`. No leading `@`.
   - **App password**: the value from step 2. This is not your account password.
   - **PDS host**: leave it as `https://bsky.social` unless you self host your data server.
5. Save, then Connect. The card should show Connected and your handle.

**What to know:**

- 300 character limit.
- Images are uploaded as blobs before the post is created. SocialShare does that for you.
- Links and mentions need facets, which are byte offsets into the text. SocialShare computes
  them on the server, so a URL in your body becomes a real link and `@someone.bsky.social`
  becomes a real mention. A handle that does not resolve stays as plain text rather than failing
  the post.
- Sessions expire. SocialShare refreshes them, and if the refresh token has also expired it
  quietly opens a new session from your stored app password.

---

## LinkedIn

**What you need:** a LinkedIn developer app with two products added.

1. Go to <https://www.linkedin.com/developers/apps> and create an app. It has to be associated
   with a LinkedIn Page you administer. If you do not have one, create a Page first.
2. On the Products tab, request both:
   - **Sign In with LinkedIn using OpenID Connect**
   - **Share on LinkedIn**

   Both are usually granted immediately.
3. On the Auth tab, add your redirect URI under Authorized redirect URLs for your app:
   `https://YOUR-HOST/app/accounts/callback/LinkedIn`
4. Copy the Client ID and Client Secret from the Auth tab.
5. In SocialShare, open Accounts and find LinkedIn.
   - **Client id**: the Client ID from step 4.
   - **Client secret**: the Client Secret from step 4.
6. Save, then Connect. You will be sent to LinkedIn to approve, then back.

**Scopes SocialShare requests:** `openid`, `profile`, `w_member_social`.

`w_member_social` is the one that lets it post. `openid` and `profile` are how it reads your
member id, which the Posts API needs as the author. It never asks for read access to your feed.

**What to know:**

- 3,000 character limit for a member post.
- An image post is two steps: initialize an upload to get an upload URL and an image URN, PUT
  the bytes, then reference the URN in the post. SocialShare does all three.
- The versioned APIs require a `LinkedIn-Version` header in `YYYYMM` form. It is a constant in
  `LinkedInPlatform.cs` and LinkedIn sunsets versions about a year out, so it needs bumping
  roughly annually. There is a comment on it saying so.
- Refresh tokens are only issued to approved partners. Most apps get a sixty day access token
  and no refresh token. When it expires, SocialShare says so in plain words and you press
  Connect again.

---

## Mastodon

**What you need:** the URL of your instance. That is all.

There is no central developer portal for Mastodon, so SocialShare registers itself with your
instance the first time you connect, and stores the client id and secret that instance generates.

1. In SocialShare, open Accounts and find Mastodon.
   - **Instance URL**: the server your account is on, for example `https://hachyderm.io` or
     `https://mastodon.social`. Include the scheme, no trailing path.
2. Save, then Connect. SocialShare registers itself with that instance, sends you there to
   approve, and comes back.

**Scopes SocialShare requests:** `read:accounts`, `write:statuses`, `write:media`.

`read:accounts` is only used to confirm which account the token belongs to, which is what the
Test connection button calls.

**What to know:**

- The character limit is per instance. The default is 500 but plenty of instances differ.
  SocialShare reads the real number from the instance API when you connect and counts against
  that, so the compose screen shows your instance's actual limit.
- Media upload can return a 202, meaning the instance is still processing the file. SocialShare
  polls until it is ready before attaching it.
- Every status is sent with an idempotency key derived from the account and the body, so a retry
  after a lost response cannot post the same thing twice.
- Access tokens do not expire on their own. If you revoke SocialShare from your instance's
  authorized apps list, reconnect to get a new one.
- If you move instances, change the Instance URL and save. SocialShare notices the change,
  discards the tokens tied to the old instance, and asks you to connect again.

---

## X

**What you need:** an X developer project and app, on a paid tier.

**Read this first:** posting through the X API requires a paid tier. On the free tier you can
register an app, complete the OAuth flow, and see SocialShare report Connected, and then every
publish comes back as a 403. That is not a bug in SocialShare and there is no way around it from
the application side. The app says so on the platform card, on the compose screen, and in the
error text when it happens.

1. Go to <https://developer.x.com/en/portal/dashboard> and create a project and an app.
2. In the app's User authentication settings:
   - Turn on OAuth 2.0.
   - App permissions: **Read and write**.
   - Type of App: **Web App, Automated App or Bot** if you want a confidential client, or
     **Public client** if you do not want to deal with a secret.
   - Callback URI: `https://YOUR-HOST/app/accounts/callback/X`
   - Website URL: anything valid.
3. Copy the OAuth 2.0 Client ID. Copy the Client Secret too if you chose a confidential client.
4. In SocialShare, open Accounts and find X.
   - **Client id**: the OAuth 2.0 Client ID.
   - **Client secret**: the Client Secret, or leave it blank for a public client.
5. Save, then Connect.

**Scopes SocialShare requests:** `tweet.read`, `tweet.write`, `users.read`, `offline.access`,
`media.write`.

`offline.access` is what gets you a refresh token. Without it the access token expires in two
hours and you reconnect constantly.

**What to know:**

- 280 characters for a standard account.
- SocialShare uses OAuth 2.0 with PKCE. The code verifier travels in a short lived encrypted
  cookie between starting the connect and the callback coming back, so a connect that sits for
  more than fifteen minutes has to be started again.
- Media upload is the chunked v2 flow: initialize, append each segment of up to four megabytes,
  finalize, then reference the media id in the post.
- A 403 on publish gets a note appended saying it usually means the free tier.

---

## Threads

**What you need:** a Meta developer app with the Threads API product.

1. Go to <https://developers.facebook.com/apps> and create an app.
2. Add the **Threads API** product (sometimes listed as "Threads" or "Use cases: Access the
   Threads API").
3. Under the Threads API settings, add your redirect URI as a Redirect Callback URL:
   `https://YOUR-HOST/app/accounts/callback/Threads`
4. Add yourself as a Threads tester on the app, and accept the invite from your Threads account
   under Settings, Website permissions, Invites. Until you do this the authorize step will refuse
   your account.
5. Copy the Threads App ID and Threads App Secret from the same settings page. These are not
   always the same as the app's top level App ID and App Secret, so take them from the Threads
   section.
6. In SocialShare, open Accounts and find Threads.
   - **Threads app id**: from step 5.
   - **Threads app secret**: from step 5.
7. Save, then Connect.

**Scopes SocialShare requests:** `threads_basic`, `threads_content_publish`.

**What to know:**

- 500 character limit. Meta counts emoji as their UTF-8 byte length, so an emoji heavy post can
  be rejected while SocialShare's counter still looks fine.
- Publishing is two calls: create a media container, then publish it. SocialShare waits five
  seconds between them on an image post, which is Meta's own recommendation, because the
  container is not immediately publishable.
- **The image is fetched, not uploaded.** Meta downloads it from a URL. That means your
  SocialShare instance has to be reachable from the public internet for an image post to work,
  which it is not when you are running on localhost. Text only posts work fine locally.
- Long lived tokens last sixty days. SocialShare refreshes them when they are within a week of
  expiring.

---

## Instagram

**What you need:** a Meta developer app, an Instagram Business or Creator account, and a Facebook
Page linked to it.

This is the most setup of the six. Instagram has no personal account publishing API.

1. Convert your Instagram account to Business or Creator, in the Instagram app under Settings,
   Account type and tools.
2. Create a Facebook Page if you do not have one, and link your Instagram account to it. In the
   Instagram app that is Settings, Account type and tools, Sharing to other apps, Facebook.
3. Go to <https://developers.facebook.com/apps> and create an app, or reuse the one you made for
   Threads.
4. Add **Facebook Login** and the **Instagram Graph API** products.
5. Under Facebook Login settings, add your redirect URI as a Valid OAuth Redirect URI:
   `https://YOUR-HOST/app/accounts/callback/Instagram`
6. Copy the App ID and App Secret from App settings, Basic.
7. In SocialShare, open Accounts and find Instagram.
   - **Meta app id**: the App ID from step 6.
   - **Meta app secret**: the App Secret from step 6.
8. Save, then Connect. SocialShare walks your Pages, finds the first one with a linked Instagram
   Business account, and stores that account's id. If none of your Pages has one, it says so and
   tells you to link one.

**Scopes SocialShare requests:** `instagram_basic`, `instagram_content_publish`,
`pages_show_list`, `pages_read_engagement`, `business_management`.

**What to know:**

- **An image is mandatory.** There is no text only post. SocialShare refuses to publish an
  Instagram target without one before it makes any call, rather than letting Meta reject it.
- 2,200 character caption limit.
- Aspect ratio has to be between 4:5 and 1.91:1. The compose screen warns when an upload is far
  outside that, but it does not block, because the warning is a guess and Meta's answer is
  authoritative.
- Same as Threads: the image is fetched from a URL, not uploaded, so an Instagram post will not
  work from localhost.
- Publishing is container then publish, with a status poll in between. SocialShare polls for up
  to thirty six seconds and then tells you to retry in a minute rather than hanging.
- Going past your own account requires Meta App Review. For posting to your own linked account
  with you as a developer or tester of the app, development mode is enough.

---

## When something goes wrong

The error text you see in SocialShare is the platform's own response, status code and body, not a
translation of it. That is deliberate: a platform error is much easier to search for verbatim.

Common ones:

| What you see | What it usually means |
|---|---|
| `HTTP 401` on connect for Bluesky | The handle or app password is wrong. App passwords have dashes and are case sensitive. |
| `HTTP 403 ACCESS_DENIED` on LinkedIn | The Share on LinkedIn product is not added, or you approved before adding it. Reconnect. |
| `HTTP 403` on X | Almost always the free tier. |
| `redirect_uri_mismatch` on any platform | The registered URI does not match exactly. Copy it from the platform card. |
| Instagram says no Page has a linked account | Step 2 above is not done, or the token could not see the Page. |
| Threads refuses to authorize | You are not a Threads tester on the app yet, or you have not accepted the invite. |
| Everything suddenly needs reconnecting | The Data Protection key ring was lost. See [operations.md](operations.md). |
