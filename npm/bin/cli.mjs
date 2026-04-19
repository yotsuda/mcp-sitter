#!/usr/bin/env node

import { spawn } from "child_process";
import { createRequire } from "module";

const require = createRequire(import.meta.url);

const platform = process.platform;
const arch = process.arch;
const subPkg = `mcp-sitter-${platform}-${arch}`;
const binaryName = platform === "win32" ? "mcp-sitter.exe" : "mcp-sitter";

let binary;
try {
  binary = require.resolve(`${subPkg}/bin/${binaryName}`);
} catch {
  console.error(
    `mcp-sitter: no native binary available for ${platform}-${arch}.\n` +
      `Supported platforms: win32-x64, linux-x64.\n` +
      `If npm install reported optional dependency failures, that is the cause.\n` +
      `See https://github.com/yotsuda/mcp-sitter for source builds.`
  );
  process.exit(1);
}

const child = spawn(binary, process.argv.slice(2), {
  stdio: "inherit",
  windowsHide: true,
});

child.on("exit", (code) => process.exit(code ?? 1));
child.on("error", (err) => {
  console.error(err.message);
  process.exit(1);
});
