using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace McpSitter;

// `mcp-sitter add`: registers an in-development server with Claude Code,
// behind this mcp-sitter, under a name that ends in -dev. It runs
// `claude mcp add`, so the client's own config handling stays in charge.
//
// A server added while Claude Code runs is not loaded into the running
// session (not even from /mcp): the client has to start again, and
// `claude --continue` brings the conversation back. From then on mcp-sitter
// reloads rebuilds by itself, so this is a once-per-server step.
public static class AddCommand
{
    const string Suffix = "-dev";

    sealed class Options
    {
        public string? Name;
        public string Scope = "user";
        public string? From;
        public readonly List<KeyValuePair<string, string>> Env = new();
        public string? Cwd;
        public readonly List<string> ReloadOn = new();
        public bool NoReload;
        public bool DryRun;
        public bool Hub;
        public string? Child;
        public string[] ChildArgs = Array.Empty<string>();
    }

    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        var o = Parse(args, out var error);
        if (o == null)
        {
            Console.Error.WriteLine($"mcp-sitter add: {error}");
            PrintUsage();
            return 1;
        }

        var name = DevName(o.Name ?? "sitter");

        // The child must be found from wherever the client starts mcp-sitter,
        // so a path is made absolute here; a bare command (node, uv) is left
        // for PATH, as the client would.
        var child = o.Child ?? "";
        if (!o.Hub && LooksLikePath(child))
        {
            var full = Path.GetFullPath(child);
            if (!File.Exists(full))
            {
                Console.Error.WriteLine($"mcp-sitter add: child executable not found: {full}");
                return 1;
            }
            child = full;
        }

        var env = new List<KeyValuePair<string, string>>();
        if (o.From != null)
        {
            var fromEnv = ReadServerEnv(o.From, out var fromError);
            if (fromEnv == null)
            {
                Console.Error.WriteLine($"mcp-sitter add: --from {o.From}: {fromError}");
                return 1;
            }
            env.AddRange(fromEnv);
        }
        foreach (var kv in o.Env)
        {
            env.RemoveAll(e => e.Key == kv.Key);
            env.Add(kv);
        }

        var sitterPath = Environment.ProcessPath ?? "mcp-sitter";
        var claudeArgs = new List<string> { "mcp", "add", "--scope", o.Scope, name };
        foreach (var kv in env)
        {
            claudeArgs.Add("-e");
            claudeArgs.Add($"{kv.Key}={kv.Value}");
        }
        claudeArgs.Add("--");
        claudeArgs.Add(sitterPath);
        if (o.Hub)
        {
            claudeArgs.Add("--hub");
            claudeArgs.Add("--hub-name");
            claudeArgs.Add(name);
        }
        else
        {
            if (o.Cwd != null) { claudeArgs.Add("--cwd"); claudeArgs.Add(Path.GetFullPath(o.Cwd)); }
            var reloadBase = o.Cwd != null ? Path.GetFullPath(o.Cwd) : Environment.CurrentDirectory;
            foreach (var p in o.ReloadOn) { claudeArgs.Add("--reload-on"); claudeArgs.Add(Path.GetFullPath(p, reloadBase)); }
            if (o.NoReload) claudeArgs.Add("--no-reload-on-change");
            claudeArgs.Add("--");
            claudeArgs.Add(child);
            claudeArgs.AddRange(o.ChildArgs);
        }

        if (o.DryRun)
        {
            Console.WriteLine("claude " + string.Join(" ", claudeArgs.Select(Quote)));
            return 0;
        }

        var claude = FindOnPath("claude");
        if (claude == null)
        {
            Console.Error.WriteLine("mcp-sitter add: `claude` (Claude Code) was not found on PATH. Run with --dry-run to see the command.");
            return 1;
        }

        var code = RunProcess(claude, claudeArgs);
        if (code != 0) return code;

