# 0005. OpenID Connect federated credentials for deployment

Status: Accepted

## Context

GitHub Actions needs to deploy to Azure App Service. The old way is to download a publish profile
and paste it into a repository secret. That profile contains a long lived credential that can
deploy to the site, it does not expire on its own, it is easy to forget about, and rotating it
means remembering to re-download it.

## Decision

`azure/login@v2` with `client-id`, `tenant-id` and `subscription-id`, and a federated credential
on an Entra ID app registration that trusts GitHub's OIDC issuer for this repository and this
environment. The workflow job requests `id-token: write`, GitHub mints a short lived token, Azure
exchanges it for an Azure token scoped to the role I granted.

No secret in the repository is a credential. The three values stored as secrets are identifiers,
not passwords. They are stored as secrets rather than variables out of habit, not necessity.

## Consequences

The good:

- Nothing long lived to leak. The token Azure sees lives for minutes.
- Nothing to rotate. There is no credential to expire.
- The trust is scoped to one repository and one environment, so another repository in the same
  account cannot use it.
- Access is an Azure role assignment, so revoking it is one command and it is visible in the
  portal like everything else.

The bad:

- The one time setup is more work than pasting a publish profile: an app registration, a
  federated credential with a subject that exactly matches the workflow, and a role assignment.
  The exact commands are in [azure-deployment.md](../azure-deployment.md).
- The subject string has to match precisely. `repo:owner/name:environment:production` will not
  match a job that has no `environment: production` on it, and the failure message is not
  obvious. The deploy job in the workflow sets that environment on purpose.
