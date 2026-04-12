using System.Text.Json.Nodes;

namespace McpSitter;

public static class SitterTools
{
    public const string Status = "sitter_status";
    public const string Kill = "sitter_kill";

    public static JsonArray Definitions() => new()
    {
        new JsonObject
        {
            ["name"] = Status,
            ["description"] = "Show mcp-sitter status: child MCP server state, watched binary path, kill/spawn counts.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject(),
                ["additionalProperties"] = false,
            },
        },
        new JsonObject
        {
            ["name"] = Kill,
            ["description"] = "Kill the child MCP server AND all other processes running the same executable, so the binary file is unlocked and can be rebuilt. The child will be lazily respawned on the next tool call.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject(),
                ["additionalProperties"] = false,
            },
        },
    };
}