        Console.WriteLine();
        Console.WriteLine($"Registered {name}. Claude Code loads new servers when it starts: start a new session, or");
        if (o.Hub)
        {
            Console.WriteLine("resume this one with `claude --continue`. From then on, sitter_attach starts a server under");
            Console.WriteLine($"development and its tools appear as mcp__{name}__<name>_<tool> at once, with no restart.");
        }
        else
        {
            Console.WriteLine("resume this one with `claude --continue`. Its tools are then mcp__" + name + "__*, and each");
            Console.WriteLine("rebuild is picked up at the next tool call.");
        }
        return 0;
    }

    static Options? Parse(string[] args, out string? error)
    {
        var o = new Options();
        error = null;
        int i = 0;
        while (i < args.Length)
        {
            var a = args[i];
            string? Next() => i + 1 < args.Length ? args[i + 1] : null;

            if (a == "--") { i++; break; }
            if (!a.StartsWith('-'))
            {
                if (o.Name == null) { o.Name = a; i++; continue; }
                break; // the child command
            }
            switch (a)
            {
                case "--scope" or "-s":
                    if (Next() is not ("user" or "local" or "project")) { error = "--scope takes user, local or project"; return null; }
                    o.Scope = Next()!; i += 2; break;
                case "--from":
                    if (Next() == null) { error = "--from takes a server name"; return null; }
                    o.From = Next(); i += 2; break;
                case "-e" or "--env":
                    var kv = Next();
                    var eq = kv?.IndexOf('=') ?? -1;
                    if (kv == null || eq <= 0) { error = "-e takes KEY=VALUE"; return null; }
                    o.Env.Add(new(kv[..eq], kv[(eq + 1)..])); i += 2; break;
                case "--cwd":
                    if (Next() == null) { error = "--cwd takes a directory"; return null; }
                    o.Cwd = Next(); i += 2; break;
                case "--reload-on":
                    if (Next() == null) { error = "--reload-on takes a path"; return null; }
                    o.ReloadOn.Add(Next()!); i += 2; break;
                case "--no-reload-on-change":
                    o.NoReload = true; i++; break;
                case "--dry-run":
                    o.DryRun = true; i++; break;
                case "--hub":
                    o.Hub = true; i++; break;
                default:
                    error = $"unknown option {a}"; return null;
            }
        }

        if (o.Hub)
        {
            // The hub runs no child of its own; servers are attached later.
            if (i < args.Length) { error = "--hub takes no child command (attach servers with sitter_attach)"; return null; }
            if (o.From != null || o.Env.Count > 0 || o.Cwd != null || o.ReloadOn.Count > 0 || o.NoReload)
            { error = "--hub takes only a name, --scope and --dry-run; give the rest to sitter_attach"; return null; }
            return o;
        }
        if (o.Name == null) { error = "missing server name"; return null; }
        if (i >= args.Length) { error = "missing child command"; return null; }
        o.Child = args[i];
        o.ChildArgs = args[(i + 1)..];
        return o;
    }

    // Every dev server ends in -dev, so it never collides with the production
    // registration and its tools (mcp__<name>-dev__*) read as the dev build's.
    static string DevName(string name) =>
        name.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase) ? name : name + Suffix;

    static bool LooksLikePath(string command) =>
        command.Contains('/') || command.Contains('\\') || Path.IsPathRooted(command) || File.Exists(command);

    // The env of an existing Claude Code registration: user scope, then this
    // project's local scope, then .mcp.json here. Read-only.
    static List<KeyValuePair<string, string>>? ReadServerEnv(string server, out string? error)
    {
        error = null;
        var candidates = new List<JsonNode?>();
        var claudeJson = Environment.GetEnvironmentVariable("MCP_SITTER_CLAUDE_JSON")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
        try
        {
            if (File.Exists(claudeJson))
            {
                var root = JsonNode.Parse(File.ReadAllText(claudeJson));
                candidates.Add(root?["mcpServers"]?[server]);
                if (root?["projects"] is JsonObject projects)
                {
                    var here = Environment.CurrentDirectory;
                    foreach (var (key, value) in projects)
                    {
                        if (string.Equals(Path.GetFullPath(key), here, StringComparison.OrdinalIgnoreCase))
                            candidates.Add(value?["mcpServers"]?[server]);
                    }
                }
            }
            var mcpJson = Path.Combine(Environment.CurrentDirectory, ".mcp.json");
            if (File.Exists(mcpJson))
                candidates.Add(JsonNode.Parse(File.ReadAllText(mcpJson))?["mcpServers"]?[server]);
        }
        catch (Exception ex)
        {
            error = $"could not read the Claude Code config: {ex.Message}";
            return null;
        }

        var found = candidates.FirstOrDefault(c => c is JsonObject);
        if (found == null)
        {
            error = $"no server named '{server}' in {claudeJson} or .mcp.json";
            return null;
        }

        var env = new List<KeyValuePair<string, string>>();
        if (found["env"] is JsonObject envObj)
            foreach (var (key, value) in envObj)
                if (value != null) env.Add(new(key, value.ToString()));
        return env;
    }

    static string? FindOnPath(string command)
    {
        var exts = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : new[] { "" };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in exts)
            {
                try
                {
                    var candidate = Path.Combine(dir, command + ext.ToLowerInvariant());
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
        }
        return null;
    }

    static int RunProcess(string exe, List<string> args)
    {
        var psi = new ProcessStartInfo { UseShellExecute = false };
        // A .cmd shim (npm install) runs through cmd.exe.
        if (OperatingSystem.IsWindows() &&
            (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            psi.FileName = "cmd.exe";
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(exe);
        }
        else psi.FileName = exe;
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode;
    }

    static string Quote(string s) =>
        s.Length > 0 && s.All(c => !char.IsWhiteSpace(c) && c is not ('"' or '\'' or '&' or '|' or ';' or '<' or '>'))
            ? s
            : "\"" + s.Replace("\"", "\\\"") + "\"";

    static void PrintUsage()
    {
        Console.Error.WriteLine(
@"Usage:
  mcp-sitter add <name> [options] [--] <child-exe> [child-args...]
  mcp-sitter add --hub [<name>] [--scope <scope>] [--dry-run]

Registers <name>-dev with Claude Code (`claude mcp add`), running
<child-exe> behind this mcp-sitter. -dev is appended when missing.

With --hub, registers the hub (default name sitter-dev) instead: servers
are then attached with sitter_attach while Claude Code runs, and their
tools appear at once, with no restart.

Options:
  --scope, -s <scope>      user (default), local or project
  --from <server>          Copy the env of an existing registration, e.g.
                           the production server's
  -e, --env KEY=VALUE      Set an environment variable (repeatable;
                           overrides --from)
  --cwd <path>             Passed to mcp-sitter: the child's directory
  --reload-on <path>       Passed to mcp-sitter (repeatable)
  --no-reload-on-change    Passed to mcp-sitter
  --dry-run                Print the claude command instead of running it

Claude Code loads a new server when it starts: start a new session, or
resume the current one with `claude --continue`.");
    }
}
