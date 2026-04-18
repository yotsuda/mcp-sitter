using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FakeMcp;

public static class Program
{
    // Set FAKEMCP_TOOLSET=a (default) or b to change the tool list.
    // Set FAKEMCP_NAME to override serverInfo.name (default "FakeMcp").

    public static void Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.Error.WriteLine($"FakeMcp started pid={Environment.ProcessId} toolset={Toolset} name={ServerName}");

        string? line;
        while ((line = Console.In.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            JsonNode? node;
            try { node = JsonNode.Parse(line); }
            catch { continue; }
            if (node is not JsonObject obj) continue;

            var method = obj["method"]?.GetValue<string>();
            var id = obj["id"];

            JsonObject? response = method switch
            {
                "initialize" => new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id?.DeepClone(),
                    ["result"] = new JsonObject
                    {
                        ["protocolVersion"] = "2024-11-05",
                        ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["version"] = "1.0.0" },
                        ["capabilities"] = new JsonObject
                        {
                            ["tools"] = new JsonObject { ["listChanged"] = true },
                        },
                    },
                },
                "tools/list" => new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id?.DeepClone(),
                    ["result"] = new JsonObject { ["tools"] = BuildTools() },
                },
                "tools/call" => BuildCallResponse(obj),
                "ping" => new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id?.DeepClone(),
                    ["result"] = new JsonObject(),
                },
                _ => null,
            };

            if (method == "notifications/initialized") continue;
            if (response == null && id != null)
            {
                response = new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id.DeepClone(),
                    ["error"] = new JsonObject
                    {
                        ["code"] = -32601,
                        ["message"] = $"method not found: {method}",
                    },
                };
            }

            if (response != null)
            {
                Console.Out.WriteLine(response.ToJsonString());
                Console.Out.Flush();
            }
        }
        Console.Error.WriteLine("FakeMcp stdin closed, exiting");
    }

    static string Toolset
    {
        get
        {
            var file = Path.Combine(AppContext.BaseDirectory, "toolset.txt");
            if (File.Exists(file))
            {
                try { return File.ReadAllText(file).Trim(); } catch { }
            }
            return Environment.GetEnvironmentVariable("FAKEMCP_TOOLSET") ?? "a";
        }
    }
    static string ServerName => Environment.GetEnvironmentVariable("FAKEMCP_NAME") ?? "FakeMcp";

    static JsonArray BuildTools()
    {
        if (Toolset == "b")
        {
            return new JsonArray
            {
                Tool("fake_echo", "Echo text", "text"),
                Tool("fake_upper", "Uppercase text", "text"),
                Tool("fake_reverse", "Reverse text", "text"),
            };
        }
        return new JsonArray
        {
            Tool("fake_echo", "Echo text", "text"),
            Tool("fake_upper", "Uppercase text", "text"),
            Tool("fake_log", "Write text to stderr and return confirmation", "text"),
        };
    }

    static JsonObject Tool(string name, string desc, string argName) => new()
    {
        ["name"] = name,
        ["description"] = desc,
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                [argName] = new JsonObject { ["type"] = "string" },
            },
            ["required"] = new JsonArray { argName },
        },
    };

    static JsonObject BuildCallResponse(JsonObject request)
    {
        var id = request["id"];
        var name = request["params"]?["name"]?.GetValue<string>() ?? "";
        var argsNode = request["params"]?["arguments"] as JsonObject;
        var text = argsNode?["text"]?.GetValue<string>() ?? "";
        string result;
        if (name == "fake_log")
        {
            Console.Error.WriteLine(text);
            result = $"logged: {text}";
        }
        else
        {
            result = name switch
            {
                "fake_echo" => $"echo: {text}",
                "fake_upper" => text.ToUpperInvariant(),
                "fake_reverse" => new string(text.Reverse().ToArray()),
                _ => $"unknown tool: {name}",
            };
        }
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = result },
                },
            },
        };
    }
}
