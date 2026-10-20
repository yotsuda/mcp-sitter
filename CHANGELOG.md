# Changelog

All notable changes to mcp-sitter are documented here. Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), versioning follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- **Reload on change.** Before each `tools/call` / `tools/list`, mcp-sitter checks whether a new build has replaced the child's binary (size + last-write time of the exe and `<name>.dll` beside it). When it has, and the new files are completely written, it restarts its own child on them — no `sitter_kill` needed when the build can replace a running binary (on Windows: rename the old file aside, then copy the new one in). Nothing is watched in the background and nothing is stopped while a build writes, unlike the file watcher removed before 0.2.0. The restart notice says `binary changed`.
- **`--reload-on <path>`** (repeatable) to name the files or directories that mark a new build, for script servers (`--reload-on dist`); **`--no-reload-on-change`** to turn reloading off.
- `sitter_status` reports the watched files, whether they changed since the spawn, and the requests in flight.

### Changed

- **`sitter_kill` stops only its own child by default.** Other processes running the same executable are listed in the report (`sameExeStillRunning`) instead of killed: they may be other sessions' servers, and a client that loses its server may never start it again. `others: true` restores the old behavior — never for an interpreter such as `node` or `python`, where it would kill unrelated programs, possibly the MCP client itself.
- README: the value is stated as what it is — the agent runs edit → build → call the new build in one conversation — rather than as avoiding a client restart.

### Fixed

- **A request the child died on no longer hangs.** Client requests forwarded to the child are tracked; when the child exits or is stopped before answering, each gets an error response at once (a `tools/list` is answered from the cache), instead of waiting for the client's timeout. An answer written just before the exit is still delivered, and a late answer to a request already failed is dropped rather than sent twice.
- A request that cannot be delivered to the child is answered with an error instead of being dropped silently.

## [0.2.0] - 2026-04-21

Multi-platform release. Linux x64 and macOS Apple Silicon native binaries ship alongside Windows, distributed as platform-specific npm subpackages. `npm i -g mcp-sitter` now installs from `@ytsuda/mcp-sitter-win32-x64`, `@ytsuda/mcp-sitter-linux-x64`, or `@ytsuda/mcp-sitter-darwin-arm64` automatically via `optionalDependencies`; a small Node launcher (`bin/cli.mjs`) resolves the matching subpackage and spawns its binary with stdio inherited and SIGTERM/SIGINT/SIGHUP/SIGQUIT forwarded. The umbrella `mcp-sitter` stays unscoped (that's the name users install); the subpackages are scoped `@ytsuda/*` because unscoped platform-suffix names triggered npm's automatic spam-filter on new-name creation — scoped subpackages bypass that filter and match the esbuild / swc convention. Release automation moved from a local-publish model (signed Windows exe committed to git) to a fully CI-driven flow: a three-runner matrix build (windows-latest, ubuntu-latest, macos-latest) followed by a dedicated Windows signing job that Authenticode-signs via Azure Key Vault + OIDC, and a Linux publish job that pushes four npm packages with SLSA provenance plus a GitHub Release.

### Added

- **Linux x64 and macOS arm64 native binaries.** NativeAOT-compiled from the same source as the Windows binary via the `build` matrix job in `release.yml`. macOS is Apple Silicon only — the `macos-13` Intel runner pool queue was too contended to ship.
- **`@ytsuda/mcp-sitter-darwin-arm64` subpackage.** `os: ['darwin']`, `cpu: ['arm64']` filter; installed automatically by npm's optional-deps mechanism on matching platforms.
- **`.github/workflows/release.yml`.** Tag-triggered three-job flow (build matrix → sign-windows → publish). Azure Key Vault OIDC federated credential for signing, npm token environment-scoped, release environment with required reviewer + tag-only deploy policy. Two approvals per release (sign + publish) for defense in depth.
- **`CHANGELOG.md`.** First versioned changelog, starting from this release.

### Changed

- **Signed Windows binary no longer tracked in git.** The `npm/platforms/win32-x64/bin/mcp-sitter.exe` previously lived in version control (signed locally via `Build.ps1 -Sign`). Now `.gitignore` excludes it and CI builds + signs on every tagged release. Signing cert and Azure Key Vault wiring follow the same pattern as the ripple project.
- **CI test matrix re-adds `macos-latest`.** Dropped in v0.2.0 preview because no macOS binary was shipped; restored now that `darwin-arm64` is a supported target.
- **`npm/bin/cli.mjs` signal forwarding.** `SIGTERM`, `SIGINT`, `SIGHUP`, and `SIGQUIT` are now forwarded from the Node wrapper to the spawned native binary, and the child's exit signal is re-raised on the wrapper so `$?` / `%ERRORLEVEL%` / `$status` match a direct invocation. Prevents zombie native processes when MCP hosts send shutdown signals.

### Removed

- **`npm/platforms/darwin-x64`.** Intel Mac is not shipped (see Added > macOS above).
- **Trusted Publisher-based publish from `ci.yml`.** Superseded by the `release.yml` three-job flow.

## [0.1.0] - earlier

First public release. Hot-reload bridge for stdio MCP servers with `sitter_status`, `sitter_kill`, `sitter_binary_info`, and `sitter_child_stderr` tools. Windows + Linux npm binaries.
