# Personal development tools

This repository ships no agent instruction files, personal skills, hooks or MCP
configuration. Build, tests and release automation do not load or validate them.
Contributors can choose their own tools and preferences.

If you use an assistant locally, create its files in your own checkout. The
repository ignores `AGENTS.md`, `CLAUDE.md`, `.agents/`, `.claude/`, `.codex/`,
`.mcp.json` and common editor-agent configuration paths. Ignored files remain
available to your local tools but are absent from commits and other clones.

Use `.git/info/exclude` for additional personal files or private notes that
should be ignored only in your checkout. Never use `git add -f` for personal
configuration: source validation rejects tracked agent instruction/settings
paths even when Git's ignore rules are bypassed.

Before sharing changes, run `git diff --cached --name-only` and
`python scripts/check-repository.py`. Maintainers can also run
`python scripts/check-repository.py --history` to inspect reachable history for
agent configuration that was committed and later deleted.

## Keep private tools across PCs

Keep a separate private folder for personal instructions, canonical skills,
hooks, wiki notes and owner-only helper scripts. Synchronize that folder with
your chosen private file-sync service and restore only those files into each
checkout. Keep the source checkout and its `.git` directory under Git's control.
Use an explicit file allowlist and preview restores; never overwrite tracked
source or silently replace conflicting local edits.

The shared ignore rules also cover private owner notes, helper scripts and
Syncthing metadata/conflict copies. `.git/info/exclude` is optional extra
protection; it is not transferred by a clone. Ignoring a path does not remove
an already tracked file or erase an older commit.

Share portable instructions and skills. Keep API tokens, account sessions,
machine-specific settings, absolute-path MCP configuration, generated graphs,
runtime databases and logs local to each PC. Generate Claude skill mirrors
from the canonical skills after restoring them. File synchronization and its
retained versions are separate from a tested backup.

For example, Syncthing supports [file versioning](https://docs.syncthing.net/users/versioning.html)
and preserves [conflict copies](https://docs.syncthing.net/users/syncing.html).
Enable versioning on both PCs; it applies to changes received from other
devices. Review conflict copies before continuing work. Its
[ignore file](https://docs.syncthing.net/users/ignoring.html) is configured on
each PC and is not itself synchronized.

## Audit before public distribution

`python scripts/check-repository.py --history` checks all locally reachable
commits, including deleted/replaced content and commit/tag metadata. It
requires a complete clone. Validation and release CI fetch full branch/tag
history and run this audit. These checks reject known private paths,
private-network addresses, personal profile paths and several credential formats; manual review is still
needed for personal data or credentials in other formats.

Before changing repository visibility, audit a fresh mirror as well. GitHub
pull-request refs can preserve commits outside branches and tags:

```powershell
git clone --mirror git@github.com:OWNER/REPOSITORY.git /private/path/repository.git
python scripts/check-repository.py --history --history-repo /private/path/repository.git
```

Keep that mirror outside the source checkout. Review release assets and
GitHub discussions, issues, pull requests and workflow logs separately; this
scanner checks Git objects and the working tree. If sensitive history is found,
preserve a private backup before rewriting it and follow
[GitHub's removal guidance](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/removing-sensitive-data-from-a-repository).
Existing clones and cached pull-request views can retain old content.

Public setup, recovery and contribution guidance lives in ordinary project
documentation. Keep machine addresses, account IDs, device identities and
operational logs in your private files.
