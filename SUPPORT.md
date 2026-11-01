# Getting help

Medal Sync is an experimental community project. Maintainers help through
GitHub issues as time permits; there is no guaranteed response time.

| You need help with | Start here |
| --- | --- |
| Installing and configuring a library | [Getting started](docs/getting-started.md) and the included Medal Sync Setup Guide |
| Server, persistent local storage or cloud copies | [Storage guide](docs/storage.md) |
| A command, exit code or `config.json` setting | [Command reference](docs/commands.md) and [configuration reference](docs/configuration.md) |
| Updating, reinstalling, removing or recovering an update | [Updates and removal](docs/updates.md) |
| Building or contributing | [Contributing](CONTRIBUTING.md) and [tests](tests/README.md) |
| A possible vulnerability | [Security policy](SECURITY.md) |

Search [existing issues](https://github.com/Larson-Logan/medal-sync/issues)
before opening a [new report or question](https://github.com/Larson-Logan/medal-sync/issues/new/choose).
Choose **Bug report**, **Feature request**, **Setup or usage question** or
**Documentation improvement**. Reports about Medal itself belong with
[Medal support](https://support.medal.tv); include an explanation when a problem
only occurs through Medal Sync.

## Share useful details safely

On version 1.2.0 or later, run `medal-sync diagnostics` to preview a summary of
versions, storage mode, ownership state and refusal codes. It leaves out paths,
names, remotes, clips, credentials and logs, and replaces library and PC IDs
with tokens. Review the preview, then save it with
`medal-sync diagnostics --out FILE` and attach that file to your report. See
the [command reference](docs/commands.md) for what it contains. Nothing is
uploaded for you.

Without a summary, include the Medal Sync version or source commit, Windows build, installation
method and storage mode. For library compatibility, include Medal app and
recorder version numbers if available. Describe the smallest reproduction,
expected result, actual result, and the relevant error text with synthetic
values. Mention whether the problem began after an update.

Use placeholders such as `C:\Example\MedalSync`, `\\example-server\clips`
and `example-remote:clips`. Remove account names, network addresses, media
titles, remote IDs and credentials, including values inside command arguments.
Check screenshots for those details before sharing them. Never upload real
databases or their sidecars, recordings, configuration files, device identities,
credentials or raw runtime logs. Do not upload a support archive from a live
library. A short error rewritten with synthetic values is enough to start.

## If a session or handoff is stuck

State the last known status: **Ready**, **ACTIVE**, finishing or unknown.
Explain whether Medal is still running, whether recent recordings may be
unpublished, and which recovery attempts have already happened. You do not
need to repeat an operation or alter the library to file a report.

Keep ownership, cache and recovery evidence intact while asking for help.
Do not clear SQLite sidecars, reset an existing journal, delete the cache or
force-kill Medal to get past a refusal. Wait for **Ready** before switching
PCs, updating or uninstalling. Keep an independent media backup.
