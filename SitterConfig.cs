namespace McpSitter;

public sealed record SitterConfig(
    string ChildCommand,
    string[] ChildArgs,
    string? WorkingDirectory,
    bool ReloadOnChange,
    string[] ReloadOn,
    bool ShadowCopy = false,
    bool HubChild = false)
{
    public static SitterConfig? Parse(string[] args)
    {
        string? cwd = null;
        bool reloadOnChange = true;
        bool shadowCopy = false;
        bool hubChild = false;
        var reloadOn = new List<string>();

        int i = 0;
        while (i < args.Length)
        {
            var a = args[i];
            if (a == "--cwd" && i + 1 < args.Length) { cwd = args[i + 1]; i += 2; }
            else if (a == "--reload-on" && i + 1 < args.Length) { reloadOn.Add(args[i + 1]); i += 2; }
            else if (a == "--no-reload-on-change") { reloadOnChange = false; i++; }
            else if (a == "--shadow-copy") { shadowCopy = true; i++; }
            // Internal: started by the hub, which explains usage itself.
            else if (a == "--hub-child") { hubChild = true; i++; }
            else if (a == "--") { i++; break; }
            else break;
        }

        if (i >= args.Length) return null;
        var cmd = args[i++];
        var rest = args[i..];

        return new SitterConfig(cmd, rest, cwd, reloadOnChange, reloadOn.ToArray(), shadowCopy, hubChild);
    }
}
