## Problem and resulting behavior

<!-- Describe the user-visible problem and what changes. Include a small before/after example when useful. -->
<!-- Link related issues. Use "Closes #123" only when this PR fully resolves that issue. -->

## Validation

<!-- List commands, results and any checks you could not run, with a reason. -->
<!-- For documentation-only changes, source hygiene, inventory and link checks are sufficient. -->
<!-- Runtime, dependency or tooling changes need Windows standalone validation. -->

## Failure and recovery

<!-- For ownership, storage, restore, setup or update changes, explain refusal, interruption and retry behavior. Otherwise write "Not applicable". -->

## Checklist

- [ ] Changes are scoped and use synthetic examples; no private or runtime files are included.
- [ ] Relevant checks and their results are recorded above; queued or skipped checks are not described as passing.
- [ ] The public source inventory is refreshed with `python scripts/update-source-manifest.py`.
- [ ] Documentation and changelog reflect user-visible changes, or I explained why they are unnecessary.
