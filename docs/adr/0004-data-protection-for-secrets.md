# 0004. Data Protection for platform secrets at rest

Status: Accepted

## Context

The database holds things that let somebody post as me: LinkedIn and X access and refresh tokens,
Meta long lived tokens, a Mastodon access token, and a Bluesky app password. It also holds the
client secrets of the developer apps those tokens came from. A stolen SQLite file should not be
enough to use any of it.

## Decision

Every secret goes through `ISecretProtector`, implemented with ASP.NET Core Data Protection.
Each `SocialAccount` row carries two encrypted blobs rather than a column per secret:

- `CredentialsCipher`, the JSON of whatever the user typed into the connect form.
- `TokensCipher`, the JSON of whatever the platform handed back.

A JSON blob rather than columns means adding a field to a platform is a code change, not a
migration, and it means nothing secret can accidentally end up in a query, an index or a log.

The key ring is persisted to `DataProtection:KeyRingPath`, which points at `/home/data/keys` in
Azure so it survives restarts and deployments.

## Consequences

The good:

- The database file on its own is useless for posting as anybody.
- One place to change if the encryption story needs to get stronger.
- Nothing platform specific in configuration. The app has no credentials of its own.

The bad:

- **Lose the key ring and every connected account has to be reconnected.** The ciphertext is
  unrecoverable by design. `Unprotect` catches the failure, logs it clearly and returns null, so
  the app keeps working and the accounts simply show as needing reconnection rather than the
  whole site throwing.
- The key ring is a file, so it needs to be in the backup and it needs the same care as the
  database. That is written up in [operations.md](../operations.md).
- Data Protection keys roll every ninety days by default. Old keys are kept and still decrypt,
  so a roll is not a reconnection event. Deleting old keys is.

## Moving this to Key Vault later

Data Protection supports protecting the key ring with a Key Vault key. The change is one call in
`Program.cs`:

```csharp
builder.Services.AddDataProtection()
    .SetApplicationName("SocialShare")
    .PersistKeysToFileSystem(new DirectoryInfo(keyRing))
    .ProtectKeysWithAzureKeyVault(new Uri(keyIdentifier), new DefaultAzureCredential());
```

The key ring stays on disk but is itself encrypted with a key that never leaves Key Vault, so a
stolen `/home/data` folder is not enough either. That needs a Key Vault, a key, and the App
Service managed identity granted wrap and unwrap. I have not done it because it is another
resource to pay for and manage for a single user app, and the current setup already means a
stolen database is useless.
