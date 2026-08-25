---
name: sbox-hotload
description: What survives an s&box hotload and what silently doesn't - use when a code change appears not to take effect, when something works after a play restart but not after an edit, or before writing long-lived async loops, static caches or event handlers in game code.
---

# Surviving the s&box hotload

The editor rebuilds and hotloads on save. Hotload keeps the running scene and swaps the
assembly under it, which means some state carries across and some is silently destroyed.
Nearly every "my change didn't work" in this project traces back to that split.

## The rule

**Symptom: works after a play restart, broken after an edit → suspect hotload-surviving state.**
That single heuristic would have caught most of the bugs below on first sight.

## What survives

| Survives | Consequence |
|---|---|
| Static field *contents* | The static constructor does **not** re-run. A table built once at startup keeps serving stale data. |
| Plain delegates and frame listeners | `Listen(Stage.StartUpdate, …)` keeps ticking. Prefer these for anything long-lived. |
| Component instances and scene objects | But a static caching "the" instance can now point at a dead one. |
| An open WebSocket | Event-driven callbacks keep firing. Nothing needs to await it. |

## What does not

| Discarded | Consequence |
|---|---|
| Async state machines | A `while (true) { await … }` loop dies mid-flight and never resumes. |
| `finally` blocks in a running async method | The task is dropped **without unwinding**. A flag cleared in `finally` stays set forever. |
| `GameObjectSystem` constructors | They do **not** re-run. Registrations made there exist only from scene start. |
| Some lambdas | `Unable to find matching substitution for a lambda method` in the console. Use a **method group** for any handler that outlives the call that registered it. |

## How to write code that tolerates it

- **Validate a static cache before trusting it, then re-latch.** See `Player.FindLocalPlayer()` -
  check `IsValid()`, fall back to a scene query, store the result. Don't just read the field.
- **Never rely on an infinite async loop.** Use a frame listener that starts short-lived tasks.
  See `AgentBridge.Tick()`.
- **Never rely on `finally` in async to release a guard.** Bound it by time instead: if the flag
  has been set longer than the work could plausibly take, treat it as stale and proceed.
- **Subscribe long-lived events with method groups**, not lambdas.
- A fix to hotload behaviour **cannot validate itself** - the hotload that installs it runs under
  the old code, and constructors don't re-run. It needs one play restart, then test properly by
  making a further edit.

## Checking whether a change landed

- **The editor compiles on save, focused or not.** Never ask the user to click into the editor.
- Use the sbox MCP `compile_status` tool. Do **not** judge from `read_console`: successful builds
  log little, only failures are loud, and stale failure lines look current.
- Better still, test the behaviour - e.g. `bridge_reload_verbs` and count the verbs.
- One real exception: a directory created *after* the project loaded isn't watched. That needs a
  full editor restart, not a focus. If a whole new folder seems invisible, that's why.

## Known statics in this project that need a manual nudge

- `AgentVerbs._verbs` - run `bridge_reload_verbs` after adding or changing a verb.
- `AgentBridge` - self-heals now via its tick, but `bridge_connect` still forces it.
