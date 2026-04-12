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
  --watch <path>       Binary to watch (default: <child-exe> if it is a file)
  --debounce <ms>      Debounce after a write before respawning (default 1500)
  --cwd <path>         Working directory for the child
  --help, -h           Show this help

mcp-sitter speaks MCP over stdio to its parent (e.g. Claude Code) and
forwards to a child stdio MCP server. When the child is killed (by
sitter_kill, by a build, or by the file watcher), it is lazily
respawned on the next tool call. If the tool set changed, the parent
is notified via notifications/tools/list_changed.

Built-in tools exposed to the parent:
  sitter_status  Show child status, watched binary, kill/spawn counts
  sitter_kill    Kill the child and all processes running the same exe
                 so the binary can be rebuilt");
    }
}
