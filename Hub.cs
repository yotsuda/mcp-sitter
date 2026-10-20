using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using System.Text.Json.Nodes;

namespace McpSitter;

// Hub mode (`mcp-sitter --hub`): one registration that MCP servers under
// development are attached to while the client runs.
//
// A server added to Claude Code mid-session is not loaded until the client
// starts again, but a registered server whose tool list changes is re-read
// at once. So the hub is registered once, and sitter_attach starts a server
// behind it and announces the new tools with notifications/tools/list_changed:
// they are callable in the same conversation, with no restart.
//
// Each attached server runs behind its own mcp-sitter child process. That
// child already does everything per server — reload on a new build, errors
// for requests the server died on, the stderr ring, restart notices — so the
// hub only merges the tool lists under a per-server prefix and routes calls.
// The hub is the MCP client of each child; it forwards the client's own
// initialize params so the server sees the real client.
public sealed class Hub
{
    public const string Attach = "sitter_attach";
    public const string Detach = "sitter_detach";
    public const string List = "sitter_list";

    readonly HubConfig _config;
    readonly Channel<string> _toClient = Channel.CreateUnbounded<string>();
    readonly object _lock = new();
    readonly Dictionary<string, HubChild> _children = new(StringComparer.OrdinalIgnoreCase);
    // exposed tool name -> (child, the tool's own name)
    readonly Dictionary<string, (HubChild Child, string Tool)> _routes = new(StringComparer.Ordinal);
    // client request id -> child serving it (for notifications/cancelled)
    readonly Dictionary<string, HubChild> _clientRequests = new();
    // id the hub gave a child's request to the client -> (child, the child's id)
    readonly Dictionary<string, (HubChild Child, JsonNode Id)> _childRequests = new();
    long _nextId;
    JsonNode? _clientInitializeParams;

    public Hub(HubConfig config) { _config = config; }

