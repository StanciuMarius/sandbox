# Driving a Sandbox session

You can control a running s&box **Sandbox** game on this machine — spawn props,
inspect the world, undo, clean up — by running a command. Nothing needs
installing; the game wrote this file and the script beside it.

## The command

```
{{SBX}} <verb> [--arg value]...
```

Run it with no verb to see everything the game currently offers:

```
{{SBX}}
```

That prints the live verb list with a description of every argument. **Start
there** — the list comes from the running game, so it is always correct, and it
may include verbs not mentioned in this file.

## Examples

```
{{SBX}} spawn_prop --ident models/dev/box.vmdl
{{SBX}} list_props --limit 10
{{SBX}} trace
{{SBX}} undo
{{SBX}} cleanup --scope mine
```

## What comes back

Success prints JSON on stdout:

```json
{
  "total": 2,
  "returned": 2,
  "props": [ { "id": "...", "model": "...", "position": "201.06,293.02,407" } ]
}
```

Failure prints an error and exits non-zero. Errors are normal and worth reading
— an unknown verb reply lists the valid ones, and a rejected argument says what
it expected.

## Things worth knowing

- **Positions are `"x,y,z"` strings**, not arrays. One unit is one inch, `+z` is
  up. This is Source engine convention.
- **`spawn_prop` places props where the player is looking.** It traces from their
  eyes and drops the prop on the first surface hit, so you generally do not pass
  a position. Use `trace` first if you want to know what they are pointing at.
- **Model paths look like `models/dev/box.vmdl`.** A bare path is treated as a
  prop; `prop:`, `entity:` and `dupe.local:` prefixes also work.
- **Run one command at a time.** Each invocation briefly claims a port, so two at
  once will make one of them fail. Do not parallelise these calls.
- **Each call takes about half a second** while the game reconnects. That is
  normal, not a hang.
- **Actions are attributed to the player and are undoable** by them, and prop
  limits still apply. You are going through the game's own commands, not around
  them.

## If it does not work

The game must be running, in a session, with the bridge switched on:
**Q menu → Utilities → AI Agent → toggle it on**.

If a call times out, that is almost always the toggle being off, or the game
sitting in the main menu rather than in a session. Ask the player to check
before retrying — retrying will not fix it.
