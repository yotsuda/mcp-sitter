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
  --cwd <path>         Working directory for the child
  --help, -h           Show this help

mcp-sitter speaks MCP over stdio to its parent (e.g. Claude Code) and
forwards to a child stdio MCP server. When the child is killed (by
sitter_kill or by crashing), it is lazily respawned on the next tool
call. If the tool set changed, the parent is notified via
notifications/tools/list_changed.

Built-in tools exposed to the parent:
  sitter_status         Show child status, binary path/version, spawn
                        counts, last startup time, and previous exit info
  sitter_kill           Kill the child and all processes running the same
                        exe so the binary can be rebuilt
  sitter_binary_info    Show version, build date, and metadata of the
                        child binary — useful for detecting stale builds
  sitter_child_stderr   Tail the child's stderr from a bounded ring
                        buffer (lines persist across respawns)

After a restart, the first tool/call response is annotated with a
[mcp-sitter] notice describing the new spawn (startup time, binary
path + version/age, previous exit, and tools-list diff).");
    }
}
