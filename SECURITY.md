# Security

Only the latest stable release receives fixes. Medal Sync is experimental;
maintain an independent media backup and inspect release notes before updating.

Use this repository's **Security → Report a vulnerability** form when private
reporting is enabled. If unavailable, open an issue asking for a private
reporting channel, without exploit details or private data. Do not publish
credentials, databases, recordings or runtime logs.

Release downloads are verified against the SHA-256 digest returned by GitHub,
then each payload file is checked against its manifest. These checks depend
on HTTPS and control of the GitHub repository; they are not publisher signing.
Windows binaries are currently unsigned. Launch checks send no configuration,
account information or credentials; GitHub receives the ordinary network
request and its source IP address.

Maintainers should enable private vulnerability reporting, secret scanning and
push protection before public launch. Require Windows validation on main and
restrict release tags to maintainers.
