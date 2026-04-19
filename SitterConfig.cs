namespace McpSitter;

public sealed record SitterConfig(
    string ChildCommand,
    string[] ChildArgs,
    string? WorkingDirectory)
{
    public static SitterConfig? Parse(string[] args)
    {
        string? cwd = null;

        int i = 0;
        while (i < args.Length)
        {
            var a = args[i];
            if (a == "--cwd" && i + 1 < args.Length) { cwd = args[i + 1]; i += 2; }
            else if (a == "--") { i++; break; }
            else break;
        }

        if (i >= args.Length) return null;
        var cmd = args[i++];
        var rest = args[i..];

        return new SitterConfig(cmd, rest, cwd);
    }
}
