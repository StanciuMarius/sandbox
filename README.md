<div align="center">
  <h3 align="center">agentic_sandbox</h3>

  <p align="center">
    A fork of Facepunch's <a href="https://github.com/Facepunch/sandbox">Sandbox</a> where an AI
    coding agent can build alongside you in a live session.
  </p>
</div>
<br/>

The agent gets a body. It spawns a companion character carrying its own toolgun, flies it over to
whatever it's working on, and builds — while you carry on with your own tools, undisturbed.

## Using it

1. Start a session and turn the bridge on: **Q menu → Utilities → AI Agent**. It's off by default.
2. Hit copy. You get a one-line prompt containing the path to a README the game just wrote.
3. Paste it to your agent. That file explains the whole interface, so you don't have to.

Nothing to install. The CLI is a PowerShell script the game unpacks beside the README, and the
WebSocket server it needs comes from the .NET framework.

```
sbx spawn_prop --ident models/dev/box.vmdl --at A
sbx stack --target A --count 12 --direction Right --gap 4
sbx constrain --kind rope --a A --b B --slack 50
```

**Markers are how you say *where*.** Pick the Marker tool and click a few spots; each gets a
letter. Naming a marker beats describing a position, and it means the agent isn't guessing from
wherever your camera happens to point.

## How it works

The game dials out to a port the CLI is listening on — s&box gives game code a WebSocket client
but no listener, so the connection goes that way round. Each call is one invocation: bind, wait,
send, print, exit.

Two rules shape everything behind that:

**An agent gets no path a player doesn't have.** Every verb runs the game's own tools, commands
and RPCs, raising the same events a real click does. So host authority, ownership, prop
protection and spawn limits all still apply, and a refusal is a real answer rather than a bug.
The companion is a second body, not a second account — what it builds lands on your undo stack
and counts against your budget.

**A verb call is complete on its own.** Tool settings are arguments with declared defaults,
written in full on every call. Nothing carries over, so the same command always builds the same
thing — which is the only thing worth scripting against.

## Upstream

This tracks [Facepunch/sandbox](https://github.com/Facepunch/sandbox). Everything that makes the
game a game is theirs; this fork adds `Code/AgenticBridge/`, the Marker tool, and the changes
needed to let a second, non-human player exist.

[MIT](LICENSE) — Copyright (c) 2026 Facepunch
