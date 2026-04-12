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

    // internal request routing
    long _nextInternalIdCounter;
    readonly Dictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();
    readonly object _pendingLock = new();

    // client tools/list ids pending merge
    readonly HashSet<string> _pendingClientToolsList = new();
    readonly object _toolsListLock = new();

    // file watcher
    FileSystemWatcher? _watcher;
    CancellationTokenSource? _debounceCts;
    readonly object _debounceLock = new();

    CancellationTokenSource? _shutdownCts;

    public Sitter(SitterConfig config) { _config = config; }

    public async Task RunAsync(CancellationToken ct)
    {
        _shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _shutdownCts.Token;

        Log.Info("mcp-sitter starting");
        Log.Info($"child: {_config.ChildCommand} {string.Join(" ", _config.ChildArgs)}");
        if (_config.WatchPath != null)
            Log.Info($"watch: {_config.WatchPath} (debounce {_config.DebounceMs}ms)");
        else
            Log.Warn("no watch path resolved; file-watch disabled");

        SetupWatcher();

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
            if (name is SitterTools.Status or SitterTools.Kill)
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
                if (t != null) merged.Add(t.DeepClone());
        foreach (var t in SitterTools.Definitions())
            if (t != null) merged.Add(t.DeepClone());

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
        foreach (var a in _config.ChildArgs) argsArr.Add(JsonValue.Create(a));

        var payload = new JsonObject
        {
            ["childCommand"] = _config.ChildCommand,
            ["childArgs"] = argsArr,
            ["watchPath"] = _config.WatchPath,
            ["workingDirectory"] = _config.WorkingDirectory,
            ["childState"] = _childState,
            ["childAlive"] = _childAlive,
            ["childReady"] = _childReady,
            ["childToolCount"] = _childTools?.Count ?? 0,
            ["spawnCount"] = _spawnCount,
            ["killCount"] = _killCount,
            ["lastKillUtc"] = _lastKillUtc == default ? null : _lastKillUtc.ToString("O"),
            ["lastKillReason"] = _lastKillReason,
            ["uptimeSeconds"] = (int)(DateTime.UtcNow - _startedUtc).TotalSeconds,
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
            killed.Add(new JsonObject { ["pid"] = childPid.Value, ["relation"] = "child" });

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
                        killed.Add(new JsonObject { ["pid"] = proc.Id, ["relation"] = "same-path" });
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

    string? ResolveChildExePath()
    {
        try
        {
            var cmd = _config.ChildCommand;
            var candidate = Path.IsPathRooted(cmd)
                ? cmd
                : Path.Combine(_config.WorkingDirectory ?? Environment.CurrentDirectory, cmd);
            return File.Exists(candidate) ? Path.GetFullPath(candidate) : _config.WatchPath;
        }
        catch { return _config.WatchPath; }
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

        var before = _childTools?.ToJsonString();
        _childTools = tools.DeepClone().AsArray();
        var after = _childTools?.ToJsonString();

        if (before != after)
        {
            var notification = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/tools/list_changed",
            };
            await SendToClientAsync(notification);
            Log.Info("tools list changed; notified client");
        }
    }

    long NextInternalId() => -Interlocked.Increment(ref _nextInternalIdCounter);

    // =================================================================
    // child stdio pumps
    // =================================================================

    async Task ChildErrorLoop(Process child, int gen)
    {
        try
        {
            string? line;
            while ((line = await child.StandardError.ReadLineAsync()) != null)
                Log.Info($"[child:{gen}] {line}");
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
                Log.Info("child initialized (client-driven)");
            }

            bool merge;
            lock (_toolsListLock) merge = _pendingClientToolsList.Remove(idJson);
            if (merge && obj["result"]?["tools"] is JsonArray rawTools)
            {
                _childTools = rawTools.DeepClone().AsArray();
                var mergedArr = rawTools.DeepClone().AsArray();
                foreach (var t in SitterTools.Definitions())
                    if (t != null) mergedArr.Add(t.DeepClone());
                obj["result"]!["tools"] = mergedArr;
                await SendToClientAsync(obj);
                return;
            }
        }

        await SendToClientAsync(line);
    }

    // =================================================================
    // file watcher (kill only, no respawn)
    // =================================================================

    void SetupWatcher()
    {
        if (_config.WatchPath == null) return;
        var dir = Path.GetDirectoryName(_config.WatchPath);
        var file = Path.GetFileName(_config.WatchPath);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(file)) return;
        try
        {
            _watcher = new FileSystemWatcher(dir, file)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size |
                               NotifyFilters.CreationTime | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnBinaryChanged;
            _watcher.Created += OnBinaryChanged;
            _watcher.Renamed += (s, e) => OnBinaryChanged(s, e);
        }
        catch (Exception ex) { Log.Warn($"watcher setup failed: {ex.Message}"); }
    }

    void OnBinaryChanged(object sender, FileSystemEventArgs e)
    {
        if (!_childAlive) return;
        Log.Info($"binary change: {e.ChangeType} {e.Name}; killing child");
        lock (_debounceLock)
        {
            _debounceCts?.Cancel();
            _debounceCts = new CancellationTokenSource();
            var token = _debounceCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(_config.DebounceMs, token);
                    _lastKillReason = "binary changed";
                    _killCount++;
                    _lastKillUtc = DateTime.UtcNow;
                    await KillChildAsync();
                    Log.Info("child killed by file watcher");
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Log.Error($"watcher kill: {ex}"); }
            }, token);
        }
    }
}
