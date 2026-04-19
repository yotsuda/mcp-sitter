using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace McpSitter;

public sealed class Sitter
{
    readonly SitterConfig _config;

    readonly Channel<string> _toClient = Channel.CreateUnbounded<string>();

    string? _cachedInitializeLine;
    string? _cachedClientInitializeIdJson;
    string? _cachedInitializedLine;

    JsonArray? _childTools;

    // stats
    readonly DateTime _startedUtc = DateTime.UtcNow;
    int _killCount;
    DateTime _lastKillUtc;
    string? _lastKillReason;
    int _spawnCount;
    string _childState = "not-started";

    // child process
    Process? _child;
    readonly SemaphoreSlim _childStdinLock = new(1, 1);
    readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    readonly SemaphoreSlim _initGate = new(1, 1);
    volatile bool _childReady;
    volatile bool _childAlive;
    int _childGeneration;
    int _expectedKill;

    // child stderr ring buffer (single writer = ChildErrorLoop, no lock needed
    // thanks to atomic ref writes; class entries avoid struct tearing on reader)
    const int StderrBufferSize = 1000;
    readonly StderrEntry?[] _stderrBuf = new StderrEntry?[StderrBufferSize];
    int _stderrHead;
    int _stderrCount;

    // internal request routing
    long _nextInternalIdCounter;
    readonly Dictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();
    readonly object _pendingLock = new();

    // client tools/list ids pending merge
    readonly HashSet<string> _pendingClientToolsList = new();
    readonly object _toolsListLock = new();

    // restart notice tracking
    DateTime? _spawnStartedUtc;
    TimeSpan? _lastStartupDuration;
    int? _previousExitCode;
    TimeSpan? _previousLifetime;
    volatile bool _announceRestart;
    string? _lastToolsDiffSummary;

    CancellationTokenSource? _shutdownCts;

    public Sitter(SitterConfig config) { _config = config; }

    public async Task RunAsync(CancellationToken ct)
    {
        _shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _shutdownCts.Token;

        Log.Info("mcp-sitter starting");
        Log.Info($"child: {_config.ChildCommand} {string.Join(" ", _config.ChildArgs)}");

        var writer = Task.Run(() => ClientWriterLoop(token), token);
        var reader = Task.Run(() => ClientReaderLoop(token), token);

        await Task.WhenAny(reader, writer);
        _shutdownCts.Cancel();
        try { await KillChildAsync(); } catch { }
        try { _toClient.Writer.TryComplete(); } catch { }
        try { await Task.WhenAll(reader, writer); } catch { }
        Log.Info("shutdown complete");
    }

    // =================================================================
    // client stdio
    // =================================================================

    async Task ClientReaderLoop(CancellationToken ct)
    {
        using var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        while (!ct.IsCancellationRequested)
        {
            string? line;
            try { line = await reader.ReadLineAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log.Error($"client read: {ex.Message}"); break; }
            if (line == null) { Log.Info("client stdin closed"); break; }
            if (line.Length == 0) continue;
            try { await HandleClientLineAsync(line, ct); }
            catch (Exception ex) { Log.Error($"handle client: {ex}"); }
        }
    }

    async Task ClientWriterLoop(CancellationToken ct)
    {
        var stdout = Console.OpenStandardOutput();
        try
        {
            await foreach (var line in _toClient.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                await stdout.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stdout.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warn($"client write: {ex.Message}"); }
    }

    Task SendToClientAsync(string line) => _toClient.Writer.WriteAsync(line).AsTask();
    Task SendToClientAsync(JsonObject obj) => SendToClientAsync(obj.ToJsonString());

    async Task SendClientErrorAsync(JsonNode? id, int code, string message)
    {
        var obj = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        };
        await SendToClientAsync(obj);
    }

    // =================================================================
    // child stdin
    // =================================================================

