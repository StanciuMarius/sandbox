# Driving a Sandbox session

You can control a running s&box **Sandbox** game on this machine — spawn props,
build contraptions, weld, bolt on wheels and thrusters, inspect the world, undo,
clean up — by running a command. Nothing needs installing; the game wrote this
file and the script beside it.

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

## Markers: how to say *where*

This is the part worth understanding first, because nearly every build verb
needs to be told what to act on.

Tools in this game aim from the player's eyes. If an agent just fires a tool,
it lands wherever the player's camera happened to be pointing at that instant —
which is unusable for anything precise, and impossible for anything needing two
points on opposite sides of a contraption.

So instead, **the player marks the points and you name them.** They pick the
Marker tool (Q menu → Tools → Marker) and click the spots you need. Each one
gets a letter — A, B, C — drawn in the world as a coloured cross. Markers stick
to the object they were placed on, so they follow it when it moves, and they
survive the player switching to another tool or wandering off.

```
{{SBX}} list_markers
{{SBX}} constrain --kind weld --a A --b B
```

If the player has not placed any, **ask them to** — it is much better than
guessing coordinates. Anywhere a verb takes a target, you can pass:

| Form | Meaning |
|---|---|
| `A`, `B` | a marker, by label — **prefer this** |
| `pointer` | the most recently placed marker |
| `aim` | wherever the player is looking right now |
| `4f2c1a9b` | an object id from `list_props`, or a unique prefix of one |
| `at:120,40,16` | a world position, resolved onto whatever surface is there |

## Building

```
{{SBX}} spawn_prop --ident models/dev/box.vmdl --at A
{{SBX}} place_entity --kind wheel --target B
{{SBX}} place_entity --kind thruster --target C --attach false
{{SBX}} constrain --kind weld --a A --b B
{{SBX}} set_mass --target A --value 250
{{SBX}} remove_object --target A
```

`place_entity` handles thruster, wheel, hoverball, balloon and emitter.
`constrain` handles weld, rope, elastic, slider, ballsocket, nocollide,
hydraulic and linker. Anything else — decal, trail, stacker, resizer — goes
through `use_tool`, which can fire any tool's primary, secondary or reload
action.

Verbs that create something return the ids of what they made, so you can build
on it:

```json
{ "kind": "wheel", "created": ["a41f8c02-..."], "position": "120,40,16" }
```

## Tool settings

Every tool has the same settings the player sees in its panel — rope slack, weld
rigidity, which thruster model to place. `list_tools` shows them with their
current values, and `set_tool_option` changes them. A setting sticks until
changed, so set it *before* the verb that uses it:

```
{{SBX}} list_tools --tool rope
{{SBX}} set_tool_option --tool rope --option Slack --value 50
{{SBX}} constrain --kind rope --a A --b B
```

## Things worth knowing

- **Welding moves things.** The weld tool's Easy Mode is on by default, and it
  works the way it does for a player: welding A to B *moves A* so the two marked
  points touch. That is usually what you want when assembling something. If you
  need both objects to stay put, turn it off first with
  `set_tool_option --tool weld --option EasyMode --value false`.
- **Using a tool switches the player's tool**, exactly as if they had selected
  it. This is deliberate — it is how the tool's own logic and permissions stay on
  the path they were written for, and it lets the player see what you are doing.
- **Positions are `"x,y,z"` strings**, not arrays. One unit is one inch, `+z` is
  up. This is Source engine convention.
- **`spawn_prop` without `--at` places props where the player is looking.** With
  `--at` it places them on a marker and hands back the id. The `--at` form needs
  the player to be hosting the session.
- **Model paths look like `models/dev/box.vmdl`.** A bare path is treated as a
  prop; `prop:`, `entity:` and `dupe.local:` prefixes also work.
- **Run one command at a time.** Each invocation briefly claims a port, so two at
  once will make one of them fail. Do not parallelise these calls.
- **Each call takes about half a second** while the game reconnects. That is
  normal, not a hang.
- **Actions are attributed to the player and are undoable** by them, and prop
  limits and prop protection still apply. Every verb goes through the game's own
  tools and commands, not around them — so a limit refusing you is a real answer,
  not a bug. `get_limits` shows what they are.

## If it does not work

The game must be running, in a session, with the bridge switched on:
**Q menu → Utilities → AI Agent → toggle it on**.

If a call times out, that is almost always the toggle being off, or the game
sitting in the main menu rather than in a session. Ask the player to check
before retrying — retrying will not fix it.

Errors are worth reading rather than working around. An unknown verb reply lists
the valid ones, a rejected argument says what it expected, and a refused action
usually means a spawn limit or an object owned by someone else.
