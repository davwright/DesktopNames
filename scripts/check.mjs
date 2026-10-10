#!/usr/bin/env node
// The quality gate: run before every commit. Fails (exit 1) on any broken rule.
//   node scripts/check.mjs
// 1. Release build with warnings as errors.
// 2. Empty catch blocks may only go down: a swallowed error hides the failure (the settings.json
//    reset bug). Lower EMPTY_CATCH_BASELINE when you remove some; never raise it.
import { execSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";

const EMPTY_CATCH_BASELINE = 40;
const root = path.resolve(path.dirname(new URL(import.meta.url).pathname.replace(/^\/(\w:)/, "$1")), "..");
let failed = false;

try {
  execSync("dotnet build DesktopNames/DesktopNames.csproj -c Release --nologo -v q -warnaserror", { cwd: root, stdio: "pipe" });
  console.log("ok   build (warnings as errors)");
} catch (e) {
  failed = true;
  const out = (e.stdout?.toString() ?? "") + (e.stderr?.toString() ?? "");
  console.log("FAIL build\n" + out.split("\n").filter(l => /error|warning/.test(l)).slice(0, 20).join("\n"));
}

const emptyCatch = /catch\s*(\([^)]*\))?\s*\{\s*(\/\*[^*]*\*\/|\/\/[^\n]*)?\s*\}/g;
const hits = [];
for (const f of fs.readdirSync(path.join(root, "DesktopNames")).filter(f => f.endsWith(".cs"))) {
  const text = fs.readFileSync(path.join(root, "DesktopNames", f), "utf8");
  for (const m of text.matchAll(emptyCatch)) hits.push(`${f}:${text.slice(0, m.index).split("\n").length}`);
}
if (hits.length > EMPTY_CATCH_BASELINE) {
  failed = true;
  console.log(`FAIL empty catch blocks: ${hits.length} > baseline ${EMPTY_CATCH_BASELINE}. Log or rethrow instead of swallowing.\n  ` + hits.join("\n  "));
} else if (hits.length < EMPTY_CATCH_BASELINE) {
  console.log(`ok   empty catch blocks: ${hits.length} (below baseline ${EMPTY_CATCH_BASELINE} — lower EMPTY_CATCH_BASELINE in scripts/check.mjs)`);
} else {
  console.log(`ok   empty catch blocks: ${hits.length} (baseline)`);
}

process.exit(failed ? 1 : 0);
