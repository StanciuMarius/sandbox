---
name: verify-in-game
description: Prove a gameplay change actually works by measuring the running scene, rather than asserting it from the code - use after changing anything with a visible or spatial effect, or before telling the user a fix works.
---

# Proving it in the running game

A gameplay change is not verified because the code looks right. It's verified when the scene
says so. Two channels are available and they answer different questions.

## The sbox MCP - what the scene actually contains

Use this for structural claims: does this component exist, is it enabled, what is it parented to.

```
find_game_objects   { "component": "Toolgun" }        → every toolgun, with its path
get_game_object     { "id": "…" }                     → components with Enabled state, children
compile_status      {}                                → did the last build succeed
console_command     { "command": "bridge_reload_verbs" }
```

This is how to answer "did my change take" without guessing. Worked examples from this project:

- **Two toolguns, one per player**, with the human's on `MarkerTool` and the agent's on `MassTool` -
  that single listing proved tool isolation outright.
- **`NoclipMoveMode.Enabled: true`** on the agent pawn, where the prefab ships it `false`. The
  delta from the prefab default is the proof.
- **`"Children": []`** on the agent's toolgun - the `toolgun_vm` child was gone, so no viewmodel.

**The running session is called `system`, and that is the live world.** In play mode
`editor_status` reports `ActiveScene: "system"` and `list_scenes` shows it as the `Game` entry
with a handful of root objects, while `scenes/sandbox.scene` sits alongside it as an inactive
`Scene` tab. That reads like a menu or a bootstrap shell, but `scene_tree` on it shows
`MapLoader` → `worldspawn` and the spawn points: it is the map. Measure against `system`, and
don't conclude from the name that nothing is running - the inactive `sandbox.scene` tab is the
file on disk, not what is playing.

## The sbx CLI - what the game does when driven

```
& "…/data/marsz/sandboxmcp#local/agent/sbx.ps1" <verb> [--arg value]
```

Add `-Json` and pipe through `ConvertFrom-Json` to assert on fields rather than eyeball output.

**PowerShell splits unquoted commas into separate arguments.** `--at at:1,2,3` arrives as three
tokens and fails to parse. Always quote: `--at "at:1,2,3"`.

## Measure, don't eyeball

Positions are the cheapest hard evidence available. Compute the delta and check it against what
the code should have produced:

- Companion standoff: target at `-111.78,297.22,407`, pawn at `-175.97,249.47,382` →
  `sqrt(64.19² + 47.75²)` = **80.0**, exactly `Standoff`. Repeated across three separate calls.
- Stacker spacing: step of `(71.79, 17.95, 0)` = **74.0** = 50-unit box + 24 gap, and `z` unchanged
  proves it went sideways rather than up.

A number that matches a constant in the code to the decimal is proof. "It looked right" is not.

## Test the default path, not just the explicit one

The interesting bug is usually leakage between calls. After proving an explicit call works, run
the same verb **with no arguments** and confirm it reverts to documented defaults. That's what
demonstrated the stateless-verb design; the explicit call alone would have proved nothing.

## Beware measuring at the wrong moment

Behaviour with a timer will fool you. A companion measured 456 units from its work looked like a
broken `PoseAt`; it had done the job correctly and then walked back after its idle timeout. If a
result contradicts the code, check whether something time-based has already moved on.
