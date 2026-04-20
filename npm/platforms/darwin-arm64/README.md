# @ytsuda/mcp-sitter-darwin-arm64

macOS Apple Silicon (arm64) native binary for [`mcp-sitter`](https://www.npmjs.com/package/mcp-sitter) — a hot-reload bridge for stdio MCP (Model Context Protocol) servers. Exposes `sitter_status`, `sitter_kill`, `sitter_binary_info`, and `sitter_child_stderr` tools alongside any MCP server it supervises, so the AI can request a child-process rebuild without the host application losing its MCP connection.

This package exists so the umbrella [`mcp-sitter`](https://www.npmjs.com/package/mcp-sitter) can resolve its macOS arm64 binary via npm's `optionalDependencies` + `os`/`cpu` filter mechanism. **You should not install this package directly.** Install `mcp-sitter` instead — npm will pick the matching platform subpackage automatically.

The binary is produced by `dotnet publish -c Release -r osx-arm64` (NativeAOT) from the `main` branch at each tagged release and published with SLSA provenance from GitHub Actions.

Source: [github.com/yotsuda/mcp-sitter](https://github.com/yotsuda/mcp-sitter)
