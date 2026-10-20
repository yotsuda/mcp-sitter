using System.Text.Json.Nodes;

namespace McpSitter;

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
            ["description"] = "Show mcp-sitter status: child MCP server state, child binary path, kill/spawn counts, last startup duration, and previous exit info.",
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
            ["description"] = "Stop the child MCP server so its binary is unlocked for a rebuild; the next tool call starts it again. Not needed when the build can replace the running binary (it renames the old file aside first, or the OS allows it): mcp-sitter then restarts the child on the new build by itself at the next tool call. Other processes running the same executable are listed, not stopped, unless others=true.",
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
            ["description"] = "Show version, build date (mtime), size, and other metadata of the child binary. Use this to confirm which build is currently loaded and to detect stale binaries.",
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
            ["description"] = "Tail the child MCP server's stderr (bounded ring buffer of ~1000 lines, persisted across child respawns). Use after `sitter_status` reports a non-zero `previousExitCode` or `child failed to start`, to retrieve the crash stack trace / error output from the child.",
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
                        ["description"] = "If true, only lines from the current child generation — most useful immediately after a crash/respawn.",
                    },
                },
                ["additionalProperties"] = false,
            },
        }
    );
}
