# mcp-sitter

A hot-reload bridge for stdio **Model Context Protocol (MCP)** servers. While
an AI agent develops your MCP server, it can edit the server, rebuild it, and
call **the new build in the same conversation** — no client restart, no
reconnecting by hand (Claude Code, Claude Desktop, Cursor, Cline, etc).

<div align="center">
  <img src="https://github.com/user-attachments/assets/9ae83915-2a07-4d09-a614-cbae4443a792" alt="social-image" width="640" />
</div>

Install `mcp-sitter` (`npm install -g mcp-sitter`), then register your
server with Claude Code:

```powershell
mcp-sitter add my-baby C:\path\to\my-baby-mcp.exe [args...]
```

This runs `claude mcp add` for a server named **`my-baby-dev`** — `-dev` is
appended so the dev build never collides with a production registration —
with your exe behind `mcp-sitter`. `--from my-baby` copies the environment
variables of the production registration; `--dry-run` prints the `claude`
command instead of running it. Claude Code loads a new server when it
starts, so start a new session (or resume this one with `claude --continue`).

That's the entire setup. Your tools are now available as `mcp__my-baby-dev__*`.
Rebuild your server any time — the next tool call runs on the new build. No
client restart, no lost conversation context, and the AI sees a `[mcp-sitter]`
notice with the tools-diff so it self-corrects after schema changes. (On
Windows a running binary is locked: call `sitter_kill` before the build, or
have the build rename the old file aside before copying the new one — see
[Reload on change](#reload-on-change).)

### No restart even for a new server: the hub

Registering a server needs that one client restart. To skip it too,
register the **hub** once:

```powershell
mcp-sitter add --hub          # registers sitter-dev
```

From then on the AI attaches any server under development while the session
runs, and its tools are callable right away:

```
sitter_attach(name: "my-baby", command: "C:\path\to\my-baby-mcp.exe")
→ mcp__sitter-dev__my-baby_<tool>, in this same conversation
```

Each attached server runs behind its own `mcp-sitter`, so it reloads on a
new build, reports crashes and keeps its stderr exactly like a registered
one. It runs from a shadow copy, so even a plain in-place `dotnet build`
works on Windows, and it is remembered for the project and attached again
in the next session. See [Hub](#hub).

> Other MCP clients (Claude Desktop, Cursor, Cline, ...) work the same way:
> point the client at `mcp-sitter` and pass your server exe + args.
> Building from source instead? See [Build from source](#build-from-source).

Point your MCP client at `mcp-sitter` instead of your in-development server.
`mcp-sitter` spawns your server as a child process and proxies all JSON-RPC
messages. Before each tool call it checks whether a new build has replaced
the child's binary; if it has, and the new files are completely written, it
restarts the child on them. When the child is down — stopped with
`sitter_kill`, or crashed — the next `tools/call` **lazily respawns** it.
Either way, if the tool set changed, it pushes
`notifications/tools/list_changed` to the client.

More importantly, **the first tool/call response after a respawn gets a
`[mcp-sitter]` notice prepended to its `content` array**, telling the AI:

- why the server restarted (binary changed, `sitter_kill`) and how many
  times it has,
- how long the startup took,
- what version/build-date the new binary has,
- whether the previous instance crashed,
- and **exactly which tools were added / removed / changed**.

This makes the AI **self-correcting**: if you renamed a tool or changed a
schema, the AI sees the diff in the same response as any error, and retries
with the correct name/shape automatically.

No more "rebuild, then stop and ask a human to restart or reconnect the
server" loop.

## Why

Developing a stdio MCP server with an AI agent hits the same wall every time
the server changes:

1. The client starts your server once and keeps talking to that process. A
   new build on disk changes nothing until the process is replaced.
2. Replacing it is a step outside the conversation: restart the client, or
   reconnect the server by hand (`/mcp` → Reconnect in Claude Code). The agent
   cannot take that step, so it stops and waits for you after every change.
3. On Windows the running server also locks its binary, so a build that
   writes it in place fails until that process is gone.
4. After the restart nothing tells the agent what changed. A renamed tool or
   a changed schema shows up as confusing errors, and a crash on startup
   leaves no output the agent can read.

`mcp-sitter` sits between the client and your server and owns the child
process lifecycle independently of the client. The agent runs the whole loop
on its own — edit, build, call the new build, read the crash output, fix —
in one conversation.

## How mcp-sitter is different from other MCP hot-reload tools

Unlike simple `stdin: inherit` passthrough wrappers:

- **Correct handshake replay.** `initialize` and `notifications/initialized`
  are cached on the very first client handshake and re-sent to the respawned
  child with internal request ids, so the child is always properly
  initialized — even with strict MCP server implementations.
- **Tools-diff awareness.** `mcp-sitter` issues an internal `tools/list` on
  respawn, diffs against the previous list by tool name and schema, and
  surfaces the result both as `notifications/tools/list_changed` (for the
  client infra's tool cache) and as a `[mcp-sitter]` content notice on the
  next tool/call response (for the AI itself).
- **Content-level notices.** MCP clients aren't guaranteed to surface
  protocol notifications to the LLM. `mcp-sitter` injects a short text
  block at `content[0]` of the next tool/call response so the AI always
  sees that a restart happened and what changed.

## Architecture

```
MCP Client <--stdio--> mcp-sitter <--stdio--> your MCP server
  (spawned and owned by the bridge; restarted on a new build, after
   sitter_kill, or after a crash)
```

See the [GitHub README](https://github.com/yotsuda/mcp-sitter#architecture)
for rendered architecture and sequence diagrams.

### Typical dev cycle

1. The AI (or you) runs `dotnet build` / `cargo build` / `npm run build` /
   whatever produces the new server binary.
   - If the build cannot replace the running binary (Windows, writing the
     file in place), call `sitter_kill` first. It closes stdin on the child,
     waits 300 ms, then kills it if needed, which unlocks the file.
2. The AI calls `tools/call my_tool`. The binary changed or the child is
   down, so `mcp-sitter`:
   - waits until the new files are completely written (up to 5 s; if they
     are not, this call still runs on the old child and the next one checks
     again),
   - stops the old child, if it is still running,
   - spawns a new child,
   - replays the cached `initialize` + `notifications/initialized`
     using an internal request id (the response never leaks to the client),
   - issues an internal `tools/list` and computes a diff vs. the last known
     tool set,
   - sends `notifications/tools/list_changed` to the client if the diff is
     non-empty,
   - forwards the original tool/call to the child,
   - receives the response, and
   - **prepends a `[mcp-sitter]` notice** to the response's `content` array
     with spawn #, startup time, binary version/age, previous exit info,
     and the tools diff.

The client surfaces the merged response to the AI on the very next turn —
inside the same conversation, with no restart.

## Example restart notice

A typical notice (one text block, pre-pended to the first tool/call response
after a respawn):

```
[mcp-sitter] server restarted (binary changed, spawn #3, pid 12345, startup 1.8s).
path: C:\dev\my-server\dist\my-server.exe.
binary v1.2.3+abc123 built 2 min ago. previous: exit 0 after 45s.
tools: +1 added (search_index), 1 schema changed (read_file).
```

Crash-after-respawn:

```
[mcp-sitter] server restarted (spawn #4, pid 12346, startup 0.9s).
path: C:\dev\my-server\dist\my-server.exe.
binary built 5 min ago. previous: ⚠ crashed (exit -1073741819) after 2.1s.
```

## Crash self-diagnosis flow

When a child crashes on startup or mid-session, `mcp-sitter` turns the usual
"invisible exit code" into something the AI can act on. The child's stderr
(usually where .NET/Node/Python dump unhandled exceptions) is buffered in a
1000-line ring persisted across respawns, and exposed via
`sitter_child_stderr`. Combined with `sitter_status`, this lets the AI
diagnose and fix its own server without the developer even noticing:

1. The child crashes mid-call. The pending call gets an error right away
   ("child exited (code N) before responding") instead of waiting for the
   client's timeout.
2. The AI retries; the next call respawns the child, and the restart notice
   says the previous instance crashed.
3. The AI calls `sitter_child_stderr` and reads the stack trace, then fixes
   and rebuilds.

See the [GitHub README](https://github.com/yotsuda/mcp-sitter) for the rendered diagram.

Because the stderr ring survives respawns and the delimiter clearly marks
the boundary, the AI sees *both* the crashed generation's stack trace *and*
the fresh generation's output in the same response. No developer
intervention, no `strace`-equivalent setup, no log file plumbing.

## Testing

Integration tests are **Pester v5** scenarios in
[`test/McpSitter.Tests.ps1`](test/McpSitter.Tests.ps1), running against a
minimal stdio MCP server ([`test/FakeMcp`](test/FakeMcp)) over the real
JSON-RPC handshake:

- `mcp-sitter core` — tools/list merging, tool forwarding, `sitter_kill` +
  lazy respawn with `[mcp-sitter]` restart notice injection, post-respawn
  `sitter_status` deltas.
- In-flight requests — an error instead of a hang when the child crashes
  mid-call; an answer written just before exit still arrives.
- Reload on change — a rename-and-copy deploy restarts the child at the next
  call with a tools-diff; `--no-reload-on-change` keeps it.
- `sitter_kill` scope — another process on the same exe is listed, and
  stopped only with `others: true`.
- `sitter_child_stderr` — startup stderr capture, mid-lifecycle lines via
  a `fake_log` tool, `since_spawn=true` filter, `since_spawn=false` with a
  `----- child respawn (gen N, pid P) -----` delimiter between generations.

```bash
dotnet build -c Debug
dotnet build test/FakeMcp/FakeMcp.csproj -c Debug
Invoke-Pester test/McpSitter.Tests.ps1 -Output Detailed
```

CI runs the same Pester suite on **Windows / Ubuntu / macOS** matrix
via [`.github/workflows/ci.yml`](.github/workflows/ci.yml). Tagged
releases run `.github/workflows/release.yml` — a three-job flow that
NativeAOT-publishes for each RID (win-x64 / linux-x64 / osx-arm64),
Authenticode-signs the Windows binary via Azure Key Vault on a
Windows runner, and publishes three platform-specific npm
sub-packages (`@ytsuda/mcp-sitter-win32-x64`, `@ytsuda/mcp-sitter-linux-x64`,
`@ytsuda/mcp-sitter-darwin-arm64`) plus the umbrella `mcp-sitter` package,
all with SLSA provenance. The umbrella resolves the matching
sub-package at runtime via its `optionalDependencies`.

## Built-in tools

`mcp-sitter` exposes four tools of its own, merged into the child's
`tools/list` response:

| Tool                  | What it does |
| --------------------- | ------------ |
| `sitter_status`       | Returns child state, binary path/version, child PID, per-spawn and sitter uptimes, spawn/kill counts (including external / crash deaths), last kill reason/time, last startup duration, previous exit info, last tools-diff summary, the files watched for a new build and whether they changed since the spawn, and the number of requests in flight. |
| `sitter_kill`         | Stops the child so its binary is unlocked for a rebuild; the next tool call respawns it. Other processes running the same executable are listed, not stopped — pass `others: true` to stop them too. |
| `sitter_binary_info`  | Returns version, build date (mtime), size, and metadata of the child binary. Use this to confirm which build is currently loaded and to detect stale binaries. |
| `sitter_child_stderr` | Tails the child MCP server's stderr from a bounded ring buffer (~1000 lines, persisted across respawns). Crucial for diagnosing child startup failures and crashes — pair with `sitter_status` after a non-zero `previousExitCode`. |

Everything else (`tools/list`, `tools/call` for non-built-in tools, `ping`,
`notifications/*`, `resources/*`, `prompts/*`, etc.) is forwarded
transparently between the client and the child.

## Build from source

Requires .NET 9 SDK. Most users should prefer the npm prebuilt binary shown
at the top of this README.

```powershell
git clone https://github.com/yotsuda/mcp-sitter.git
cd mcp-sitter
.\Build.ps1
```

The AOT binary lands at `dist/mcp-sitter.exe`; NativeAOT needs the C++
build tools ("Desktop development with C++" in Visual Studio). `Build.ps1`
stops nothing: a running copy is renamed aside first, so the `mcp-sitter`
instances of open MCP sessions keep running. Each release runs
`dotnet publish` for all three platform RIDs (`win-x64` / `linux-x64` /
`osx-arm64`) on matrix runners, Authenticode-signs the Windows binary
via Azure Key Vault, and publishes per-platform npm sub-packages with
SLSA provenance; see `.github/workflows/release.yml`.

## CLI

```
mcp-sitter [options] [--] <child-exe> [child-args...]
```

| Option         | Description |
| -------------- | ----------- |
| `--cwd <path>` | Working directory for the child process. Defaults to `mcp-sitter`'s cwd. |
| `--reload-on <path>` | A file or directory whose change means a new build (repeatable). Default: the child executable, plus `<name>.dll` beside it. Use it for a script server, e.g. `--reload-on dist`. |
| `--no-reload-on-change` | Restart the child only after `sitter_kill` or a crash. |
| `--shadow-copy` | Run the child from a copy of its directory (under the per-user data folder, one copy per spawn), so the build output is never locked. Watches the whole directory. |
| `--help`, `-h` | Show help. |

```
mcp-sitter add <name> [options] [--] <child-exe> [child-args...]
```

Registers `<name>-dev` with Claude Code through `claude mcp add` (`-dev` is
appended when missing). A child given as a path is made absolute.

| Option | Description |
| ------ | ----------- |
| `--scope`, `-s <scope>` | `user` (default), `local` or `project`. |
| `--from <server>` | Copy the env of an existing registration (user scope, this project's local scope, or `.mcp.json`). |
| `-e`, `--env KEY=VALUE` | Set an environment variable (repeatable; overrides `--from`). |
| `--cwd`, `--reload-on`, `--no-reload-on-change` | Passed to `mcp-sitter` in the registration. |
| `--dry-run` | Print the `claude` command instead of running it. |
| `--hub` | Register the [hub](#hub) (default name `sitter-dev`) instead of a server. Takes no child command. |

A server added while Claude Code runs is not loaded into the running
session, not even from `/mcp`; start a new session or `claude --continue`.

Use `--` to separate `mcp-sitter` options from the child command if any
child argument happens to start with `--`.

## How it works

### Lazy spawn

The child process is **not** eagerly restarted after being killed. Instead,
the next incoming `tools/call` or `tools/list` from the client triggers
a spawn:

1. Spawn the child with the same command line.
2. Replay the cached `initialize` request with a fresh internal id (the
   response is absorbed via the bridge's pending-request table and never
   leaks to the client) and forward the cached `notifications/initialized`.
3. Issue an internal `tools/list` request and diff the response by tool
   name and schema against the previous cache. If diff is non-empty, send
   `notifications/tools/list_changed` to the client and stash the diff
   summary for the next restart notice.
4. Forward the original client request to the child.
5. When the child responds to the original request, prepend a
   `[mcp-sitter]` notice to `result.content[0]` and forward.

Spawning only on demand means nothing is started while a build is still
writing files.

### Reload on change

Before forwarding a `tools/call` or `tools/list`, `mcp-sitter` compares the
watched files with what they were when the child was spawned (size and
last-write time — not creation time, which Windows carries over to a file
created under a name just renamed away). When they differ:

1. If requests are still in flight to the child, the reload waits for a
   later call.
2. It waits for the new files to be complete: all present, none open for
   writing, unchanged across two samples. Up to 5 s; otherwise this call
   runs on the old child and the next one checks again.
3. It stops **its own child only**, then respawns as above. The restart
   notice says `binary changed`.

Nothing runs in the background and nothing is stopped while a build is
writing, so the build never races `mcp-sitter` over a half-written file.

On Windows a running binary is locked, so a build that writes it in place
fails while the child runs. There are three ways out: run the child from a
shadow copy (`--shadow-copy`; the hub does this by default), so the build
output is never locked at all; call `sitter_kill` before the build; or
deploy the way `mcp-sitter` handles by itself: rename the old file aside,
then copy the new one in (Windows allows renaming a running binary; the
running process keeps the renamed file, and the next spawn loads the new
one).

When a new build is found but cannot be used yet — still being written, or
other calls are running on the current one — the call runs on the previous
build and its response starts with a `[mcp-sitter]` line saying so, so the
AI never mistakes old behavior for its fix. On Linux and macOS a running binary can be replaced by a new file
(unlinked or renamed over); a build that rewrites it in place may fail with
"Text file busy" and then needs `sitter_kill` too.

The default watch is the child executable and `<name>.dll` beside it (a
framework-dependent .NET app keeps its code in the .dll, and an incremental
build may leave the exe untouched). When the child command is an
interpreter (`node`, `python`, `uv`, `dotnet`, …) nothing is watched by
default — pass `--reload-on` with your build output (`--reload-on dist`;
a directory stands for every file in it, so point it at the build output,
not at `node_modules`).

### `sitter_kill`

Stops the child `mcp-sitter` spawned, which unlocks its binary. Other
processes running the same executable (matched by `MainModule.FileName`,
case-insensitive, full path) are listed in the report but **not** stopped:
they may be other sessions' servers, and a client that loses its server may
never start it again. `others: true` stops them too — except when the child
command is an interpreter such as `node` or `python`, where "the same
executable" would include unrelated programs, possibly the MCP client
itself.

### Requests in flight

`mcp-sitter` tracks every client request it forwards to the child until the
child answers. When the child exits or is stopped first, each such request
gets an error response right away (a `tools/list` is answered from the
cache), so the client never waits for its own timeout. An answer the child
wrote just before exiting is still delivered.

### Hub

`mcp-sitter --hub` (registered with `mcp-sitter add --hub`, as `sitter-dev`)
runs no server of its own. Its tools:

| Tool | What it does |
| ---- | ------------ |
| `sitter_attach` | `name`, `command`, optional `args`, `cwd`, `env`, `reload_on`, `shadow_copy` (default true), `remember` (default true). Starts the server behind its own `mcp-sitter` child, initializes it with the client's own `initialize` params, and announces its tools with `notifications/tools/list_changed`. The result lists the exposed names, a ready-made ToolSearch `select:` query to load them (Claude Code shows new tools as deferred), and **the server's own instructions**, which a client would otherwise show the model only at connect time. Attaching a name again replaces it. |
| `sitter_detach` | Stops a server, removes its tools and forgets it for the project (`forget: false` keeps it remembered). |
| `sitter_list` | Attached servers, their commands, which exposed name maps to which tool, what is remembered, and how each remembered server's attach at start went. |

Why a hub: Claude Code does not load a server added while it runs (not even
from `/mcp`), but it does re-read the tool list of a server it already has
when that server sends `tools/list_changed`. The hub is that server.

- **Shadow copy by default.** An attached server runs from a copy of its
  directory, so its build output is never locked: a plain `dotnet build` (or
  any build that overwrites files in place) just works, and the next call runs
  the new build. Turn it off with `shadow_copy: false` for a server that finds
  files relative to its own location outside its directory.
- **Remembered per project.** Attachments are stored (arguments and env) in
  the per-user data folder, keyed by the directory the client runs in, and
  attached again automatically when the next session starts; the hub's
  instructions name them. `remember: false` or `sitter_detach` opts out.
- **Tool names** are `<name>_<tool>` (a tool already named `<name>_…` keeps
  its name), so Claude Code shows `mcp__sitter-dev__<name>_<tool>`. Tool names are capped at 64 characters,
  so a longer one is cut and ends in a short hash of its full name; it stays
  the same across reloads, and `sitter_list` shows the mapping. Each server
  also gets `<name>_sitter_status`, `<name>_sitter_kill`,
  `<name>_sitter_binary_info` and `<name>_sitter_child_stderr`.
- **Per-server behavior** — reload on change, errors for requests the server
  died on, restart notices, the stderr ring — comes from that server's own
  `mcp-sitter` child, unchanged.
- **Requests from a server to the client** (sampling, roots, elicitation)
  pass through with ids that stay unique across servers.
- **Limits.** Only tools are merged: a server's prompts and resources are
  not exposed through the hub.

### During downtime

While the child is down, incoming `tools/call` requests trigger a lazy
respawn (which may take a moment). Incoming `tools/list` requests are served
from the bridge's cache (merged with the built-in tools) if the spawn fails,
so the AI's tool roster stays stable.

## Limitations

- **Stateful children lose state across restart.** Session ids,
  subscriptions, in-memory caches, resource watches, etc. are gone after
  respawn. For pure "tools over data" servers this is a non-issue.
- **Script servers report the interpreter as "the binary".** For
  `node server.js` and the like, `sitter_binary_info` and the restart
  notice's version/age describe `node`, not your code, and a new build is
  only noticed with `--reload-on`.
- **A new build is noticed at the next tool call**, not when it lands. A
  reload is postponed while requests are in flight to the child.
- **Only `tools/list_changed` is actively surfaced.** Changes to
  `resources/*` or `prompts/*` pass through transparently but the bridge
  does not issue a `notifications/resources/list_changed` after respawn on
  its own (yet).
- **Platform coverage.** v0.2.0 ships `win32-x64`, `linux-x64`, and
  `darwin-arm64` (Apple Silicon) native binaries. Intel Mac is not
  shipped — the `macos-13` GHA runner pool is too contended to
  practically build against. The Windows binary is Authenticode-signed
  by the project's code-signing cert via Azure Key Vault; Linux and
  macOS binaries ship unsigned (neither OS has equivalent mandatory
  signature verification, and all three platforms are covered by npm
  SLSA provenance).
- **One child per bridge instance.** The child command is fixed at bridge
  startup; to supervise multiple MCP servers, register multiple `mcp-sitter`
  instances with different names, or attach them all to the [hub](#hub).
- **Restart notice is only injected when the response has a `result.content`
  array** (= tool-level response). JSON-RPC error responses (the `error`
  field at the envelope level) are forwarded unchanged; the notification
  via `notifications/tools/list_changed` still reaches the client infra.

## Project layout

```
mcp-sitter/
  Program.cs                        entry + CLI help
  Sitter.cs                         core: stdio pumps, message routing,
                                    child lifecycle, lazy spawn +
                                    handshake replay, restart notice,
                                    tools-diff, stderr ring buffer
  SitterTools.cs                    sitter_status / sitter_kill /
                                    sitter_binary_info / sitter_child_stderr
                                    tool definitions
  SitterConfig.cs                   CLI argument parsing
  Log.cs                            stderr logger
  McpSitter.csproj                  net9.0 console app (AOT-published)
  Build.ps1                         local dev build
  .github/workflows/ci.yml          Pester matrix (win/linux/macOS)
  .github/workflows/release.yml     AOT build → Azure KV sign (Windows)
                                    → npm publish + GitHub release
  test/
    McpSitter.Tests.ps1             Pester v5 integration tests
    FakeMcp/                        minimal stdio MCP server for tests
  npm/
    package.json                    umbrella mcp-sitter; optionalDependencies
                                    pin all 3 platform sub-packages
    bin/cli.mjs                     resolves mcp-sitter-<platform>-<arch>
                                    at runtime via require.resolve
    platforms/
      win32-x64/     package.json   @ytsuda/mcp-sitter-win32-x64 (os/cpu pinned);
                                    bin/mcp-sitter.exe populated by CI
      linux-x64/     package.json   @ytsuda/mcp-sitter-linux-x64; bin/ populated by CI
      darwin-arm64/  package.json   @ytsuda/mcp-sitter-darwin-arm64; bin/ populated by CI
```

The .NET namespace and types are `McpSitter` / `Sitter` (PascalCase, .NET
convention). The binary and user-facing CLI name are `mcp-sitter`
(kebab-case, ecosystem convention).

## License

MIT — see [LICENSE](LICENSE).
