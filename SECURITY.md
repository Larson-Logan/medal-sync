# Security

Only the latest stable release receives fixes. Medal Sync is experimental;
maintain an independent media backup and inspect release notes before updating.

Use [private vulnerability reporting](https://github.com/Larson-Logan/medal-sync/security/advisories/new)
to report vulnerabilities. If unavailable, open an issue asking for a private
reporting channel, without exploit details or private data. Do not publish
credentials, databases, recordings or runtime logs.

Release downloads are verified against the SHA-256 digest returned by GitHub,
then each payload file is checked against its manifest. These checks depend
on HTTPS and control of the GitHub repository; they are not publisher signing.
Windows binaries are currently unsigned. Launch checks send no configuration,
account information or credentials; GitHub receives the ordinary network
request and its source IP address.

Maintainers keep private vulnerability reporting, secret scanning, push protection
and Dependabot security updates enabled. Require pull requests and Windows
validation on main, restrict release tags to maintainers and review dependency
updates before merging. See [security maintenance](docs/security-maintenance.md)
for repository settings, scanning and emergency handling.
