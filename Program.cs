using System.Text;

namespace McpSitter;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);

        if (args.Length == 0 || args[0] is "--help" or "-h" or "/?")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        var config = SitterConfig.Parse(args);
        if (config is null)
        {
            Console.Error.WriteLine("mcp-sitter: invalid arguments");
            PrintUsage();
            return 1;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var sitter = new Sitter(config);
        try
        {
            await sitter.RunAsync(cts.Token);
            return 0;
        }
        catch (OperationCanceledException) { return 0; }
        catch (Exception ex)
        {
            Log.Error($"fatal: {ex}");
            return 2;
        }
    }

    static void PrintUsage()
    {
        Console.Error.WriteLine(
@"mcp-sitter - hot-reload bridge for stdio MCP servers.

Usage:
  mcp-sitter [options] [--] <child-exe> [child-args...]

Options:
  --cwd <path>             Working directory for the child
  --reload-on <path>       File or directory whose change means a new build
                           (repeatable). Default: the child exe, and
                           <name>.dll beside it. Use it for a script server
                           (node, python), e.g. --reload-on dist
  --no-reload-on-change    Restart only on sitter_kill or a crash
  --help, -h               Show this help

mcp-sitter speaks MCP over stdio to its parent (e.g. Claude Code) and
forwards to a child stdio MCP server. When a new build replaces the
child's binary, the next tool call restarts the child on it. When the
child is stopped (by sitter_kill or by crashing), it is lazily respawned
on the next tool call. If the tool set changed, the parent is notified
via notifications/tools/list_changed.

Built-in tools exposed to the parent:
  sitter_status         Show child status, binary path/version, spawn
                        counts, last startup time, and previous exit info
  sitter_kill           Stop the child so its binary can be rebuilt
                        (others=true also stops other processes running
                        the same exe)
  sitter_binary_info    Show version, build date, and metadata of the
                        child binary — useful for detecting stale builds
  sitter_child_stderr   Tail the child's stderr from a bounded ring
                        buffer (lines persist across respawns)

After a restart, the first tool/call response is annotated with a
[mcp-sitter] notice describing the new spawn (startup time, binary
path + version/age, previous exit, and tools-list diff).");
    }
}
