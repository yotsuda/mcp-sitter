namespace McpSitter;

public static class Log
{
    static readonly object _gate = new();

    public static void Info(string msg) => Write("INFO ", msg);
    public static void Warn(string msg) => Write("WARN ", msg);
    public static void Error(string msg) => Write("ERROR", msg);
    public static void Debug(string msg) => Write("DEBUG", msg);

    static void Write(string level, string msg)
    {
        var ts = DateTime.Now.ToString("HH:mm:ss.fff");
        lock (_gate)
            Console.Error.WriteLine($"[{ts}] {level} mcp-sitter | {msg}");
    }
}
