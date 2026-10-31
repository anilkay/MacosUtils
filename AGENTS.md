# MacosUtils

An Avalonia app (.NET 10, `net10.0`) that bundles small macOS utilities in one window with sidebar navigation. It is the Mac port of WindowsUtils (`../WindowsUtils`): same screens and the same "every screen is also an AI tool" rule, with macOS implementations underneath.

## Build & run

```bash
dotnet build                       # from the repo root; 0 warnings (warnings are errors)
dotnet test
dotnet run --project MacosUtils.App
dotnet run --project MacosUtils.Cli -- system
```

The development machine may be Windows: everything compiles and the app starts there, with Mac-only screens showing a notice. Anything that talks to macOS is guarded by `OperatingSystem.IsMacOS()` and returns empty/zero instead of throwing elsewhere. **Real macOS behaviour is only verified by CI** (`build.yml`: `macos-latest` runs the Mac-only tests, the CLI against the live system, and opens the packaged `.app`).

## Structure

```
MacosUtils.Core/
├── AppPaths.cs            # ~/Library/Application Support/MacosUtils, ~/Library/Logs/MacosUtils
├── Processes.cs           # runs system tools by FULL path (never PATH, never a shell), timeouts
├── Plist.cs               # XML plists parsed directly, binary ones via /usr/bin/plutil
├── Interop/ObjC.cs        # minimal Objective-C runtime P/Invoke (NSFileManager, NSURL)
├── IO/                    # FileScanner, DuplicateFinder, Trash (NSFileManager), Finder (open -R), Volumes, ScanExclusions
├── Net/                   # NetworkInfo (ping), TcpTraceroute (SYN probes + unprivileged ICMP socket), FirewallInfo (socketfilterfw + lsof)
├── Logging/               # daily rolling file logger
└── SystemInfo/            # SystemDetails (sysctl/vm_stat/CoreGraphics), MacFeatures, StartupItems (launchd), LocalUsers (dscacheutil), Homebrew,
                           # EnvironmentInfo, RuntimeInstallation + RuntimeDetectors (Java/Python/Node/.NET)
MacosUtils.AI/             # ChatSession, PcTools (the single tool list), McpServerHost, CredentialStore (Keychain), ChatSettingsStore
MacosUtils.Cli/            # "mutils": command table over PcTools
MacosUtils.App/            # Avalonia: App.axaml (theme), MainWindow.cs (shell), Dialogs.cs, Views/*View.cs, macOS/ (Info.plist, package-app.sh)
MacosUtils.Tests/
```

## Conventions

- **UI is built in code**, like WindowsUtils: one `Views/XxxView.cs : UtilityView` per utility, no `.axaml` per screen. `App.axaml` only holds the theme (light/dark brushes via `ThemeDictionaries`) and styles; use the `*Brush` resources and classes (`muted`, `error`, `mono`, `warn`/`dim` rows), not hard-coded colours.
- **Navigation**: `MainWindow.Utilities` is a record array `(Name, Icon emoji, Description, Factory)`. Views are created on first visit and cached. A view that holds work (chat, MCP server) implements `IDisposable`.
- **Bindings**: `AvaloniaUseCompiledBindingsByDefault` is on, but grids bind through `ReflectionBinding` on private record rows (`UtilityView.AddColumn`). `AddColumn(..., sortBy:)` names a numeric property, because sizes display as "1.5 GB" but must sort by bytes.
- **Dialogs are async** (`Dialogs.ShowAsync/ConfirmAsync/ShowTextAsync`). Confirmations that delete or grant access default to **No** (Return and Escape both answer no).
- **Reusable logic goes to Core** (no Avalonia references); views only present. Long work runs in `Task.Run` with a `CancellationTokenSource` (cancel the previous run first) and `Progress<T>`; never block the UI thread.
- **Every screen is also an AI tool**: whatever a screen can show must be a read-only `[Description]` method in `PcTools` (registered in `CreateTools()`), with output capped, paths passed through `ResolveDirectory`/`ToLocalPath`, long scans taking a `CancellationToken`. Add the matching `mutils` command. Chat and the MCP server pick tools up automatically.
- **Tools that return private data or leave the Mac need approval**: `ReadTextFile`, `GetEnvironmentVariable`, `ListEnvironmentVariables` (values can hold secrets) and `PingHost`/`TraceTcpRoute` (DNS and probes reach other hosts) are `NeedsApproval`. `ChatSession.DescribeToolCall` words the prompt; the test `Tool_list_marks_private_data_and_network_tools_for_approval` pins the set.
- **MCP server**: loopback only (`http://localhost:5199/mcp`), per-start bearer token, `Host`/`Origin` guard, tools annotated read-only; needs the ASP.NET Core runtime (bundled in the self-contained app).
- **Never delete permanently.** Files go to the Trash via `Trash.MoveToTrash`; a volume without a Trash makes the call fail and the file stays.

