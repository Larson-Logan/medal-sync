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

Public setup, recovery and contribution guidance lives in ordinary project
documentation. Keep machine addresses, account IDs, device identities and
operational logs in your private files.
