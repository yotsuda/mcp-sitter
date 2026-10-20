using System.Text.Json.Nodes;

namespace McpSitter;

// The tools mcp-sitter adds next to the server's own. Their descriptions are
// the only manual an AI gets, so each says when to use the tool, not just
// what it returns.
public static class SitterTools
{
    public const string Status = "sitter_status";
    public const string Kill = "sitter_kill";
    public const string BinaryInfo = "sitter_binary_info";
    public const string ChildStderr = "sitter_child_stderr";

    public static JsonArray Definitions() => new JsonArray(
        (JsonNode?)new JsonObject
        {
            ["name"] = Status,
            ["description"] = "Show the state of the MCP server under development that mcp-sitter runs: running or down, pid, how many times it restarted and why, the previous exit code (non-zero = it crashed), last startup time, the last tools diff, whether a newer build is on disk than the one running (binaryChangedSinceSpawn), and whether it runs from a shadow copy. Use it when a call behaves unexpectedly or to confirm which build is running.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject(),
                ["additionalProperties"] = false,
            },
        },
        (JsonNode?)new JsonObject
        {
            ["name"] = Kill,
            ["description"] = "Stop the MCP server under development now; the next call to one of its tools starts it again, on the newest build. Rarely needed: a rebuild is picked up by itself at the next call. Use it (1) on Windows before a build that overwrites the server's executable in place while it runs WITHOUT a shadow copy (sitter_status shows shadowCopy: null) — otherwise the build fails on the locked file; or (2) to reset the server's in-memory state. Other processes running the same executable may belong to other sessions: they are listed, and stopped only with others=true.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["others"] = new JsonObject
                    {
                        ["type"] = "boolean",
                        ["default"] = false,
                        ["description"] = "Also stop every other process running the same executable. They may belong to other sessions, whose clients may never restart a server they lose; use only for processes that are yours to stop. Never applies to an interpreter such as node or python.",
                    },
                },
                ["additionalProperties"] = false,
            },
        },
        (JsonNode?)new JsonObject
        {
            ["name"] = BinaryInfo,
            ["description"] = "Show the server executable's version, build time (file mtime), size and metadata. Use it to check that the build on disk is the one you just made.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject(),
                ["additionalProperties"] = false,
            },
        },
        (JsonNode?)new JsonObject
        {
            ["name"] = ChildStderr,
            ["description"] = "Read the MCP server's stderr: crash stack traces, startup errors, its own log lines. Use it right after a call fails with \"exited ... before responding\" or the server fails to start. Kept across restarts (~1000 lines, a '----- child respawn -----' line between runs); since_spawn=true shows only the current run.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["lines"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["default"] = 200,
                        ["maximum"] = 1000,
                        ["minimum"] = 1,
                        ["description"] = "Tail N lines from the buffer (default 200, max 1000).",
                    },
                    ["since_spawn"] = new JsonObject
                    {
                        ["type"] = "boolean",
                        ["default"] = false,
                        ["description"] = "Only lines from the current run of the server (after its last restart).",
                    },
                },
                ["additionalProperties"] = false,
            },
        }
    );

    // Appended to the server's own instructions when mcp-sitter runs it as a
    // registered server, so the client's model learns how to work with it
    // without the README. (Under the hub, the hub's instructions cover this.)
    public static string UsageNote(bool shadowCopy) =>
        "[mcp-sitter] This server runs behind mcp-sitter for development. Edit and rebuild it at any time: the next " +
        "call to one of its tools restarts it on the new build, and that response starts with \"[mcp-sitter] server " +
        "restarted (binary changed, ...)\", listing tools added or removed (its in-memory state is lost on a restart). " +
        "If a response says the new build is still being written, call again once the build has finished. A crash " +
        "fails the pending call at once and the next call restarts the server; sitter_child_stderr shows its stderr " +
        "and sitter_status its state. " +
        (shadowCopy
            ? "It runs from a shadow copy, so a build can overwrite its output in place while it runs."
            : "On Windows its executable is locked while it runs: call sitter_kill before a build that overwrites it in place.");
}
