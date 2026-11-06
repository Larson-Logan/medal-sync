# Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io/),
certificate by [SignPath Foundation](https://signpath.org/).

Medal Sync is applying to SignPath Foundation's free signing program for
open-source projects. Until the project is accepted and signing is turned on,
releases stay unsigned, as described in
[if Windows warns about the download](updates.md#if-windows-warns-about-the-download).

## What is signed

Each stable release from `main` signs:

- `medal-sync.exe` and `medal-sync-launch.exe`, in both the setup EXE and the
  portable ZIP
- the setup EXE, `medal-sync-VERSION-setup-win-x64.exe`

Betas from `dev` are not signed. The portable ZIP itself cannot carry a
signature; its programs are signed and its SHA-256 sidecar and per-file manifest
describe the signed files. Third-party programs bundled by setup, such as
rclone and the WinFsp installer, keep their publishers' own signatures or are
verified against pinned SHA-256 values.

Only files built from this repository's source by the
[release workflow](../.github/workflows/release.yml) on GitHub-hosted runners
are signed. Nothing built on a personal computer is submitted for signing.

## How a release is signed

1. A merge to `main` starts the release workflow. Validation builds both
   programs and uploads them as a workflow artifact.
2. The workflow sends a signing request to SignPath. An approver checks that
   the request comes from the release workflow on `main` for the expected
   version, then approves it.
3. The signed programs are packaged into the ZIP and the setup EXE, and the
   setup EXE goes through the same request and approval.
4. The setup EXE's `.sha256` sidecar is rewritten for the signed file, and
   every installer test runs against the signed setup EXE. Before publishing,
   the workflow checks again that the setup EXE and both programs have a
   valid, timestamped signature.

Signing is turned on by setting the `SIGNPATH_ORGANIZATION_ID` repository
variable and the `SIGNPATH_API_TOKEN` secret. `SIGNPATH_PROJECT_SLUG` and
`SIGNPATH_POLICY_SLUG` override the default project slug `medal-sync` and
signing policy slug `release-signing`.

The SignPath project needs two artifact configurations. `programs`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<artifact-configuration xmlns="http://signpath.io/artifact-configuration/v1">
  <zip-file>
    <pe-file path="medal-sync.exe">
      <authenticode-sign />
    </pe-file>
    <pe-file path="medal-sync-launch.exe">
      <authenticode-sign />
    </pe-file>
  </zip-file>
</artifact-configuration>
```

and `setup`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<artifact-configuration xmlns="http://signpath.io/artifact-configuration/v1">
  <zip-file>
    <pe-file path="medal-sync-*-setup-win-x64.exe" max-matches="1">
      <authenticode-sign />
    </pe-file>
  </zip-file>
</artifact-configuration>
```

## Team roles

| Role | Members |
| --- | --- |
| Committers and reviewers | [Larson-Logan](https://github.com/Larson-Logan) |
| Approvers | [Larson-Logan](https://github.com/Larson-Logan) |

Committers and reviewers may change the source code; every change reaches
`main` through a reviewed pull request into `dev` and a promotion from `dev`.
Approvers approve each signing request. All of them must use multi-factor
authentication for GitHub and SignPath.

## Privacy policy

This program will not transfer any information to other networked systems
unless specifically requested by the user or the person installing or
operating it, with these exceptions, which are part of what it is configured
to do:

- **Update checks.** When it starts, and when you choose
  **Check for Medal Sync updates**, Medal Sync asks GitHub for the latest
  release of this repository. The request sends no configuration, account
  information or credentials; GitHub receives the ordinary network request and
  its source IP address. See [installation, updates and removal](updates.md).
- **Your storage.** Medal Sync copies clips and library records to the server,
  folders and cloud providers you configure, and to no other place.

## Check a signature

In PowerShell, on a downloaded setup EXE or an installed program:

```powershell
Get-AuthenticodeSignature .\medal-sync-VERSION-setup-win-x64.exe | Format-List Status, SignerCertificate
```

`Status` must be `Valid`. A signed file's properties also show the signer on
the **Digital Signatures** tab. Report a release that is unsigned after signing
was turned on, or signed by anyone else, through the
[security policy](../SECURITY.md).
