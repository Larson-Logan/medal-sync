# Third-party dependencies

.NET package versions, including Microsoft.Data.Sqlite, SQLitePCLRaw and xUnit,
are pinned in project packages.lock.json files. Self-contained binary releases
include the .NET runtime and native SQLite dependencies.

The release also carries `DOTNET-LICENSE.txt` and `DOTNET-NOTICES.txt` copied
from the pinned SDK distribution used to build it.

Upstream license and notice sources:

- [.NET runtime license](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT)
  and [third-party notices](https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT).
- [Microsoft.Data.Sqlite / EF Core license](https://github.com/dotnet/efcore/blob/main/LICENSE.txt).
- [SQLitePCLRaw license](https://github.com/ericsink/SQLitePCL.raw/blob/master/LICENSE.TXT).
- [SQLite copyright statement](https://www.sqlite.org/copyright.html).
- [xUnit license](https://github.com/xunit/xunit/blob/main/LICENSE) (tests only).

rclone and WinFsp URLs and SHA-256 pins are in
tools/medal-sync/config/dependencies.json. Native setup bundles the pinned rclone
executable and its full vendor README (including the MIT copyright and license)
as RCLONE-LICENSE.txt, plus the original pinned WinFsp MSI. The MSI retains its
upstream license and notices and is offered only as an optional all-users driver
installation. See the upstream
[rclone license](https://github.com/rclone/rclone/blob/master/COPYING) and
[WinFsp license](https://github.com/winfsp/winfsp/blob/master/License.txt).
The advanced portable ZIP does not include these dependencies; its helper
downloads rclone separately and reports the pinned WinFsp installer.

The native setup EXE is built with pinned [Inno Setup](https://jrsoftware.org/isinfo.php).
Its compiler installer is verified by SHA-256 and Authenticode before installation
in the developer's ignored tool cache. Inno Setup's generated setup/uninstall
runtime retains its upstream notices. The compiler is not part of the portable ZIP.
