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

The main ruleset must explicitly select `~DEFAULT_BRANCH`, refuse deletion and
force pushes, require a pull request with resolved review threads, and require
the `validate` check from GitHub Actions with the branch up to date. `validate`
summarizes the other jobs: it passes only when every suite passed, or when a
documentation-only pull request skipped the Windows suites. A ruleset
whose include list is empty does not protect main. The solo-maintainer policy
requires zero approving reviews; this does not establish independent review.

Repository administrators have an emergency bypass through pull requests only.
Use it only after reviewing the exact revision, recording local validation and
explaining any unavailable required check in the PR. A queued, skipped or failed
job never counts as a passing check. Ordinary direct pushes cannot use this
bypass. Keep the bypass limited to repository administrators.

Restrict creation, updates and deletion of `refs/tags/v*` to repository
administrators. Preserve published version tags and assets; publish a new version
to correct an existing release. Follow the [release process](releases.md).

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