## macOS gotchas

- **Tools by full path, parsed in English.** An app opened from Finder has only `/usr/bin:/bin:/usr/sbin:/sbin`. `Processes` requires a fully qualified path and sets `LC_ALL`/`LANG` to `en_US.UTF-8`. Every parser is a public method taking text, with a test on a captured sample (`ParserTests`): add the sample first when output formats change.
- **Homebrew is not on PATH in an app.** Runtime detectors also look in `/opt/homebrew` and `/usr/local` (`RuntimeProbe.FindExecutables`). `/usr/bin/java` and `/usr/bin/python3` are Apple stubs that can open an install dialog: never run them and skip `/usr/bin` when searching PATH.
- **Detectors never start the runtime**; versions come from `release`, `patchlevel.h`, `node_version.h` or folder names.
- **Homebrew is read, not run.** `Homebrew` lists `Cellar/<formula>/<version>` (with `INSTALL_RECEIPT.json` for "on request" and the date), `Caskroom/<cask>/<version>` (the cask JSON under `.metadata` names its apps for the size) and `var/homebrew/pinned`. Only `brew --version` runs, and only when `xcode-select -p` finds the Command Line Tools: it calls git, and `/usr/bin/git` without them opens the install dialog.
- **Firmlinks and automounts.** Scanning `/` would count `/System/Volumes/Data` twice, and `/net/<host>` mounts an NFS export from that host. `ScanExclusions` and `Volumes.IsNetworkPath` handle both; AI tool paths (the model chooses them) must go through `PcTools.ToLocalPath`, which also expands `~` and refuses network mounts.
- **Privacy (TCC).** Protected folders return access denied, which the scanner's `IgnoreInaccessible` skips; Full Disk Access un-skips them. `Info.plist` carries the Documents/Desktop/Downloads/removable-volume purpose strings.
- **`SpecialFolder.ApplicationData` is `~/.config` on macOS.** Use `AppPaths` for settings and logs.
- **Culture.** `ByteFormatter` follows the user's culture ("1,5 KB" on a Turkish Mac); tests pin the invariant culture.
- **Keychain** uses the classic `SecKeychain*` generic-password calls (service "MacosUtils AI Chat", account = endpoint host). They are deprecated but supported; the Keychain test returns quietly when the keychain is locked.
- **TCP traceroute without root.** `/usr/sbin/traceroute -P tcp` fails for normal users ("pcap_activate() failed": it reads the SYN-ACK through /dev/bpf). `TcpTraceroute` sends ordinary TCP connects with `IP_TTL` set and reads Time Exceeded from an ICMP **datagram** socket (unprivileged on macOS, the one ping uses); root gets a raw socket. Received ICMP packets start with the IPv4 header, which `TryParseIcmpReply` skips. `build.yml` prints a trace with and without sudo to compare.
- **lsof exits 1** when one `-i` selection matched nothing, even with output for the others: parse its output whatever the exit code.
- **root** has `/bin/sh` on every Mac but is disabled unless `dscl . -read /Users/root AuthenticationAuthority` shows a ShadowHash.
- **XML plists are parsed with DTD processing ignored** and no resolver, so a plist can never make the app fetch apple.com.

## Packaging

`MacosUtils.App/macOS/package-app.sh <publish> <version> <out>` builds `MacosUtils.app`: Info.plist (version filled by `plutil`), `AppIcon.icns` from `assets/logo` (PNG up to 256 px, `rsvg-convert` from `logo.svg` above), then ad-hoc signs every file under `Contents/MacOS` except the main executable, then the bundle. `.NET` assemblies count as code, and `codesign --deep` on the bundle fails otherwise. Zip with `ditto -c -k --keepParent` (plain `zip` can drop the extended-attribute signatures). Keep the script LF and executable (`git update-index --chmod=+x`); workflows call it with `bash` so a lost bit cannot break CI.

Releasing: `.\release.ps1 X.Y.Z` pushes the tag; `release.yml` builds `osx-arm64` and `osx-x64` (both on `macos-latest`; the Intel build is cross-published and not launched in CI).

## Adding a new utility

1. `Views/MyToolView.cs : UtilityView`, build the UI in the constructor.
2. One line in `MainWindow.Utilities`.
3. The Core logic in `MacosUtils.Core` with a parser test; a read-only method in `PcTools` + one line in `CreateTools()` (set `NeedsApproval` if it returns file contents, secrets or reaches the network); a `mutils` command.
4. Update the README table.

## Testing

`dotnet test` runs everywhere. `MacOnlyTests` does real work on macOS (sysctl, Trash, Keychain, lsof, traceroute to loopback…) and returns at once elsewhere (xUnit 2 cannot skip at run time). After a UI change, launch the app and open the changed screen.
