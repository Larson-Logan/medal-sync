"""Write the winget manifests for a published stable release.

Use this once, for the first submission to microsoft/winget-pkgs. Later
releases are submitted by .github/workflows/winget.yml, which starts from the
manifests already in winget-pkgs. See docs/updates.md#install-with-winget.

    python scripts/new-winget-manifest.py --version 1.4.0.00 \\
        --sidecar medal-sync-1.4.0.00-setup-win-x64.exe.sha256 --out winget

The files are written below OUT in the folder layout of winget-pkgs, so OUT's
manifests folder can be copied into a winget-pkgs clone as it is. Validate
them on Windows with `winget validate` and test with `winget install
--manifest` before opening the pull request.
"""
from pathlib import Path
import argparse
import datetime
import re
import sys

IDENTIFIER = 'LarsonLogan.MedalSync'
MANIFEST_VERSION = '1.10.0'
REPOSITORY = 'https://github.com/Larson-Logan/medal-sync'
# AppId in installer/windows/medal-sync.iss; Inno Setup adds _is1.
PRODUCT_CODE = '{168562DF-1041-441D-96D0-2244EA744E18}_is1'
# AppPublisher in installer/windows/medal-sync.iss, as Windows shows it.
PUBLISHER = 'Medal Sync contributors'
# Stable release tags are vMAJOR.MINOR.PATCH.NN. Betas are never submitted.
STABLE = re.compile(r'(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0[0-9]|[1-9][0-9]+)')
SHA256 = re.compile(r'[0-9a-fA-F]{64}')


def header(kind):
    return (f'# yaml-language-server: $schema=https://aka.ms/winget-manifest.{kind}.{MANIFEST_VERSION}.schema.json\n\n'
            f'PackageIdentifier: {IDENTIFIER}\n')


def installer(scope, switch, url, sha256, dependency=''):
    return (f'- Architecture: x64\n'
            f'  Scope: {scope}\n'
            f'  InstallerUrl: {url}\n'
            f'  InstallerSha256: {sha256}\n'
            f'  InstallerSwitches:\n'
            f'    Custom: {switch}\n'
            f'{dependency}')


def manifests(version, sha256, release_date):
    url = f'{REPOSITORY}/releases/download/v{version}/medal-sync-{version}-setup-win-x64.exe'
    # Per-user setup cannot install the WinFsp driver, so winget installs it first.
    # All-users setup installs its own bundled, pinned copy when it is missing.
    winfsp = ('  Dependencies:\n'
              '    PackageDependencies:\n'
              '    - PackageIdentifier: WinFsp.WinFsp\n')
    return {
        f'{IDENTIFIER}.yaml': header('version') + (
            f'PackageVersion: {version}\n'
            f'DefaultLocale: en-US\n'
            f'ManifestType: version\n'
            f'ManifestVersion: {MANIFEST_VERSION}\n'),
        f'{IDENTIFIER}.installer.yaml': header('installer') + (
            f'PackageVersion: {version}\n'
            f'InstallerType: inno\n'
            f'UpgradeBehavior: install\n'
            f"ProductCode: '{PRODUCT_CODE}'\n"
            f'ReleaseDate: {release_date}\n'
            f'AppsAndFeaturesEntries:\n'
            f'- DisplayName: Medal Sync\n'
            f'  Publisher: {PUBLISHER}\n'
            f'  DisplayVersion: {version}\n'
            f"  ProductCode: '{PRODUCT_CODE}'\n"
            f'Installers:\n'
            + installer('machine', '/ALLUSERS', url, sha256)
            + installer('user', '/CURRENTUSER', url, sha256, winfsp)
            + f'ManifestType: installer\n'
            f'ManifestVersion: {MANIFEST_VERSION}\n'),
        f'{IDENTIFIER}.locale.en-US.yaml': header('defaultLocale') + (
            f'PackageVersion: {version}\n'
            f'PackageLocale: en-US\n'
            f'Publisher: {PUBLISHER}\n'
            f'PublisherUrl: {REPOSITORY}\n'
            f'PublisherSupportUrl: {REPOSITORY}/blob/main/SUPPORT.md\n'
            f'PackageName: Medal Sync\n'
            f'PackageUrl: https://larson-logan.github.io/medal-sync/\n'
            f'License: MIT\n'
            f'LicenseUrl: {REPOSITORY}/blob/main/LICENSE\n'
            f'ShortDescription: Sync your Medal clips between gaming PCs.\n'
            f'Description: |-\n'
            f'  Medal Sync is a free, open-source Windows companion for Medal. It records to an\n'
            f'  SSD-cached virtual drive, keeps the clip library on your own NAS, disk or cloud\n'
            f'  drive, and hands it from PC to PC. Each PC keeps its own Medal login and\n'
            f'  recording settings. Not made by or affiliated with Medal.\n'
            f'Moniker: medal-sync\n'
            f'Tags:\n'
            f'- clips\n'
            f'- game-recording\n'
            f'- medal\n'
            f'- nas\n'
            f'- rclone\n'
            f'- sync\n'
            f'ReleaseNotesUrl: {REPOSITORY}/releases/tag/v{version}\n'
            f'ManifestType: defaultLocale\n'
            f'ManifestVersion: {MANIFEST_VERSION}\n'),
    }


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split('\n\n', 1)[0])
    parser.add_argument('--version', required=True, help='stable release version, such as 1.4.0.00')
    digest = parser.add_mutually_exclusive_group(required=True)
    digest.add_argument('--sha256', help="the setup EXE's SHA-256")
    digest.add_argument('--sidecar', type=Path, help="the setup EXE's .sha256 file from the release")
    parser.add_argument('--release-date', default=datetime.date.today().isoformat(), help='YYYY-MM-DD; default today')
    parser.add_argument('--out', type=Path, required=True, help='folder to write manifests/ into')
    args = parser.parse_args(argv)

    if not STABLE.fullmatch(args.version):
        parser.error(f'{args.version!r} is not a stable release version such as 1.4.0.00; betas are not published to winget.')
    if args.sidecar:
        setup = f'medal-sync-{args.version}-setup-win-x64.exe'
        fields = args.sidecar.read_text(encoding='ascii').split()
        if len(fields) != 2 or fields[1] != setup:
            parser.error(f'{args.sidecar} is not the checksum of {setup}.')
        sha256 = fields[0]
    else:
        sha256 = args.sha256
    if not SHA256.fullmatch(sha256):
        parser.error(f'{sha256!r} is not a SHA-256.')
    try:
        datetime.date.fromisoformat(args.release_date)
    except ValueError:
        parser.error(f'{args.release_date!r} is not a YYYY-MM-DD date.')

    folder = args.out / 'manifests' / IDENTIFIER[0].lower() / Path(*IDENTIFIER.split('.')) / args.version
    if folder.exists():
        parser.error(f'{folder} already exists; choose another --out.')
    folder.mkdir(parents=True)
    for name, text in manifests(args.version, sha256.upper(), args.release_date).items():
        (folder / name).write_text(text, encoding='utf-8', newline='\n')
        print(folder / name)
    return 0


if __name__ == '__main__':
    sys.exit(main())
