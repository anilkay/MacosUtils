# macOS Utils

The Mac sibling of [WindowsUtils](https://github.com/anilkay/WindowsUtils): small, handy utilities in one window with sidebar navigation, built with [Avalonia](https://avaloniaui.net/) on .NET 10. The same read-only tools are available to an AI assistant (AI Chat), to other apps as a local MCP server, and from a command line (`mutils`).

It follows the Mac: light and dark appearance, `~/Library` folders, the Trash, Finder, the Keychain.

## Download

Get `MacosUtils-<version>-osx-arm64.zip` (Apple silicon) or `-osx-x64.zip` (Intel) from the [latest release](https://github.com/anilkay/MacosUtils/releases/latest), unzip it and drag `MacosUtils.app` to Applications. It is self-contained, so no .NET install is needed. `MacosUtils-cli-<version>-<rid>.tar.gz` holds the `mutils` command-line tool.

The app is signed ad hoc (no Apple Developer account), not notarized. Allow it once, either in System Settings › Privacy & Security › "Open Anyway", or:

```bash
xattr -dr com.apple.quarantine /Applications/MacosUtils.app
```

New releases are built by GitHub Actions whenever a `v*` tag is pushed (`.\release.ps1 0.0.2` on an up-to-date, clean `main`; add `-WhatIf` to only run the checks). Every pull request also builds the `.app` and uploads it as a CI artifact to try before a release.

## Utilities

| Utility | What it does | Mac specifics |
| --- | --- | --- |
| **Folder Size Analyzer** | Sizes of everything inside a folder, largest first | Double-click a folder to open it; *Show in Finder* |
| **Largest Files** | Top 100 largest files in a folder or on all local volumes | *Move to Trash* (Finder's "Put Back" works), never deletes permanently |
| **Duplicate Files** | Identical files (size → first 64 KB → SHA-256) | *Select Copies (Keep Oldest)*, then Trash |
| **System Information** | macOS version, Mac model, chip and core counts, memory, uptime, display, installed .NET / Java / Python / Node.js, security settings | Finds Homebrew, pyenv, uv, nvm, Volta, fnm, SDKMAN, python.org, Command Line Tools |
| **Disk Info** | Mounted volumes with capacity and free space; under 10% free in red | Startup disk and `/Volumes`, local / external / network |
| **Network Info** | Interfaces, addresses, MAC, and a ping | `en0`, VPN `utun`… |
| **TCP Traceroute** | Path to a host over TCP, hop by hop | SYN probes with rising TTL; routers are read from ICMP on the unprivileged socket macOS gives ping, so **no administrator password** (WindowsUtils needs admin). The system `traceroute -P tcp` needs root (pcap), so it is not used |
| **Firewall & Ports** | Application Firewall state and per-app rules, plus the TCP/UDP ports processes listen on | macOS's firewall works per app, not per port; ports come from `lsof` and are marked "Other machines" or "This Mac only" |
| **File Hash Calculator** | MD5, SHA-1, SHA-256, SHA-512 and hash verification | Drop a file from Finder |
| **Environment Variables** | This app's variables, or what a new Terminal window sees | An app opened from Finder only gets launchd's short environment; the login-shell view runs your shell once |
| **Startup Items** | LaunchAgents and LaunchDaemons that start with the Mac | Replaces the Windows registry Run keys; *Show plist in Finder* |
| **Homebrew** | Whether Homebrew is installed, its version, and every formula and cask with version, install date, size and whether you asked for it or it came as a dependency | Reads `Cellar` and `Caskroom` in `/opt/homebrew` and `/usr/local` (an Intel Homebrew on Apple silicon is flagged as Rosetta); brew itself is not run, so nothing auto-updates. Shows the download cache that `brew cleanup` frees |
| **Local Users** | Local accounts (UID ≥ 500), administrators, home and shell | From Directory Services; manage users in System Settings |
| **AI Chat** | Chat with an AI agent that can inspect this Mac through the read-only tools | API key in the **Keychain**; local MCP server |

Windows-only features have no Mac equivalent and were swapped for the nearest useful thing: *Windows Features* became security & sharing settings (SIP, Gatekeeper, FileVault, firewall, Remote Login, Screen Sharing, File Sharing, Command Line Tools, Rosetta 2, Homebrew), *.NET Framework* became installed .NET SDKs and runtimes, and creating or deleting users is left to System Settings.

## Permissions

macOS asks the first time the app reads Documents, Desktop, Downloads or an external disk. For a scan of folders such as `~/Library/Mail` or other apps' data, grant **Full Disk Access** in System Settings › Privacy & Security; otherwise those folders are skipped.

Scans never enter `/System/Volumes` (the Data volume is firmlinked into `/`, so files would be counted twice), `/dev`, swap files, or autofs mount points (`/net`, `/Network`, `/home`), and never read network shares (SMB, NFS, AFP, WebDAV): looking inside an automount makes macOS contact another machine.

## AI Chat and the MCP server

Any OpenAI-compatible endpoint works (OpenAI, Ollama, LM Studio, vLLM, OpenRouter…). The API key is stored in the login Keychain, one item per endpoint host, so a key saved for one endpoint is never sent to another. Settings (endpoint, model) live in `~/Library/Application Support/MacosUtils/chat.json`; the key never touches that file.

All tools are read-only. Reading a file's text, reading environment variables and the network tools (ping, traceroute) ask for permission each time (**Don't Allow** is the default).

*Start MCP server* hosts the same tools, plus the agent as `ask_macos_utils_agent`, at `http://localhost:5199/mcp` for MCP clients on this Mac. It listens on loopback only, requires a per-start bearer token (shown with a ready-to-paste `mcp.json`), and rejects browser requests with a foreign `Host` or `Origin`.

## Command line

```bash
dotnet run --project MacosUtils.Cli -- system
dotnet run --project MacosUtils.Cli -- features
dotnet run --project MacosUtils.Cli -- hash ~/Downloads/file.iso SHA-256
dotnet run --project MacosUtils.Cli -- largest ~/Movies 20
dotnet run --project MacosUtils.Cli -- ports
```

`mutils help` lists every command. All commands are read-only.

## Logging

`Microsoft.Extensions.Logging` to a daily rolling file at `~/Library/Logs/MacosUtils/macosutils-yyyyMMdd.log` (also visible in Console.app). Startup, shutdown, unhandled exceptions, AI Chat and the MCP server are logged; files older than 7 days are deleted.

## Build & run

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet build
dotnet test
dotnet run --project MacosUtils.App
```

The app also starts on Windows and Linux for development; screens that need macOS say so instead of showing data.

Package the `.app` yourself (macOS, needs `brew install librsvg`):

```bash
dotnet publish MacosUtils.App -c Release -r osx-arm64 --self-contained -o publish/MacosUtils
bash MacosUtils.App/macOS/package-app.sh publish/MacosUtils 0.0.1 publish
open publish/MacosUtils.app
```

## Project structure

```
MacosUtils.slnx
MacosUtils.Core/   # net10.0, no UI: scanning, hashing, volumes, Trash/Finder, sysctl, launchd, firewall, runtimes
MacosUtils.AI/     # AI chat session, read-only PcTools, MCP server, Keychain credential store
MacosUtils.Cli/    # "mutils" command-line tool over PcTools
MacosUtils.App/    # Avalonia app: MainWindow shell + one Views/*View.cs per utility, macOS/ bundle files
MacosUtils.Tests/  # xUnit: parsers on captured macOS output, file logic, security rules, Mac-only live checks
```

See [AGENTS.md](AGENTS.md) for conventions and gotchas.
