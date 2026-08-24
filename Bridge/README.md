# sandbox-bridge

An MCP bridge that lets a coding agent drive a live [Sandbox](https://sbox.game) session.

## Install

```
claude mcp add --scope user sandbox -- npx -y @marsz/sandbox-bridge
```

That's the whole install. Claude Code launches the bridge itself at the start of
each session and stops it at the end — there is no process to run, no service to
install, and nothing to uninstall. Requires Node 18+.

`--scope user` is not optional. `claude mcp add` defaults to *local* scope, which
registers the server only for the directory you ran it in. Playing a game is not a
project, so you have no directory to be in — without `--scope user` the server
works in whatever folder you happened to be in and appears to vanish everywhere
else.

Then, in the game console:

```
sb.bridge true
bridge_connect
```

`sb.bridge` is saved, so it only needs setting once. Run `bridge_status` to check.

## How it works

```
  Claude Code  ──stdio──▶  bridge  ◀──WebSocket──  Sandbox client
   (spawns it)              (us)      (game dials out)
```

The game cannot accept connections — s&box gives game code a WebSocket *client*
and no listener — so the game dials out and the bridge holds the socket. That is
why this works from a published client behind NAT with no ports opened.

On connect the game sends a `hello` carrying its verb table, and the bridge turns
those verbs into MCP tools. **The bridge has no idea what any verb does.** Adding
a verb in `Code/AgenticBridge/AgentVerbs.cs` makes it appear in the agent's tool
list automatically — this package never needs republishing to stay in sync.

## What the agent can do

Only the verbs the game declares. This is not a console pipe: every verb routes
through a command or RPC the game already exposes, so host authority, per-player
ownership, prop limits and the undo stack all still apply. Widening what an agent
can do is a deliberate edit to the verb table, reviewable in one file.

## Ports

s&box only permits `localhost` connections on **80, 443, 8080, 8443**, and rejects
raw IP literals (`ws://127.0.0.1:8080/` fails purely for being an address — use the
hostname). The bridge binds the first of those that is free, and the game scans the
same list in the same order, so they find each other without configuration.

`ws://localhost:80/` and `:443` generally need elevation to bind, so in practice
this is 8080, falling back to 8443.

To pin one explicitly, set `sb.bridge_url` in the game console:

```
sb.bridge_url "ws://localhost:8443/"
```

## Protocol

Game to bridge, once on connect:

```json
{ "type": "hello", "game": "marsz.sandboxmcp", "isHost": true,
  "verbs": [ { "name": "spawn_prop", "description": "...",
               "args": { "ident": "..." } } ] }
```

Bridge to game, per tool call:

```json
{ "id": "1", "verb": "spawn_prop", "args": { "ident": "models/dev/box.vmdl" } }
```

Game to bridge, per reply:

```json
{ "id": "1", "ok": true, "result": { } }
```

A failed verb comes back with `"ok": false` and an `error` string. The bridge
surfaces that to the agent as a tool result marked `isError`, not as a protocol
failure, so the agent can read it and adapt.

## Development

```
cd Bridge
npm install
node index.js      # logs to stderr; stdout is the MCP channel
```

Point a local Claude Code at your checkout instead of npm with:

```
claude mcp add --scope user sandbox -- node /absolute/path/to/Bridge/index.js
```
