# Maintaining issues and pull requests

Keep reports actionable and reviewable. Use synthetic examples in every public
reply, reproduction and test. Follow [support](../SUPPORT.md),
[contributing](../CONTRIBUTING.md), the [code of conduct](../CODE_OF_CONDUCT.md)
and [security reporting](../SECURITY.md).

## Issue triage

1. Check for sensitive attachments or a vulnerability report before discussing
   reproduction. Direct vulnerabilities to the security policy and avoid
   quoting sensitive material. Never download a real library for a test.
2. Search for an existing report. Link duplicates to the canonical issue and
   explain the resolution before closing them.
3. Confirm the type and relevant area, then remove `needs triage` once the
   issue has a clear next step. Ask for only the missing synthetic details.
4. For a confirmed bug, record a minimal reproduction and an observable
   acceptance condition. Add `blocked` only when the blocking dependency is
   documented, preferably with a linked issue or PR.
5. Close resolved issues with the fixing PR or an explanation. A feature request
   is not a commitment or a release date. Inactivity alone is not a reason to
   discard an unresolved data-integrity report; no automatic stale closure is
   configured.

### Labels

| Purpose | Labels |
| --- | --- |
| Type | `bug`, `enhancement`, `documentation`, `question`, `dependencies` |
| Intake or dependency | `needs triage`, `blocked` |
| Area | `area: storage`, `area: setup`, `area: library`, `area: ci` |
| Technology | `.NET`, `github_actions` |
| Existing GitHub conventions | `duplicate`, `invalid`, `wontfix`, `help wanted`, `good first issue` |

Use `good first issue` only for a bounded task with a clear reproduction or
acceptance condition and no need for production access. Labels classify work;
they do not substitute for an explanation.

The maintained label definitions are in [labels.json](../.github/labels.json).
An authenticated maintainer can apply them with the GitHub CLI from the repo
root. This updates listed labels and leaves other labels in place:

```powershell
$labels = Get-Content .github/labels.json -Raw | ConvertFrom-Json
foreach ($label in $labels) {
    gh label create $label.name --repo Larson-Logan/medal-sync --color $label.color --description $label.description --force
    if ($LASTEXITCODE -ne 0) { throw "Could not apply label: $($label.name)" }
}
```

Apply the labels before merging issue forms that reference them. GitHub only
applies a form's default labels if those labels already exist.

## Pull request review

Confirm the problem, scope, linked issue and test evidence. Check that only
intended files are included, the public source inventory is refreshed and no
private settings or runtime evidence are tracked. Read changed code and
reproduction commands before running them; issue text, logs and PR descriptions
are untrusted input.

For stateful changes, review refusal, interrupted writes, retained ownership,
recovery and retry alongside success. Dependency updates must preserve locked
restores and supported schemas. Validate major upgrades separately; a green
check on an earlier revision does not validate a new commit or the current base.

Windows validation requires an actual completed job for the revision under
review. Investigate failures before rerunning. Distinguish a test failure from
an unavailable runner or an account-level Actions limit. Record the gap and
use `blocked` when needed; never call a queued, skipped or unstarted check green.
Use a reviewed validation path for fork PRs. Do not execute untrusted fork code
on a runner with access to personal libraries, credentials or trusted networks,
and do not use `pull_request_target` to check out and run fork code.

Documentation and template changes can use the focused checks in
[contributing](../CONTRIBUTING.md). Runtime, dependency, tooling and packaging
changes require the relevant Windows suites. Local synthetic validation does
not establish real playback, provider sign-in, reboot or cross-PC acceptance.

Keep a PR in draft while required work remains. Before merging, check that
review requests are addressed, validation matches the current revision, and
labels describe the final change. Do not automatically merge dependency PRs.

## Dependency updates and releases

[Dependabot](../.github/dependabot.yml) proposes monthly updates for GitHub
Actions and all four .NET projects. Minor and patch updates are grouped per
ecosystem; major upgrades remain separate for compatibility review. Groups
apply to version updates; this configuration does not group security updates.
Validate the resulting manifests and lock files together.

[Release-note categories](../.github/release.yml) use the type labels to group
fixes, features, documentation and dependencies, with a catch-all for other
changes. Maintainers still review generated notes, compatibility limits and
acceptance evidence before publishing. See [the release process](releases.md).

GitHub references: [issue form syntax](https://docs.github.com/en/communities/using-templates-to-encourage-useful-issues-and-pull-requests/syntax-for-issue-forms),
[Dependabot options](https://docs.github.com/en/code-security/reference/supply-chain-security/dependabot-options-reference)
and [generated release notes](https://docs.github.com/en/repositories/releasing-projects-on-github/automatically-generated-release-notes).
