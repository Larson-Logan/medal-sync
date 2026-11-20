---
type: policy
title: Public source hygiene
description: What may be committed to this public repository, how the source inventory classifies files for CI, and the documentation link check.
resource: https://github.com/Larson-Logan/medal-sync/blob/dev/scripts/check-repository.py
tags: [policy, repository, ci, privacy, agent-files, source-manifest, docs]
sources:
  - id: check-repository
    resource: https://github.com/Larson-Logan/medal-sync/blob/dev/scripts/check-repository.py
    title: Public source check
  - id: source-manifest
    resource: https://github.com/Larson-Logan/medal-sync/blob/dev/scripts/update-source-manifest.py
    title: Public source inventory generator
  - id: local-development
    resource: https://github.com/Larson-Logan/medal-sync/blob/dev/docs/local-development.md
    title: Personal development tools
generated: { by: "claude-code/claude-opus-5-5", at: "2026-11-20T03:34:40Z" }
status: draft
---

# Summary

The repository is public. [`scripts/check-repository.py`](../../../scripts/check-repository.py)
runs in every validation and fails on files or content that must stay private.
[`public-source.json`](../../../public-source.json) lists every tracked file
with the Windows CI suites it affects.

# What must stay untracked

- **Agent instructions and settings:** `AGENTS.md`, `CLAUDE.md`,
  `CLAUDE.local.md`, `GEMINI.md`, `.mcp.json`, `.claude/`, `.agents/`,
  `.codex/`, `.cursor/`, `.github/copilot-instructions.md` and similar. They
  are ignored by `.gitignore`. Never use `git add -f` for them; the check
  rejects them even when ignore rules are bypassed.[^local-development]
- **Private owner knowledge and generated data:** `wiki/`, `graphify-out/`,
  `.validation/`, `.agent-state/` and the owner-only paths listed in
  `OWNER_PATHS`.
- **Runtime and private files:** databases, logs, keys, executables, media,
  `device.json`, `rclone.conf`, `.env` files and Syncthing markers.

This knowledge bundle (`docs/knowledge/`) is public documentation and is
committed. The local `CLAUDE.md` that imports it is not.

# Content checks

Tracked text must not contain:

- credentials (private keys, GitHub, Slack, AWS or Google API tokens);
- a path inside a real person's Windows profile folder. Use a placeholder
  profile name such as `your-user`, `user` or `example`;
- private IPv4 addresses (`10.x`, `172.16–31.x`, `192.168.x`). Use
  documentation ranges and placeholders in examples.

# Source inventory

After adding, removing or renaming files, run
`python scripts/update-source-manifest.py`; validation runs it with `--check`.
Rules in that script decide each file's suites. The first match wins, and a
file no rule names needs every suite. Documentation (`docs/*.md`,
`docs/knowledge/`) needs no Windows suite, so a documentation-only pull request
skips them.

# Documentation links

[`scripts/check-doc-links.mjs`](../../../scripts/check-doc-links.mjs) checks
every local file target and Markdown section anchor in the Git-visible
Markdown files that `check-repository.py --list-docs` lists.
Links starting with `/` resolve from the repository root, so this bundle uses
only relative links. Remote URLs are not checked.

# Related

- [Release and versioning](../runbooks/release-and-versioning.md): where these
  checks run in the pull request flow.
- [Personal development tools](../../local-development.md) and
  [CONTRIBUTING](../../../CONTRIBUTING.md#validate-your-change)

[^local-development]: Personal development tools
