#!/usr/bin/env node
//
// mcp-sitter meta-package launcher.
//
// On install, npm inspects each optionalDependency's "os" and "cpu"
// fields and only installs the subpackage that matches the current
// platform. This script figures out which one that is and spawns
// its binary with stdio inherited.
//
// MCP clients talk to mcp-sitter over stdio pipes. The Node process
// here is a transparent relay: process.argv is forwarded verbatim,
// stdio is inherited so pipes pass through, and SIGTERM / SIGINT /
// SIGHUP are forwarded to the child so client-initiated shutdown
// propagates.

import { spawn } from "node:child_process";
import { createRequire } from "node:module";

const require = createRequire(import.meta.url);

// process.platform is one of 'win32' / 'linux' / 'darwin' / …
// process.arch is one of 'x64' / 'arm64' / …
// Keep this map in sync with the meta package.json's
// optionalDependencies list and with the workflow matrix.
const PLATFORM_PACKAGES = {
  "win32-x64":    { pkg: "@ytsuda/mcp-sitter-win32-x64",    exe: "mcp-sitter.exe" },
  "linux-x64":    { pkg: "@ytsuda/mcp-sitter-linux-x64",    exe: "mcp-sitter" },
  "darwin-arm64": { pkg: "@ytsuda/mcp-sitter-darwin-arm64", exe: "mcp-sitter" },
};

const key = `${process.platform}-${process.arch}`;
const entry = PLATFORM_PACKAGES[key];

if (!entry) {
  const supported = Object.keys(PLATFORM_PACKAGES).join(", ");
  console.error(
    `mcp-sitter: unsupported platform ${key}. Supported: ${supported}.\n` +
      `If you need another platform, please open an issue at https://github.com/yotsuda/mcp-sitter/issues`
  );
  process.exit(1);
}

let binPath;
try {
  binPath = require.resolve(`${entry.pkg}/bin/${entry.exe}`);
} catch (err) {
  console.error(
    `mcp-sitter: ${entry.pkg} is not installed (${err && err.code ? err.code : err}).\n` +
      `This usually means the optional dependency for your platform was skipped at install time.\n` +
      `Try reinstalling: npm i -g mcp-sitter`
  );
  process.exit(1);
}

const child = spawn(binPath, process.argv.slice(2), {
  stdio: "inherit",
  windowsHide: false,
});

// Forward signals so clients (including MCP hosts) that SIGTERM /
// SIGINT the wrapper reach the actual mcp-sitter process. Without
// this, Ctrl+C from an MCP host would kill only the Node wrapper
// and leave a zombie child.
for (const sig of ["SIGTERM", "SIGINT", "SIGHUP", "SIGQUIT"]) {
  process.on(sig, () => {
    try { child.kill(sig); } catch { /* child may have already exited */ }
  });
}

child.on("exit", (code, signal) => {
  if (signal) {
    // Re-raise the signal on the wrapper so the exit status mirrors
    // a direct invocation.
    process.kill(process.pid, signal);
  } else {
    process.exit(code ?? 0);
  }
});

child.on("error", (err) => {
  console.error(`mcp-sitter: failed to launch ${binPath}: ${err.message}`);
  process.exit(1);
});