    public async Task RunAsync(CancellationToken ct)
    {
        Log.Info("mcp-sitter hub starting");
        var writer = Task.Run(() => ClientWriterLoop(ct), ct);
        var reader = Task.Run(() => ClientReaderLoop(ct), ct);
        await Task.WhenAny(reader, writer);
        List<HubChild> children;
        lock (_lock) children = _children.Values.ToList();
        foreach (var c in children) c.Stop();
        _toClient.Writer.TryComplete();
        try { await Task.WhenAll(reader, writer); } catch { }
        Log.Info("hub shutdown complete");
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
            try { line = await reader.ReadLineAsync(ct); }
            catch { break; }
            if (line == null) { Log.Info("client stdin closed"); break; }
            if (line.Length == 0) continue;
            try { await HandleClientLineAsync(line); }
            catch (Exception ex) { Log.Error($"handle client: {ex}"); }
        }
    }

    async Task ClientWriterLoop(CancellationToken ct)
    {
        var stdout = Console.OpenStandardOutput();
        try
        {
            await foreach (var line in _toClient.Reader.ReadAllAsync(ct))
            {
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                await stdout.WriteAsync(bytes, ct);
                await stdout.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warn($"client write: {ex.Message}"); }
    }

    internal Task SendToClientAsync(JsonObject obj) => _toClient.Writer.WriteAsync(obj.ToJsonString()).AsTask();

    Task RespondAsync(JsonNode? id, JsonObject result) => SendToClientAsync(new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result,
    });

    internal Task RespondErrorAsync(JsonNode? id, int code, string message) => SendToClientAsync(new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    });

    Task NotifyToolsChangedAsync() => SendToClientAsync(new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["method"] = "notifications/tools/list_changed",
    });

    async Task HandleClientLineAsync(string line)
    {
        if (JsonNode.Parse(line) is not JsonObject obj) return;
        var method = obj["method"]?.GetValue<string>();
        var id = obj["id"];

        if (method == null)
        {
            // the client's answer to a request a child sent it
            if (id == null) return;
            (HubChild Child, JsonNode Id) target;
            lock (_lock)
            {
                if (!_childRequests.Remove(id.ToJsonString(), out target)) return;
            }
            obj["id"] = target.Id.DeepClone();
            await target.Child.SendAsync(obj);
            return;
        }

        switch (method)
        {
            case "initialize":
                _clientInitializeParams = obj["params"]?.DeepClone();
                var remembered = LoadMemory();
                var instructions = Instructions.Replace("{hub}", _config.HubName);
                if (remembered.Count > 0)
                    instructions += "\n\nRemembered for this project and being attached now (tools arrive with " +
                        "notifications/tools/list_changed): " +
                        string.Join(", ", remembered.Select(r => $"{r.Key} ({r.Value["command"]})")) +
                        ". sitter_list shows how each attach went; sitter_detach forgets one.";
                await RespondAsync(id, new JsonObject
                {
                    ["protocolVersion"] = obj["params"]?["protocolVersion"]?.DeepClone() ?? "2025-06-18",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = true } },
                    ["serverInfo"] = new JsonObject { ["name"] = "mcp-sitter-hub", ["version"] = Version() },
                    ["instructions"] = instructions,
                });
                if (remembered.Count > 0) _ = Task.Run(() => AttachRememberedAsync(remembered));
                return;
            case "notifications/initialized":
                return;
            case "ping":
                await RespondAsync(id, new JsonObject());
                return;
            case "tools/list":
                await RespondAsync(id, new JsonObject { ["tools"] = BuildToolList() });
                return;
            case "tools/call":
                await HandleToolCallAsync(obj);
                return;
            case "notifications/cancelled":
                var requestId = obj["params"]?["requestId"]?.ToJsonString();
                HubChild? owner = null;
                if (requestId != null) lock (_lock) _clientRequests.Remove(requestId, out owner);
                if (owner != null) await owner.SendAsync(obj);
                return;
        }

        if (id != null) await RespondErrorAsync(id, -32601, $"method not found: {method}");
    }

    async Task HandleToolCallAsync(JsonObject request)
    {
        var id = request["id"];
        var name = request["params"]?["name"]?.GetValue<string>() ?? "";
        var args = request["params"]?["arguments"] as JsonObject ?? new JsonObject();

        if (name is Attach or Detach or List)
        {
            // Off the reader: an attach waits for a server to start, and the
            // calls to other servers should not queue behind it.
            _ = Task.Run(async () =>
            {
                JsonObject result;
                try
                {
                    result = name switch
                    {
                        Attach => await AttachAsync(args),
                        Detach => await DetachAsync(args),
                        _ => ListResult(),
                    };
                }
                catch (Exception ex) { result = ErrorResult(ex.Message); }
                await RespondAsync(id, result);
            });
            return;
        }

        (HubChild Child, string Tool) route;
        bool found;
        lock (_lock) found = _routes.TryGetValue(name, out route);
        if (!found)
        {
            await RespondErrorAsync(id, -32602, $"unknown tool {name}. Attached servers: {AttachedNames()}. Call {List} for their tools.");
            return;
        }

        var forwarded = (JsonObject)request.DeepClone();
        forwarded["params"]!["name"] = route.Tool;
        if (id != null) lock (_lock) _clientRequests[id.ToJsonString()] = route.Child;
        if (!await route.Child.ForwardClientRequestAsync(forwarded))
        {
            if (id != null) lock (_lock) _clientRequests.Remove(id.ToJsonString());
            await RespondErrorAsync(id, -32002, $"server '{route.Child.Name}' is not running. Attach it again with {Attach}.");
        }
    }

    // =================================================================
    // children -> client
    // =================================================================

    internal void ClientRequestDone(JsonNode id)
    {
        lock (_lock) _clientRequests.Remove(id.ToJsonString());
    }

    // A request from a child to the client (sampling, roots, elicitation)
    // gets an id unique across children.
    internal Task ForwardChildRequestAsync(HubChild child, JsonObject request)
    {
        var hubId = $"mcp-sitter-hub:{child.Name}:{Interlocked.Increment(ref _nextId)}";
        lock (_lock) _childRequests[JsonValue.Create(hubId).ToJsonString()] = (child, request["id"]!.DeepClone());
        request["id"] = hubId;
        return SendToClientAsync(request);
    }

    internal async Task ChildToolsChangedAsync(HubChild child)
    {
        try
        {
            await child.RefreshToolsAsync();
            Remap();
            await NotifyToolsChangedAsync();
        }
        catch (Exception ex) { Log.Warn($"{child.Name}: tools refresh failed: {ex.Message}"); }
    }

    internal async Task ChildExitedAsync(HubChild child)
    {
        bool current;
        lock (_lock)
        {
            current = _children.TryGetValue(child.Name, out var c) && ReferenceEquals(c, child);
            if (current) _children.Remove(child.Name);
            foreach (var key in _clientRequests.Where(kv => ReferenceEquals(kv.Value, child)).Select(kv => kv.Key).ToList())
                _clientRequests.Remove(key);
        }
        if (!current) return;
        Log.Warn($"{child.Name}: mcp-sitter child exited");
        Remap();
        await NotifyToolsChangedAsync();
    }

    // =================================================================
    // hub tools
    // =================================================================

    async Task<JsonObject> AttachAsync(JsonObject args, bool fromMemory = false)
    {
        var name = args["name"]?.GetValue<string>()?.Trim();
        var command = args["command"]?.GetValue<string>();
        if (string.IsNullOrEmpty(name) || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            return ErrorResult("name must be letters, digits and '-' (it prefixes every tool: <name>_<tool>)");
        if (string.IsNullOrEmpty(command))
            return ErrorResult("command is required: the server executable (or an interpreter such as node, with args)");
        if (_clientInitializeParams == null)
            return ErrorResult("the client has not initialized yet");

        var cwd = args["cwd"]?.GetValue<string>();
        if (cwd != null && !Directory.Exists(cwd)) return ErrorResult($"cwd not found: {cwd}");
        if (cwd != null) cwd = Path.GetFullPath(cwd);
        // A path is made absolute against cwd (or the hub's directory), so it
        // means the same thing to the child mcp-sitter and in the memory.
        if (command.Contains('/') || command.Contains('\\'))
        {
            command = Path.GetFullPath(command, cwd ?? Environment.CurrentDirectory);
            if (!File.Exists(command)) return ErrorResult($"command not found: {command}");
        }
        var shadowCopy = args["shadow_copy"]?.GetValue<bool>() ?? true;
        var remember = args["remember"]?.GetValue<bool>() ?? true;

        var sitterArgs = new List<string> { "--hub-child" };
        if (cwd != null) { sitterArgs.Add("--cwd"); sitterArgs.Add(cwd); }
        if (shadowCopy) sitterArgs.Add("--shadow-copy");
        if (args["reload_on"] is JsonArray reloadOn)
            foreach (var p in reloadOn)
                if (p?.GetValue<string>() is string path) { sitterArgs.Add("--reload-on"); sitterArgs.Add(path); }
        sitterArgs.Add("--");
        sitterArgs.Add(command);
        if (args["args"] is JsonArray childArgs)
            foreach (var a in childArgs)
                if (a != null) sitterArgs.Add(a.ToString());

        var env = new Dictionary<string, string>();
        if (args["env"] is JsonObject envObj)
            foreach (var (k, v) in envObj)
                if (v != null) env[k] = v.ToString();

        // Attaching a name again replaces it (e.g. a different command).
        HubChild? old;
        lock (_lock) _children.Remove(name, out old);
        old?.Stop();

        var child = new HubChild(this, name, sitterArgs, env);
        string? instructions;
        try
        {
            instructions = await child.StartAsync(_clientInitializeParams);
        }
        catch (Exception ex)
        {
            child.Stop();
            Remap();
            if (old != null) await NotifyToolsChangedAsync();
            var stderr = child.StderrTail();
            return ErrorResult($"'{name}' failed to start: {ex.Message}" + (stderr.Length > 0 ? $"\n--- stderr ---\n{stderr}" : ""));
        }

        lock (_lock) _children[name] = child;
        var exposed = Remap().Where(r => ReferenceEquals(r.Child, child)).ToList();
        await NotifyToolsChangedAsync();

        if (!fromMemory)
        {
            var stored = (JsonObject)args.DeepClone();
            stored["name"] = name;
            stored["command"] = command;
            if (cwd != null) stored["cwd"] = cwd;
            stored.Remove("remember");
            if (remember) Remember(name, stored); else Forget(name);
        }

        var hubPrefix = $"mcp__{_config.HubName}__";
        var sb = new StringBuilder();
        sb.Append($"Attached '{name}' (pid {child.Pid}): {exposed.Count} tools, callable now as {hubPrefix}<tool>:\n");
        foreach (var r in exposed) sb.Append($"  {r.Exposed}  ->  {r.Tool}\n");
        sb.Append("Claude Code lists them as deferred tools; load them all at once with ToolSearch query \"select:")
          .Append(string.Join(",", exposed.Select(r => hubPrefix + r.Exposed))).Append("\".\n");
        if (Sitter.IsInterpreter(command))
            sb.Append(args["reload_on"] is JsonArray { Count: > 0 }
                ? "Rebuild or edit it as usual; the next call after a change in reload_on runs the new version."
                : "WARNING: it runs under an interpreter and no reload_on was given, so changes are never picked up. Attach it again with reload_on, e.g. [\"dist\"] (its build output) or its source directory.");
        else
            sb.Append(shadowCopy
                ? "It runs from a shadow copy of its directory, so the build output is not locked: just rebuild, and the next call runs the new build."
                : $"It runs in place: on Windows its binary is locked, so call {name}_sitter_kill before a build that writes it in place.");
        sb.Append(remember
            ? " Remembered for this project: the hub attaches it again in the next session (sitter_detach forgets it)."
            : " Not remembered (remember=false).");
        if (!string.IsNullOrWhiteSpace(instructions))
            sb.Append($"\n\nThe server's own instructions (an MCP client shows these to the model at connect time; read them now):\n{instructions}");
        return TextResult(sb.ToString());
    }

    // =================================================================
    // per-project memory of attachments
    // =================================================================

    // Attachments are remembered per directory the client runs in (the
    // project), in the per-user data folder — never in the project itself.
    // The stored entries are sitter_attach arguments, env included.
    static string MemoryPath()
    {
        var cwd = Path.GetFullPath(Environment.CurrentDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (OperatingSystem.IsWindows()) cwd = cwd.ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cwd)))[..16].ToLowerInvariant();
        var root = Environment.GetEnvironmentVariable("MCP_SITTER_HUB_STATE");
        if (string.IsNullOrEmpty(root))
        {
            try { root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mcp-sitter", "hub"); }
            catch { root = Path.Combine(Path.GetTempPath(), "mcp-sitter", "hub"); }
        }
        return Path.Combine(root, hash + ".json");
    }

    readonly object _memoryLock = new();

    List<KeyValuePair<string, JsonObject>> LoadMemory()
    {
        lock (_memoryLock)
        {
            try
            {
                var path = MemoryPath();
                if (!File.Exists(path)) return new();
                if (JsonNode.Parse(File.ReadAllText(path))?["servers"] is not JsonObject servers) return new();
                return servers.Where(kv => kv.Value is JsonObject)
                    .Select(kv => new KeyValuePair<string, JsonObject>(kv.Key, (JsonObject)kv.Value!.DeepClone())).ToList();
            }
            catch (Exception ex) { Log.Warn($"hub memory unreadable: {ex.Message}"); return new(); }
        }
    }

    void UpdateMemory(Action<JsonObject> change)
    {
        lock (_memoryLock)
        {
            try
            {
                var path = MemoryPath();
                var root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;
                root ??= new JsonObject();
                root["cwd"] = Environment.CurrentDirectory;
                if (root["servers"] is not JsonObject servers) root["servers"] = servers = new JsonObject();
                change(servers);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, root.ToJsonString(new() { WriteIndented = true }));
            }
            catch (Exception ex) { Log.Warn($"hub memory not saved: {ex.Message}"); }
        }
    }

    void Remember(string name, JsonObject args) => UpdateMemory(s => s[name] = args);
    void Forget(string name) => UpdateMemory(s => s.Remove(name));

    readonly Dictionary<string, string> _startupResults = new(StringComparer.OrdinalIgnoreCase);

    async Task AttachRememberedAsync(List<KeyValuePair<string, JsonObject>> remembered)
    {
        foreach (var (name, args) in remembered)
        {
            JsonObject result;
            try { result = await AttachAsync(args, fromMemory: true); }
            catch (Exception ex) { result = ErrorResult(ex.Message); }
            var ok = result["isError"]?.GetValue<bool>() != true;
            var text = result["content"]?[0]?["text"]?.GetValue<string>() ?? "";
            lock (_lock) _startupResults[name] = ok ? "attached at start" : "failed at start: " + text;
            Log.Info($"remembered '{name}': {(ok ? "attached" : text)}");
        }
    }

    async Task<JsonObject> DetachAsync(JsonObject args)
    {
        var name = args["name"]?.GetValue<string>();
        var forget = args["forget"]?.GetValue<bool>() ?? true;
        HubChild? child = null;
        if (name != null) lock (_lock) _children.Remove(name, out child);
        var wasRemembered = name != null && LoadMemory().Any(r => string.Equals(r.Key, name, StringComparison.OrdinalIgnoreCase));
        if (forget && wasRemembered) Forget(LoadMemory().First(r => string.Equals(r.Key, name, StringComparison.OrdinalIgnoreCase)).Key);
        if (child == null)
            return wasRemembered && forget
                ? TextResult($"'{name}' was not attached; forgot it for this project.")
                : ErrorResult($"no attached server named '{name}'. Attached: {AttachedNames()}");
        child.Stop();
        Remap();
        await NotifyToolsChangedAsync();
        return TextResult($"Detached '{child.Name}'." + (forget ? (wasRemembered ? " Forgot it for this project." : "") : " Still remembered for this project."));
    }

    JsonObject ListResult()
    {
        List<HubChild> children;
        Dictionary<string, string> startup;
        lock (_lock)
        {
            children = _children.Values.ToList();
            startup = new Dictionary<string, string>(_startupResults, StringComparer.OrdinalIgnoreCase);
        }
        var memory = LoadMemory();
        var routes = Remap();
        var arr = new JsonArray();
        foreach (var c in children)
        {
            var tools = new JsonObject();
            foreach (var r in routes.Where(r => ReferenceEquals(r.Child, c))) tools[r.Exposed] = r.Tool;
            arr.Add((JsonNode?)new JsonObject
            {
                ["name"] = c.Name,
                ["command"] = c.Command,
                ["sitterPid"] = c.Pid,
                ["remembered"] = memory.Any(m => string.Equals(m.Key, c.Name, StringComparison.OrdinalIgnoreCase)),
                ["startup"] = startup.TryGetValue(c.Name, out var s) ? s : null,
                ["tools"] = tools,
            });
        }
        // Remembered but not running: it failed at start, or was detached with forget=false.
        var notRunning = new JsonArray();
        foreach (var m in memory.Where(m => !children.Any(c => string.Equals(c.Name, m.Key, StringComparison.OrdinalIgnoreCase))))
            notRunning.Add((JsonNode?)new JsonObject
            {
                ["name"] = m.Key,
                ["command"] = m.Value["command"]?.DeepClone(),
                ["startup"] = startup.TryGetValue(m.Key, out var s) ? s : null,
            });
        var payload = new JsonObject { ["attached"] = arr, ["memoryFile"] = MemoryPath() };
        if (notRunning.Count > 0) payload["rememberedNotRunning"] = notRunning;
        return TextResult(payload.ToJsonString(new() { WriteIndented = true }));
    }

    string AttachedNames()
    {
        lock (_lock) return _children.Count == 0 ? "(none)" : string.Join(", ", _children.Keys);
    }

    // =================================================================
    // tool naming
    // =================================================================

    // Rebuilds the exposed-name routes from every child's current tools.
    // Claude Code names a tool mcp__<server>__<tool> and the API caps a tool
    // name at 64 characters, so <name>_<tool> gets what is left after the
    // hub's own prefix; a longer one is cut and given a short hash of its
    // full name, so it stays unique and stable across reloads.
    List<(string Exposed, HubChild Child, string Tool)> Remap()
    {
        var budget = Math.Max(16, 64 - "mcp__".Length - _config.HubName.Length - "__".Length);
        var result = new List<(string, HubChild, string)>();
        lock (_lock)
        {
            _routes.Clear();
            foreach (var child in _children.Values.OrderBy(c => c.Name, StringComparer.Ordinal))
            {
                foreach (var tool in child.ToolNames())
                {
                    // A tool already named <name>_... keeps its name rather
                    // than becoming <name>_<name>_..., unless that clashes.
                    var exposed = tool.StartsWith(child.Name + "_", StringComparison.OrdinalIgnoreCase) && !_routes.ContainsKey(tool)
                        ? tool
                        : $"{child.Name}_{tool}";
                    if (exposed.Length > budget)
                    {
                        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(exposed)))[..6].ToLowerInvariant();
                        exposed = exposed[..(budget - 7)] + "_" + hash;
                    }
                    _routes[exposed] = (child, tool);
                    result.Add((exposed, child, tool));
                }
            }
        }
        return result;
    }

    JsonArray BuildToolList()
    {
        var tools = HubToolDefinitions();
        List<(string Exposed, HubChild Child, string Tool)> routes = Remap();
        foreach (var r in routes)
        {
            if (r.Child.ToolDefinition(r.Tool) is not JsonObject def) continue;
            def["name"] = r.Exposed;
            var desc = def["description"]?.GetValue<string>();
            def["description"] = $"[{r.Child.Name}] {desc}";
            tools.Add((JsonNode?)def);
        }
        return tools;
    }

    static JsonArray HubToolDefinitions() => new(
        (JsonNode?)new JsonObject
        {
            ["name"] = Attach,
            ["description"] = "Start an MCP server you are developing and make its tools callable in this conversation, with no client restart. They are exposed as <name>_<tool> (a tool already named <name>_... keeps its name). The result lists them, gives a ToolSearch \"select:...\" query to load them (Claude Code shows new tools as deferred: run that query before calling them), and includes the server's own instructions — read those. Afterwards just edit and rebuild the server; its next call runs the new build. Attaching an existing name again replaces it (use that to change args or env). Remembered for this project unless remember=false.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["name"] = new JsonObject { ["type"] = "string", ["description"] = "Short name, e.g. the project name; prefixes its tools. Letters, digits and '-'." },
                    ["command"] = new JsonObject { ["type"] = "string", ["description"] = "Absolute path to the server's built executable (e.g. ...\\bin\\Debug\\net9.0\\MyServer.exe), or an interpreter such as node, python, uv or dotnet with the script or project in args. A relative path is resolved against cwd." },
                    ["args"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Arguments, e.g. [\"C:\\\\proj\\\\dist\\\\index.js\"] for node." },
                    ["cwd"] = new JsonObject { ["type"] = "string", ["description"] = "Working directory for the server (default: the client's directory)." },
                    ["env"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = new JsonObject { ["type"] = "string" }, ["description"] = "Environment variables for the server. Stored with the remembered attachment." },
                    ["reload_on"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Only for a server run by an interpreter: the build output or source directories whose change means a new version, e.g. [\"dist\"]. Without it such a server is never reloaded. Not needed for an executable: its whole directory is watched." },
                    ["shadow_copy"] = new JsonObject { ["type"] = "boolean", ["default"] = true, ["description"] = "Run an executable from a copy of its directory, so builds can overwrite the original while it runs. Set false only for a server that reads files relative to its own location outside its directory; then, on Windows, call <name>_sitter_kill before each build." },
                    ["remember"] = new JsonObject { ["type"] = "boolean", ["default"] = true, ["description"] = "Attach it again automatically when a new session starts in this project." },
                },
                ["required"] = new JsonArray("name", "command"),
                ["additionalProperties"] = false,
            },
        },
        (JsonNode?)new JsonObject
        {
            ["name"] = Detach,
            ["description"] = "Stop an attached server and remove its tools. It is also forgotten for this project (not attached again next session) unless forget=false.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["name"] = new JsonObject { ["type"] = "string" },
                    ["forget"] = new JsonObject { ["type"] = "boolean", ["default"] = true, ["description"] = "false keeps it remembered, so the next session attaches it again." },
                },
                ["required"] = new JsonArray("name"),
                ["additionalProperties"] = false,
            },
        },
        (JsonNode?)new JsonObject
        {
            ["name"] = List,
            ["description"] = "Show what is attached: each server's command and pid, every exposed tool name with the server tool it maps to, what is remembered for this project, and how each remembered server's automatic attach went at session start (with the error if it failed).",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject(),
                ["additionalProperties"] = false,
            },
        });

    // The hub's manual: what a model reads at connect time, so it must cover
    // every quirk without the README.
    const string Instructions =
        "mcp-sitter hub: run MCP servers you are developing inside this conversation, with no client restart.\n" +
        "1. Attach: sitter_attach(name, command, args?, cwd?, env?). command is the server's built executable " +
        "(absolute path), or an interpreter (node, python, uv, dotnet) with the script in args.\n" +
        "2. Load: its tools become <name>_<tool> (a tool already named <name>_... keeps its name). Claude Code lists " +
        "them as deferred tools: run the ToolSearch \"select:...\" query from the sitter_attach result before calling " +
        "them. Read the server instructions in that result too.\n" +
        "3. Develop: edit and rebuild the server as usual; nothing needs stopping. The next call to any of its tools " +
        "restarts it on the new build, and that response starts with \"[mcp-sitter] server restarted (binary changed, " +
        "...)\", listing tools added or removed (load added ones with ToolSearch). Its in-memory state is lost on every " +
        "restart. A response that says the new build is still being written ran on the previous build: call again " +
        "once the build has finished.\n" +
        "4. Debug: a crash fails the pending call at once and the next call restarts the server. " +
        "<name>_sitter_child_stderr shows its stderr (stack traces, startup errors); <name>_sitter_status shows its " +
        "state and which build runs.\n" +
        "Good to know: an executable runs from a shadow copy, so the build may overwrite its output in place (set " +
        "shadow_copy=false only for a server that reads files outside its own directory relative to its location, and " +
        "then call <name>_sitter_kill before each build on Windows). A server run by an interpreter is reloaded only " +
        "with reload_on, e.g. [\"dist\"]. Only tools pass through, not the server's prompts or resources. Attachments " +
        "are remembered for this project and attached again when the next session starts; sitter_detach forgets one, " +
        "sitter_list shows everything. Rebuilding mcp-sitter itself does not update this hub: the user has to run " +
        "/mcp, pick {hub}, and Reconnect (remembered servers come back by themselves).";

    static JsonObject TextResult(string text) => new()
    {
        ["content"] = new JsonArray((JsonNode?)new JsonObject { ["type"] = "text", ["text"] = text }),
    };

    static JsonObject ErrorResult(string text)
    {
        var r = TextResult(text);
        r["isError"] = true;
        return r;
    }

    static string Version() =>
        typeof(Hub).Assembly.GetName().Version?.ToString(3) ?? "0";
}

