namespace McpSitter;

public sealed record SitterConfig(
    string ChildCommand,
    string[] ChildArgs,
    string? WatchPath,
    int DebounceMs,
    string? WorkingDirectory)
{
    public static SitterConfig? Parse(string[] args)
    {
        string? watchPath = null;
        int debounceMs = 1500;
        string? cwd = null;

        int i = 0;
        while (i < args.Length)
        {
            var a = args[i];
            if (a == "--watch" && i + 1 < args.Length) { watchPath = args[i + 1]; i += 2; }
            else if (a == "--debounce" && i + 1 < args.Length)
            {
                if (!int.TryParse(args[i + 1], out debounceMs)) return null;
                i += 2;
            }
            else if (a == "--cwd" && i + 1 < args.Length) { cwd = args[i + 1]; i += 2; }
            else if (a == "--") { i++; break; }
            else break;
        }

        if (i >= args.Length) return null;
        var cmd = args[i++];
        var rest = args[i..];

        watchPath ??= ResolveWatch(cmd, cwd);
        return new SitterConfig(cmd, rest, watchPath, debounceMs, cwd);
    }

    static string? ResolveWatch(string cmd, string? cwd)
    {
        try
        {
            var candidate = Path.IsPathRooted(cmd)
                ? cmd
                : Path.Combine(cwd ?? Directory.GetCurrentDirectory(), cmd);
            return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
        }
        catch { return null; }
    }
}