    async Task<bool> SendToChildAsync(string line)
    {
        await _childStdinLock.WaitAsync();
        try
        {
            var child = _child;
            if (child == null || child.HasExited)
            {
                Log.Warn("dropping message to child (not alive)");
                return false;
            }
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            await child.StandardInput.BaseStream.WriteAsync(bytes);
            await child.StandardInput.BaseStream.FlushAsync();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"child write failed: {ex.Message}");
            return false;
        }
        finally { _childStdinLock.Release(); }
    }

    Task<bool> SendToChildAsync(JsonObject obj) => SendToChildAsync(obj.ToJsonString());

    // =================================================================
    // client -> message handling
    // =================================================================

    async Task HandleClientLineAsync(string line, CancellationToken ct)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(line); }
        catch { Log.Warn("invalid JSON from client"); return; }
        if (node is not JsonObject obj) return;

        var method = obj["method"]?.GetValue<string>();
        var hasId = obj["id"] != null;

        // initialize: cache and spawn child (first time only)
        if (method == "initialize" && hasId)
        {
            _cachedInitializeLine = line;
            _cachedClientInitializeIdJson = obj["id"]!.ToJsonString();
            await EnsureChildStartedAsync(ct);
            await SendToChildAsync(line);
            return;
        }

        if (method == "notifications/initialized")
        {
            _cachedInitializedLine = line;
            await SendToChildAsync(line);
            return;
        }

        // tools/list: lazy spawn if child is down, then forward
        if (method == "tools/list" && hasId)
        {
            var idJson = obj["id"]!.ToJsonString();
            if (!_childReady)
            {
                await EnsureChildReadyAsync(ct);
                if (!_childReady)
                {
                    await RespondToolsListFromCacheAsync(obj);
                    return;
                }
            }
            lock (_toolsListLock) _pendingClientToolsList.Add(idJson);
            await SendToChildAsync(line);
            return;
        }

        // tools/call: handle sitter_* locally, lazy spawn for everything else
        if (method == "tools/call" && hasId)
        {
            var name = obj["params"]?["name"]?.GetValue<string>();
            if (name is SitterTools.Status or SitterTools.Kill or SitterTools.BinaryInfo or SitterTools.ChildStderr)
            {
                await HandleSitterToolCallAsync(obj, name, ct);
                return;
            }
            if (!_childReady)
            {
                await EnsureChildReadyAsync(ct);
                if (!_childReady)
                {
                    await SendClientErrorAsync(obj["id"], -32002,
                        "child MCP server failed to start. call sitter_status for details");
                    return;
                }
            }
            await SendToChildAsync(line);
            return;
        }

        // everything else: forward if child ready, otherwise error
        if (!_childReady)
        {
            await EnsureChildReadyAsync(ct);
            if (!_childReady)
            {
                if (hasId)
                    await SendClientErrorAsync(obj["id"], -32002, "child MCP server not ready");
                return;
            }
        }
        await SendToChildAsync(line);
    }

    async Task RespondToolsListFromCacheAsync(JsonObject request)
    {
        var merged = new JsonArray();
        if (_childTools != null)
            foreach (var t in _childTools)
                if (t != null) merged.Add((JsonNode?)t.DeepClone());
        foreach (var t in SitterTools.Definitions())
            if (t != null) merged.Add((JsonNode?)t.DeepClone());

        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = request["id"]?.DeepClone(),
            ["result"] = new JsonObject { ["tools"] = merged },
        };
        await SendToClientAsync(response);
    }

    // =================================================================
    // sitter tool calls
    // =================================================================

    async Task HandleSitterToolCallAsync(JsonObject request, string name, CancellationToken ct)
    {
        try
        {
            JsonObject result = name switch
            {
                SitterTools.Status => BuildStatusResult(),
                SitterTools.Kill => await HandleKillCallAsync(ct),
                SitterTools.BinaryInfo => BuildBinaryInfoResult(),
                SitterTools.ChildStderr => BuildChildStderrResult(request),
                _ => new JsonObject
                {
                    ["content"] = new JsonArray(TextContent("unknown sitter tool")),
                    ["isError"] = true,
                },
            };
            var response = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = request["id"]?.DeepClone(),
                ["result"] = result,
            };
            await SendToClientAsync(response);
        }
        catch (Exception ex)
        {
            await SendClientErrorAsync(request["id"], -32000, $"sitter tool failed: {ex.Message}");
        }
    }

    JsonObject BuildStatusResult()
    {
        var argsArr = new JsonArray();
        foreach (var a in _config.ChildArgs) argsArr.Add((JsonNode?)JsonValue.Create(a));

        var payload = new JsonObject
        {
            ["childCommand"] = _config.ChildCommand,
            ["childArgs"] = argsArr,
            ["workingDirectory"] = _config.WorkingDirectory,
            ["binaryPath"] = ResolveChildExePath(),
            ["binaryVersion"] = GetBinaryVersion(ResolveChildExePath()),
            ["childState"] = _childState,
            ["childAlive"] = _childAlive,
            ["childReady"] = _childReady,
            ["childPid"] = _child?.Id,
            ["childToolCount"] = _childTools?.Count ?? 0,
            ["spawnCount"] = _spawnCount,
            ["killCount"] = _killCount,
            ["lastKillUtc"] = _lastKillUtc == default ? null : _lastKillUtc.ToString("O"),
            ["lastKillReason"] = _lastKillReason,
            ["lastStartupMs"] = _lastStartupDuration.HasValue
                ? (int)_lastStartupDuration.Value.TotalMilliseconds
                : null,
            ["childUptimeSeconds"] = _childAlive && _spawnStartedUtc.HasValue
                ? (int)(DateTime.UtcNow - _spawnStartedUtc.Value).TotalSeconds
                : null,
            ["previousExitCode"] = _previousExitCode,
            ["previousLifetimeSeconds"] = _previousLifetime.HasValue
                ? (int)_previousLifetime.Value.TotalSeconds
                : null,
            ["lastToolsDiff"] = _lastToolsDiffSummary,
            ["sitterUptimeSeconds"] = (int)(DateTime.UtcNow - _startedUtc).TotalSeconds,
        };
        var text = payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        return new JsonObject { ["content"] = new JsonArray(TextContent(text)) };
    }

    async Task<JsonObject> HandleKillCallAsync(CancellationToken ct)
    {
        var killed = new JsonArray();

        // 1. Kill own child
        var childPid = _child?.Id;
        await KillChildAsync();
        if (childPid.HasValue)
            killed.Add((JsonNode?)new JsonObject { ["pid"] = childPid.Value, ["relation"] = "child" });

        // 2. Kill all other processes running the same exe
        var exePath = ResolveChildExePath();
        if (exePath != null)
        {
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (proc.Id == Environment.ProcessId) continue;
                    if (proc.Id == childPid) continue;
                    var modulePath = proc.MainModule?.FileName;
                    if (modulePath != null &&
                        string.Equals(Path.GetFullPath(modulePath), exePath, StringComparison.OrdinalIgnoreCase))
                    {
                        proc.Kill(entireProcessTree: true);
                        killed.Add((JsonNode?)new JsonObject { ["pid"] = proc.Id, ["relation"] = "same-path" });
                        Log.Info($"killed same-path process pid {proc.Id}");
                    }
                }
                catch { }
            }
        }

        _killCount++;
        _lastKillUtc = DateTime.UtcNow;
        _lastKillReason = "sitter_kill";

        var report = new JsonObject
        {
            ["killed"] = killed,
            ["path"] = exePath,
            ["count"] = killed.Count,
        };
        var text = report.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        return new JsonObject { ["content"] = new JsonArray(TextContent(text)) };
    }

    static string? GetBinaryVersion(string? exe)
    {
        if (exe == null || !File.Exists(exe)) return null;
        try
        {
            var ver = FileVersionInfo.GetVersionInfo(exe);
            var verStr = !string.IsNullOrWhiteSpace(ver.ProductVersion)
                ? ver.ProductVersion!.Trim()
                : ver.FileVersion?.Trim();
            return string.IsNullOrWhiteSpace(verStr) ? null : verStr;
        }
        catch { return null; }
    }

    string? ResolveChildExePath()
    {
        try
        {
            var cmd = _config.ChildCommand;
            var candidate = Path.IsPathRooted(cmd)
                ? cmd
                : Path.Combine(_config.WorkingDirectory ?? Environment.CurrentDirectory, cmd);
            return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
        }
        catch { return null; }
    }

    static JsonObject TextContent(string text) => new()
    {
        ["type"] = "text",
        ["text"] = text,
    };

    // =================================================================
    // child lifecycle
    // =================================================================

    // First spawn: triggered by client's initialize
    async Task EnsureChildStartedAsync(CancellationToken ct)
    {
        await _lifecycleGate.WaitAsync(ct);
        try
        {
            if (_childAlive) return;
            SpawnChildUnlocked();
        }
        finally { _lifecycleGate.Release(); }
    }

    // Lazy respawn: triggered by tools/call or tools/list when child is down
    async Task EnsureChildReadyAsync(CancellationToken ct)
    {
        if (_childReady) return;
        if (_cachedInitializeLine == null) return;

        await _initGate.WaitAsync(ct);
        try
        {
            if (_childReady) return;

            if (!_childAlive)
            {
                await _lifecycleGate.WaitAsync(ct);
                try { SpawnChildUnlocked(); }
                finally { _lifecycleGate.Release(); }
            }

            await ReplayHandshakeAsync();
            await RefreshToolsAsync();
        }
        catch (Exception ex)
        {
            Log.Error($"lazy spawn failed: {ex.Message}");
        }
        finally { _initGate.Release(); }
    }

    void SpawnChildUnlocked()
    {
        var psi = new ProcessStartInfo
        {
            FileName = _config.ChildCommand,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = _config.WorkingDirectory ?? Environment.CurrentDirectory,
        };
        foreach (var a in _config.ChildArgs) psi.ArgumentList.Add(a);

        Process child;
        try
        {
            child = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        }
        catch (Exception ex)
        {
            _childState = $"spawn-failed: {ex.Message}";
            Log.Error($"spawn failed: {ex.Message}");
            throw;
        }

        _child = child;
        _childAlive = true;
        _childReady = false;
        _childState = "running";
        _spawnStartedUtc = DateTime.UtcNow;
        Interlocked.Exchange(ref _expectedKill, 0);
        if (_spawnCount > 0) _announceRestart = true;
        _spawnCount++;
        var gen = Interlocked.Increment(ref _childGeneration);
        Log.Info($"child spawned; pid {child.Id} gen {gen} (total spawns: {_spawnCount})");

        _ = Task.Run(() => ChildReaderLoop(child, gen));
        _ = Task.Run(() => ChildErrorLoop(child, gen));
        _ = Task.Run(() => WatchChildExitAsync(child, gen));
    }

    async Task WatchChildExitAsync(Process child, int gen)
    {
        try { await child.WaitForExitAsync(); }
        catch { }
        var code = SafeExitCode(child);
        Log.Info($"child (gen {gen}) exited; code {code}");
        if (Volatile.Read(ref _childGeneration) == gen)
        {
            _previousExitCode = int.TryParse(code, out var ec) ? ec : null;
            _previousLifetime = _spawnStartedUtc.HasValue
                ? DateTime.UtcNow - _spawnStartedUtc.Value
                : null;

            var wasExpected = Interlocked.Exchange(ref _expectedKill, 0) != 0;
            if (!wasExpected)
            {
                _killCount++;
                _lastKillUtc = DateTime.UtcNow;
                _lastKillReason = ec == 0
                    ? "external (exit 0)"
                    : $"external/crash (exit {code})";
            }

            _childAlive = false;
            _childReady = false;
            _childState = $"exited ({code})";
            lock (_pendingLock)
            {
                foreach (var kv in _pending) kv.Value.TrySetCanceled();
                _pending.Clear();
            }
        }
    }

    static string SafeExitCode(Process p)
    {
        try { return p.ExitCode.ToString(); }
        catch { return "?"; }
    }

    async Task KillChildAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            var child = _child;
            if (child == null) { _childAlive = false; _childReady = false; return; }
            _childAlive = false;
            _childReady = false;
            _childState = "killing";
            Interlocked.Exchange(ref _expectedKill, 1);
            try
            {
                if (!child.HasExited)
                {
                    try { child.StandardInput.Close(); } catch { }
                    if (!child.WaitForExit(300))
                    {
                        try { child.Kill(entireProcessTree: true); } catch { }
                    }
                }
            }
            catch { }
            _child = null;
            _childState = "down";
        }
        finally { _lifecycleGate.Release(); }

        lock (_pendingLock)
        {
            foreach (var kv in _pending) kv.Value.TrySetCanceled();
            _pending.Clear();
        }
    }

    // =================================================================
    // lazy spawn handshake
    // =================================================================

    async Task ReplayHandshakeAsync()
    {
        if (_cachedInitializeLine == null) return;
        var cached = JsonNode.Parse(_cachedInitializeLine)!.AsObject();

        var id = NextInternalId();
        var idNode = JsonValue.Create(id);
        var req = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = idNode,
            ["method"] = "initialize",
            ["params"] = cached["params"]?.DeepClone(),
        };

        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var key = idNode.ToJsonString();
        lock (_pendingLock) _pending[key] = tcs;

        if (!await SendToChildAsync(req))
        {
            lock (_pendingLock) _pending.Remove(key);
            throw new InvalidOperationException("failed to send initialize to child");
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await tcs.Task.WaitAsync(timeout.Token); }
        catch { lock (_pendingLock) _pending.Remove(key); throw; }

        _childReady = true;
        _childState = "ready";
        if (_spawnStartedUtc.HasValue)
            _lastStartupDuration = DateTime.UtcNow - _spawnStartedUtc.Value;

        if (_cachedInitializedLine != null)
            await SendToChildAsync(_cachedInitializedLine);
    }

    async Task RefreshToolsAsync()
    {
        var id = NextInternalId();
        var idNode = JsonValue.Create(id);
        var req = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = idNode,
            ["method"] = "tools/list",
            ["params"] = new JsonObject(),
        };

        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var key = idNode.ToJsonString();
        lock (_pendingLock) _pending[key] = tcs;

        if (!await SendToChildAsync(req))
        {
            lock (_pendingLock) _pending.Remove(key);
            return;
        }

        JsonNode? resp;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { resp = await tcs.Task.WaitAsync(timeout.Token); }
        catch { lock (_pendingLock) _pending.Remove(key); return; }

        if (resp?["result"]?["tools"] is not JsonArray tools) return;

        var oldTools = _childTools;
        _childTools = tools.DeepClone().AsArray();

        var diff = ComputeToolsDiff(oldTools, _childTools);
        _lastToolsDiffSummary = FormatToolsDiff(diff);

        if (_lastToolsDiffSummary != null)
        {
            var notification = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/tools/list_changed",
            };
            await SendToClientAsync(notification);
            Log.Info($"tools list changed; notified client ({_lastToolsDiffSummary})");
        }
    }

    long NextInternalId() => -Interlocked.Increment(ref _nextInternalIdCounter);

    // =================================================================
    // child stdio pumps
    // =================================================================

    async Task ChildErrorLoop(Process child, int gen)
    {
        // Snapshot pid now — child.Id throws after the process exits, but we
        // may still be draining buffered stderr lines at that point.
        int pid;
        try { pid = child.Id; } catch { pid = -1; }
        try
        {
            string? line;
            while ((line = await child.StandardError.ReadLineAsync()) != null)
            {
                // Push to ring buffer for sitter_child_stderr. Single writer path
                // (this task per generation) + atomic ref write => no lock needed.
                _stderrBuf[_stderrHead] = new StderrEntry(line, gen, pid, DateTime.UtcNow);
                _stderrHead = (_stderrHead + 1) % _stderrBuf.Length;
                if (_stderrCount < _stderrBuf.Length) _stderrCount++;

                Log.Info($"[child:{gen}] {line}");
            }
        }
        catch { }
    }

    async Task ChildReaderLoop(Process child, int gen)
    {
        try
        {
            string? line;
            while ((line = await child.StandardOutput.ReadLineAsync()) != null)
            {
                if (line.Length == 0) continue;
                try { await HandleChildLineAsync(line); }
                catch (Exception ex) { Log.Error($"handle child: {ex}"); }
            }
        }
        catch (Exception ex) { Log.Warn($"child read: {ex.Message}"); }
    }

    async Task HandleChildLineAsync(string line)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(line); }
        catch { Log.Warn("invalid JSON from child"); return; }
        if (node is not JsonObject obj) return;

        var method = obj["method"]?.GetValue<string>();
        var isResponse = method == null && obj["id"] != null;

        if (isResponse)
        {
            var idJson = obj["id"]!.ToJsonString();

            TaskCompletionSource<JsonNode?>? tcs = null;
            lock (_pendingLock)
            {
                if (_pending.TryGetValue(idJson, out tcs))
                    _pending.Remove(idJson);
            }
            if (tcs != null)
            {
                tcs.TrySetResult(obj);
                return;
            }

            if (!_childReady && idJson == _cachedClientInitializeIdJson)
            {
                _childReady = true;
                _childState = "ready";
                if (_spawnStartedUtc.HasValue)
                    _lastStartupDuration = DateTime.UtcNow - _spawnStartedUtc.Value;
                Log.Info("child initialized (client-driven)");
            }

            bool merge;
            lock (_toolsListLock) merge = _pendingClientToolsList.Remove(idJson);
            if (merge && obj["result"]?["tools"] is JsonArray rawTools)
            {
                _childTools = rawTools.DeepClone().AsArray();
                var mergedArr = rawTools.DeepClone().AsArray();
                foreach (var t in SitterTools.Definitions())
                    if (t != null) mergedArr.Add((JsonNode?)t.DeepClone());
                obj["result"]!["tools"] = mergedArr;
                await SendToClientAsync(obj);
                return;
            }

            // Inject restart notice on the first tool/call response after a respawn.
            // Tool/call responses have result.content (a JsonArray); initialize /
            // tools/list / other response shapes do not, so they are skipped.
            if (_announceRestart
                && obj["result"] is JsonObject resObj
                && resObj["content"] is JsonArray contentArr)
            {
                _announceRestart = false;
                var notice = BuildRestartNotice();
                if (notice != null)
                {
                    contentArr.Insert(0, (JsonNode?)new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = notice,
                    });
                    await SendToClientAsync(obj);
                    return;
                }
            }
        }

        await SendToClientAsync(line);
    }

    // =================================================================
    // restart notice + binary info
    // =================================================================

    string? BuildRestartNotice()
    {
        var parts = new List<string>();

        var header = $"server restarted (spawn #{_spawnCount}";
        if (_child?.Id is int pid) header += $", pid {pid}";
        if (_lastStartupDuration is TimeSpan startup)
            header += $", startup {startup.TotalSeconds:0.0}s";
        header += ")";
        parts.Add(header);

        var exe = ResolveChildExePath();
        if (exe != null) parts.Add($"path: {exe}");
        if (exe != null && File.Exists(exe))
        {
            try
            {
                var fi = new FileInfo(exe);
                var age = HumanizeAge(DateTime.UtcNow - fi.LastWriteTimeUtc);
                string binaryPart;
                try
                {
                    var ver = FileVersionInfo.GetVersionInfo(exe);
                    var verStr = !string.IsNullOrWhiteSpace(ver.ProductVersion)
                        ? ver.ProductVersion!.Trim()
                        : ver.FileVersion?.Trim();
                    binaryPart = !string.IsNullOrWhiteSpace(verStr)
                        ? $"binary v{verStr} built {age}"
                        : $"binary built {age}";
                }
                catch
                {
                    binaryPart = $"binary built {age}";
                }
                parts.Add(binaryPart);
            }
            catch { }
        }

        if (_previousExitCode.HasValue || _previousLifetime.HasValue)
        {
            var prev = "previous: ";
            if (_previousExitCode.HasValue)
            {
                var code = _previousExitCode.Value;
                prev += code == 0 ? "exit 0" : $"\u26A0 crashed (exit {code})";
            }
            else prev += "unknown exit";
            if (_previousLifetime.HasValue)
                prev += $" after {FormatShortDuration(_previousLifetime.Value)}";
            parts.Add(prev);
        }

        if (_lastToolsDiffSummary != null)
            parts.Add($"tools: {_lastToolsDiffSummary}");

        return "[mcp-sitter] " + string.Join(". ", parts) + ".";
    }

    JsonObject BuildChildStderrResult(JsonObject request)
    {
        var args = request["params"]?["arguments"] as JsonObject;
        var lines = args?["lines"]?.GetValue<int>() ?? 200;
        lines = Math.Clamp(lines, 1, _stderrBuf.Length);
        var sinceSpawn = args?["since_spawn"]?.GetValue<bool>() ?? false;
        int? onlyGen = sinceSpawn ? Volatile.Read(ref _childGeneration) : null;

        // Snapshot the ring. The reader may observe _stderrHead / _stderrCount
        // in a slightly inconsistent state relative to each other; the worst
        // case is that one or two entries at the boundary are missed or
        // duplicated — acceptable for a diagnostics tool.
        var head = _stderrHead;
        var count = _stderrCount;
        var capacity = _stderrBuf.Length;
        var start = (head - count + capacity) % capacity;

        var collected = new List<StderrEntry>(count);
        for (int i = 0; i < count; i++)
        {
            var e = _stderrBuf[(start + i) % capacity];
            if (e == null) continue;
            if (onlyGen.HasValue && e.Gen != onlyGen.Value) continue;
            collected.Add(e);
        }
        var offset = Math.Max(0, collected.Count - lines);
        var tail = collected.GetRange(offset, collected.Count - offset);

        string text;
        if (tail.Count == 0)
        {
            text = "(no child stderr in buffer)";
        }
        else
        {
            var sb = new StringBuilder();
            int? prevGen = null;
            foreach (var e in tail)
            {
                if (prevGen.HasValue && e.Gen != prevGen.Value)
                {
                    sb.Append("----- child respawn (gen ").Append(e.Gen)
                      .Append(", pid ").Append(e.Pid).Append(") -----\n");
                }
                sb.Append(e.Line).Append('\n');
                prevGen = e.Gen;
            }
            if (sb.Length > 0 && sb[^1] == '\n') sb.Length--;
            text = sb.ToString();
        }
        return new JsonObject { ["content"] = new JsonArray(TextContent(text)) };
    }

    JsonObject BuildBinaryInfoResult()
    {
        var exe = ResolveChildExePath();
        var payload = new JsonObject
        {
            ["path"] = exe,
            ["exists"] = exe != null && File.Exists(exe),
        };

        if (exe != null && File.Exists(exe))
        {
            try
            {
                var fi = new FileInfo(exe);
                payload["sizeBytes"] = fi.Length;
                payload["mtime"] = fi.LastWriteTimeUtc.ToString("O");
                payload["mtimeHuman"] = HumanizeAge(DateTime.UtcNow - fi.LastWriteTimeUtc);
            }
            catch (Exception ex)
            {
                payload["fileInfoError"] = ex.Message;
            }

            try
            {
                var ver = FileVersionInfo.GetVersionInfo(exe);
                if (!string.IsNullOrWhiteSpace(ver.FileVersion))
                    payload["fileVersion"] = ver.FileVersion;
                if (!string.IsNullOrWhiteSpace(ver.ProductVersion))
                    payload["productVersion"] = ver.ProductVersion;
                if (!string.IsNullOrWhiteSpace(ver.ProductName))
                    payload["productName"] = ver.ProductName;
                if (!string.IsNullOrWhiteSpace(ver.FileDescription))
                    payload["fileDescription"] = ver.FileDescription;
                if (!string.IsNullOrWhiteSpace(ver.CompanyName))
                    payload["companyName"] = ver.CompanyName;
                if (!string.IsNullOrWhiteSpace(ver.LegalCopyright))
                    payload["copyright"] = ver.LegalCopyright;
                payload["isDebug"] = ver.IsDebug;
            }
            catch (Exception ex)
            {
                payload["versionInfoError"] = ex.Message;
            }
        }

        var text = payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        return new JsonObject { ["content"] = new JsonArray(TextContent(text)) };
    }

    readonly record struct ToolsDiff(List<string> Added, List<string> Removed, List<string> Modified);

    static ToolsDiff ComputeToolsDiff(JsonArray? before, JsonArray? after)
    {
        var beforeMap = ToolsByName(before);
        var afterMap = ToolsByName(after);
        var added = new List<string>();
        var removed = new List<string>();
        var modified = new List<string>();

        foreach (var k in afterMap.Keys)
            if (!beforeMap.ContainsKey(k)) added.Add(k);
        foreach (var k in beforeMap.Keys)
            if (!afterMap.ContainsKey(k)) removed.Add(k);
        foreach (var k in beforeMap.Keys)
            if (afterMap.TryGetValue(k, out var aft)
                && beforeMap[k].ToJsonString() != aft.ToJsonString())
                modified.Add(k);

        added.Sort(StringComparer.Ordinal);
        removed.Sort(StringComparer.Ordinal);
        modified.Sort(StringComparer.Ordinal);
        return new ToolsDiff(added, removed, modified);
    }

    static Dictionary<string, JsonObject> ToolsByName(JsonArray? arr)
    {
        var map = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (arr == null) return map;
        foreach (var t in arr)
            if (t is JsonObject obj && obj["name"]?.GetValue<string>() is string name)
                map[name] = obj;
        return map;
    }

    static string? FormatToolsDiff(ToolsDiff diff)
    {
        if (diff.Added.Count == 0 && diff.Removed.Count == 0 && diff.Modified.Count == 0)
            return null;
        var parts = new List<string>();
        if (diff.Added.Count > 0)
            parts.Add($"+{diff.Added.Count} added ({string.Join(", ", diff.Added)})");
        if (diff.Removed.Count > 0)
            parts.Add($"-{diff.Removed.Count} removed ({string.Join(", ", diff.Removed)})");
        if (diff.Modified.Count > 0)
            parts.Add($"{diff.Modified.Count} schema changed ({string.Join(", ", diff.Modified)})");
        return string.Join(", ", parts);
    }

    static string HumanizeAge(TimeSpan age)
    {
        if (age.TotalSeconds < 1) return "just now";
        if (age.TotalSeconds < 60) return $"{age.TotalSeconds:0}s ago";
        if (age.TotalMinutes < 60) return $"{age.TotalMinutes:0} min ago";
        if (age.TotalHours < 24) return $"{age.TotalHours:0.0} hours ago";
        return $"{age.TotalDays:0.0} days ago";
    }

    static string FormatShortDuration(TimeSpan d)
    {
        if (d.TotalSeconds < 60) return $"{d.TotalSeconds:0.0}s";
        if (d.TotalMinutes < 60) return $"{d.TotalMinutes:0}m";
        return $"{d.TotalHours:0.0}h";
    }

    sealed record StderrEntry(string Line, int Gen, int Pid, DateTime Utc);
}