public sealed record HubConfig(string HubName);

// One attached server: an mcp-sitter child process the hub talks MCP to.
public sealed class HubChild
{
    readonly Hub _hub;
    readonly List<string> _sitterArgs;
    readonly Dictionary<string, string> _env;
    readonly SemaphoreSlim _stdinLock = new(1, 1);
    readonly object _lock = new();
    readonly Dictionary<string, TaskCompletionSource<JsonObject>> _pending = new();
    readonly HashSet<string> _clientIds = new();
    readonly Queue<string> _stderr = new();
    JsonArray _tools = new();
    Process? _process;
    long _nextId;
    volatile bool _stopping;

    public string Name { get; }
    public string Command => string.Join(" ", _sitterArgs.SkipWhile(a => a != "--").Skip(1));
    public int? Pid { get { try { return _process?.Id; } catch { return null; } } }

    public HubChild(Hub hub, string name, List<string> sitterArgs, Dictionary<string, string> env)
    {
        _hub = hub;
        Name = name;
        _sitterArgs = sitterArgs;
        _env = env;
    }

    // Starts the mcp-sitter child, initializes it with the client's params,
    // and reads its tools. Returns the server's instructions, if any.
    public async Task<string?> StartAsync(JsonNode clientInitializeParams)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? "mcp-sitter",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var a in _sitterArgs) psi.ArgumentList.Add(a);
        foreach (var (k, v) in _env) psi.Environment[k] = v;

        _process = Process.Start(psi) ?? throw new InvalidOperationException("could not start mcp-sitter");
        var process = _process;
        _ = Task.Run(() => ReaderLoop(process));
        _ = Task.Run(() => StderrLoop(process));
        _ = Task.Run(async () =>
        {
            try { await process.WaitForExitAsync(); } catch { }
            FailPending("mcp-sitter child exited");
            if (!_stopping) await _hub.ChildExitedAsync(this);
        });

        var init = await RequestAsync("initialize", clientInitializeParams.DeepClone(), TimeSpan.FromSeconds(30));
        if (init["error"] is JsonNode err) throw new InvalidOperationException($"initialize failed: {err["message"]}");
        await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });
        await RefreshToolsAsync();
        return init["result"]?["instructions"]?.GetValue<string>();
    }

    public void Stop()
    {
        _stopping = true;
        var p = _process;
        if (p == null) return;
        // Closing stdin shuts mcp-sitter down, and it stops its own child.
        try { p.StandardInput.Close(); } catch { }
        try { if (!p.WaitForExit(2000)) p.Kill(entireProcessTree: true); } catch { }
        FailPending("detached");
    }

    public async Task RefreshToolsAsync()
    {
        var all = new JsonArray();
        string? cursor = null;
        do
        {
            var p = new JsonObject();
            if (cursor != null) p["cursor"] = cursor;
            var resp = await RequestAsync("tools/list", p, TimeSpan.FromSeconds(30));
            if (resp["result"]?["tools"] is JsonArray tools)
                foreach (var t in tools) all.Add(t?.DeepClone());
            cursor = resp["result"]?["nextCursor"]?.GetValue<string>();
        } while (cursor != null);
        lock (_lock) _tools = all;
    }

    public List<string> ToolNames()
    {
        lock (_lock)
            return _tools.Select(t => t?["name"]?.GetValue<string>()).Where(n => n != null).Select(n => n!).ToList();
    }

    public JsonObject? ToolDefinition(string tool)
    {
        lock (_lock)
            return _tools.FirstOrDefault(t => t?["name"]?.GetValue<string>() == tool)?.DeepClone() as JsonObject;
    }

    public string StderrTail()
    {
        lock (_stderr) return string.Join("\n", _stderr.TakeLast(40));
    }

    public async Task<bool> ForwardClientRequestAsync(JsonObject request)
    {
        var key = request["id"]?.ToJsonString();
        if (key != null) lock (_lock) _clientIds.Add(key);
        if (await SendAsync(request)) return true;
        if (key != null) lock (_lock) _clientIds.Remove(key);
        return false;
    }

    public async Task<bool> SendAsync(JsonObject obj)
    {
        var p = _process;
        if (p == null) return false;
        await _stdinLock.WaitAsync();
        try
        {
            if (p.HasExited) return false;
            var bytes = Encoding.UTF8.GetBytes(obj.ToJsonString() + "\n");
            await p.StandardInput.BaseStream.WriteAsync(bytes);
            await p.StandardInput.BaseStream.FlushAsync();
            return true;
        }
        catch { return false; }
        finally { _stdinLock.Release(); }
    }

    async Task<JsonObject> RequestAsync(string method, JsonNode? @params, TimeSpan timeout)
    {
        var id = $"mcp-sitter-hub:{Interlocked.Increment(ref _nextId)}";
        var key = JsonValue.Create(id).ToJsonString();
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock) _pending[key] = tcs;
        var sent = await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = @params });
        if (!sent)
        {
            lock (_lock) _pending.Remove(key);
            throw new InvalidOperationException($"could not send {method}: mcp-sitter is not running");
        }
        try { return await tcs.Task.WaitAsync(timeout); }
        catch (TimeoutException)
        {
            lock (_lock) _pending.Remove(key);
            throw new InvalidOperationException($"{method} timed out after {timeout.TotalSeconds:0}s");
        }
    }

    void FailPending(string reason)
    {
        List<TaskCompletionSource<JsonObject>> pending;
        lock (_lock) { pending = _pending.Values.ToList(); _pending.Clear(); }
        foreach (var t in pending) t.TrySetException(new InvalidOperationException(reason));
    }

    async Task ReaderLoop(Process process)
    {
        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync()) != null)
            {
                if (line.Length == 0) continue;
                try { await HandleLineAsync(line); }
                catch (Exception ex) { Log.Warn($"{Name}: {ex.Message}"); }
            }
        }
        catch { }
        // mcp-sitter answers every request before it exits; anything left
        // was never going to be answered.
        List<string> orphaned;
        lock (_lock) { orphaned = _clientIds.ToList(); _clientIds.Clear(); }
        foreach (var key in orphaned)
        {
            var id = JsonNode.Parse(key);
            _hub.ClientRequestDone(id!);
            await _hub.RespondErrorAsync(id, -32002, $"server '{Name}' was detached or its mcp-sitter exited before responding");
        }
    }

    async Task StderrLoop(Process process)
    {
        try
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync()) != null)
            {
                lock (_stderr)
                {
                    _stderr.Enqueue(line);
                    while (_stderr.Count > 200) _stderr.Dequeue();
                }
                Log.Debug($"[{Name}] {line}");
            }
        }
        catch { }
    }

    async Task HandleLineAsync(string line)
    {
        if (JsonNode.Parse(line) is not JsonObject obj) return;
        var method = obj["method"]?.GetValue<string>();
        var id = obj["id"];

        if (method == null && id != null)
        {
            var key = id.ToJsonString();
            TaskCompletionSource<JsonObject>? tcs;
            bool client;
            lock (_lock)
            {
                _pending.Remove(key, out tcs);
                client = tcs == null && _clientIds.Remove(key);
            }
            if (tcs != null) { tcs.TrySetResult(obj); return; }
            if (client)
            {
                _hub.ClientRequestDone(id);
                await _hub.SendToClientAsync(obj);
            }
            return;
        }

        if (method == "notifications/tools/list_changed")
        {
            _ = Task.Run(() => _hub.ChildToolsChangedAsync(this));
            return;
        }

        if (method == "ping" && id != null)
        {
            await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = new JsonObject() });
            return;
        }

        if (id != null)
        {
            await _hub.ForwardChildRequestAsync(this, obj);
            return;
        }

        // other notifications (progress, logging) pass through
        await _hub.SendToClientAsync(obj);
    }
}
