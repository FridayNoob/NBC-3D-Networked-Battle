// unity-mcp-call.js -- one-shot MCP call against anklebreaker-unity-mcp (stdio).
// Usage: node Tools/unity-mcp-call.js <toolName> [jsonArguments]
//        node Tools/unity-mcp-call.js --list [substringFilter]
// Prints the tool's text payload to stdout; non-zero exit on failure.
// ASCII only on purpose: this file must not need a BOM.

const { spawn } = require('child_process');

const listMode = process.argv[2] === '--list';
const schemaMode = process.argv[2] === '--schema';
const toolName = process.argv[2];
const rawArgs = (listMode || schemaMode) ? '{}' : (process.argv[3] || '{}');
const filter = listMode ? (process.argv[3] || '') : (schemaMode ? (process.argv[3] || '') : '');

if (!toolName) {
  console.error('usage: node unity-mcp-call.js <toolName> [jsonArguments] | --list [filter]');
  process.exit(2);
}

let args;
try {
  // argv[3] may be a JSON literal or a path to a JSON file (the PowerShell wrapper uses a file
  // so quoting can never be mangled by the shell).
  const fs = require('fs');
  const looksLikeFile = rawArgs && !rawArgs.trimStart().startsWith('{') && fs.existsSync(rawArgs);
  args = JSON.parse(looksLikeFile ? fs.readFileSync(rawArgs, 'utf8') : rawArgs);
} catch (e) {
  console.error('bad json arguments: ' + e.message);
  process.exit(2);
}

const proc = spawn('npx -y anklebreaker-unity-mcp@2.36.0', { shell: true });

let buffer = '';
const pending = new Map();
let nextId = 1;
let finished = false;

function send(method, params) {
  const id = nextId++;
  const msg = { jsonrpc: '2.0', id, method };
  if (params) msg.params = params;
  proc.stdin.write(JSON.stringify(msg) + '\n');
  return id;
}

function waitFor(id, timeoutMs) {
  return new Promise((resolve, reject) => {
    const started = Date.now();
    const timer = setInterval(() => {
      if (pending.has(id)) {
        clearInterval(timer);
        resolve(pending.get(id));
        pending.delete(id);
      } else if (Date.now() - started > timeoutMs) {
        clearInterval(timer);
        reject(new Error('timeout waiting for id=' + id));
      }
    }, 50);
  });
}

function finish(code, text) {
  if (finished) return;
  finished = true;
  if (text !== undefined) process.stdout.write(text);
  try { proc.kill(); } catch (_) { /* ignore */ }
  setTimeout(() => process.exit(code), 200);
}

proc.stdout.on('data', (chunk) => {
  buffer += chunk.toString('utf8');
  let idx;
  while ((idx = buffer.indexOf('\n')) >= 0) {
    const line = buffer.slice(0, idx).trim();
    buffer = buffer.slice(idx + 1);
    if (!line.startsWith('{')) continue;
    try {
      const msg = JSON.parse(line);
      if (msg.id !== undefined) pending.set(msg.id, msg);
    } catch (_) { /* ignore non-json */ }
  }
});

proc.stderr.on('data', () => { /* server banner goes here; keep stdout clean */ });
proc.on('error', (e) => finish(1, 'spawn failed: ' + e.message + '\n'));

(async () => {
  try {
    const i1 = send('initialize', {
      protocolVersion: '2024-11-05',
      capabilities: {},
      clientInfo: { name: 'nbc-unity-bridge', version: '1.0' },
    });
    await waitFor(i1, 30000);
    proc.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');

    if (schemaMode) {
      const i4 = send('tools/list');
      const res4 = await waitFor(i4, 30000);
      const all4 = (res4.result && res4.result.tools) || [];
      const hit = all4.find((t) => t.name === filter);
      if (!hit) { finish(1, 'no such tool: ' + filter + '\n'); return; }
      finish(0, hit.name + '\n' + JSON.stringify(hit.inputSchema, null, 2) + '\n');
      return;
    }

    if (listMode) {
      const i3 = send('tools/list');
      const res3 = await waitFor(i3, 30000);
      const all = (res3.result && res3.result.tools) || [];
      const picked = filter ? all.filter((t) => t.name.toLowerCase().includes(filter.toLowerCase())) : all;
      const lines = picked.map((t) => t.name + '  ::  ' + String(t.description || '').split('\n')[0].slice(0, 110));
      finish(0, 'count=' + picked.length + ' (total ' + all.length + ')\n' + lines.join('\n') + '\n');
      return;
    }

    const i2 = send('tools/call', { name: toolName, arguments: args });
    const res = await waitFor(i2, 60000);

    if (res.error) {
      finish(1, 'tool error: ' + JSON.stringify(res.error) + '\n');
      return;
    }

    const content = (res.result && res.result.content) || [];
    const texts = content.filter((c) => c.type === 'text').map((c) => c.text);
    const isError = res.result && res.result.isError;
    finish(isError ? 1 : 0, texts.join('\n') + '\n');
  } catch (e) {
    finish(1, 'probe error: ' + e.message + '\n');
  }
})();
