# Secrets for Agent2

This folder is mounted **read-only** into her container at `/agent/secrets`. Nothing in it is committed to git
(only this file is). It is optional: without it she can still use git, but only with public repositories.

## `github_read_token` - lets her pull from your private repos

Create a **fine-grained personal access token** on GitHub that can only *read*:

1. GitHub > Settings > Developer settings > Personal access tokens > **Fine-grained tokens** > Generate new token.
2. Resource owner: your account. **Repository access: Only select repositories**, and pick just the repos she should read.
3. Repository permissions: **Contents: Read-only**. Leave everything else as "No access".
4. Set an expiry (for example 90 days).
5. Copy the token and save it in this folder as a file named exactly `github_read_token` (just the token, nothing else).

She uses it only for `https://github.com` and only to pull and clone. Pushing is turned off inside the container, and a read-only
token could not push anyway. To remove her access, delete the file (and revoke the token on GitHub).

Keep in mind that she has a shell, so she (and anything she is tricked into running) can read this file. That is why the token must be
read-only and limited to the repos she needs.
