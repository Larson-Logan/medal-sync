# Security maintenance

Keep these controls enabled in the canonical repository. Settings are administered
in GitHub and do not travel with a clone; recheck them after transfers, visibility
changes or workflow changes. A saved configuration is not proof that a scan ran.

## Reporting and detection

Enable private vulnerability reporting, secret scanning, secret push protection,
Dependabot alerts and Dependabot security updates. Handle vulnerability reports
through the [private reporting form](https://github.com/Larson-Logan/medal-sync/security/advisories/new).
Review the [security overview](https://github.com/Larson-Logan/medal-sync/security)
for alerts and scan failures. Rotate an exposed credential at its provider before
removing it from Git; deleting a file does not remove its history. Keep credentials
and incident evidence outside the repository and public issue attachments.

CodeQL default setup uses the extended query suite for C#, JavaScript/TypeScript,
Python and Actions on standard GitHub runners. Check that setup completed and
that each language has a successful analysis for the current revision. CodeQL
and Dependabot findings require review; neither automatically approves a release.

All four .NET projects inherit direct and transitive NuGet auditing, including
low-severity advisories. NU1901 through NU1904 fail restores/builds. Keep locked
restores and inspect audit-source warnings: an unavailable advisory feed cannot
establish that dependencies are clear. Reproduce an affected dependency update
with the [standalone suite](../tests/README.md) before merging it.

## Branches, tags and Actions

The rulesets live in [.github/rulesets](../.github/rulesets) and are applied
by a repository administrator with the GitHub CLI. The script creates `dev` if
needed, creates or updates each ruleset by name, keeps merge commits enabled
and makes `dev` the default branch:

```powershell
pwsh -File scripts/setup-github-rulesets.ps1
```

It warns about any other ruleset, which still applies on top of these; review
and delete it, or run again with `-RemoveOthers`. Repository checks fail when a
ruleset requires a check that no validation job reports, or selects its
branches implicitly.

- **main** selects `refs/heads/main` by name. `~DEFAULT_BRANCH` now means
  `dev`, so a main ruleset written that way would protect the wrong branch. It
  refuses deletion and force pushes, requires a pull request with resolved
  review threads and merge commits only, and requires the `validate` check from
  GitHub Actions. Validation fails any pull request into `main` whose head is
  not this repository's `dev`, and runs every suite for it. `validate`
  summarizes the other jobs: it passes only when every suite passed, or was
  skipped because the source inventory showed the change cannot affect it.
  Branches need not be up to date first: only `dev` merges into `main`, and
  validation tests the merge result.
- **dev** selects `refs/heads/dev`. It refuses deletion, so merging a
  promotion never removes it, and force pushes. It requires a pull request and
  `Repository checks`, so the source and history privacy audit passes before
  anything reaches `dev`. Build and test failures may merge; fix them before
  promoting.

The solo-maintainer policy requires zero approving reviews; this does not
establish independent review. Repository administrators have an emergency
bypass through pull requests only. Use it only after reviewing the exact
revision, recording local validation and explaining any unavailable required
check in the PR. A queued, skipped or failed job never counts as a passing
check. Ordinary direct pushes cannot use this bypass. Keep the bypass limited
to repository administrators.

The **release tags** ruleset restricts creation, updates and deletion of
`refs/tags/v*` to repository administrators. Preserve published version tags
and assets; publish a new version to correct an existing release. Follow the [release process](releases.md).

Require full commit SHA pins for Actions. Allow `actions/*` and
`github/codeql-action/*`; review an action's source and requested permissions
before expanding this allowlist. Default workflow permissions stay read-only,
and Actions cannot approve pull requests. Checkout must not retain credentials.
Write permissions belong only to the job that needs them. Workflow lint
downloads a release of actionlint pinned by version and SHA-256 rather than
using a third-party action. Validation caches never feed a release build.

Require approval for all external fork contributors. Approval does not make fork
code safe for a self-hosted runner. Keep untrusted code away from personal data,
credentials and trusted networks, including before the first workflow step.
Never use `pull_request_target` to check out and execute fork code. Apply the
[disposable runner requirements](releases.md) to Windows integration jobs.

GitHub documents these controls in the [secure use reference](https://docs.github.com/en/actions/reference/security/secure-use),
[rulesets reference](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/about-rulesets)
and [CodeQL default setup guide](https://docs.github.com/en/code-security/code-scanning/enabling-code-scanning/configuring-default-setup-for-code-scanning).
