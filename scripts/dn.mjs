#!/usr/bin/env node
// Drive the running DesktopNames over its pipe — no keystrokes, no mouse.
//   node scripts/dn.mjs <method> [params-json]
//   node scripts/dn.mjs commands
//   node scripts/dn.mjs execute '{"command":"desktop.toggleBlue","params":{"desktop":3}}'
//   node scripts/dn.mjs ui.open '{"window":"projects"}'
//   node scripts/dn.mjs ui.screenshot '{"window":"projects","path":"C:/tmp/dn.png"}'
// Prints the reply JSON. Exit 0 ok, 1 the request failed, 2 usage error.
import net from "node:net";

const [method, paramsText] = process.argv.slice(2);
if (!method) {
  console.error("usage: dn.mjs <method> [params-json]   (method \"commands\" lists commands; see CLAUDE.md for methods)");
  process.exit(2);
}
let params;
if (paramsText !== undefined) {
  try { params = JSON.parse(paramsText); }
  catch (e) { console.error(`params are not JSON: ${e.message}`); process.exit(2); }
}

const socket = net.connect("\\\\.\\pipe\\DesktopNames");
let buffer = "";
socket.on("connect", () => socket.write(JSON.stringify({ type: "control", method, params }) + "\n"));
socket.on("data", chunk => {
  buffer += chunk.toString("utf8");
  const nl = buffer.indexOf("\n");
  if (nl < 0) return;
  const reply = JSON.parse(buffer.slice(0, nl));
  console.log(JSON.stringify(reply, null, 2));
  socket.end();
  process.exit(reply.ok ? 0 : 1);
});
socket.on("error", e => { console.error(`DesktopNames pipe: ${e.message} (is DesktopNames running?)`); process.exit(1); });
