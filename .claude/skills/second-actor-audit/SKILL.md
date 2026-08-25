---
name: second-actor-audit
description: Adding a second instance of something the codebase assumed there was only one of - use before introducing an extra player, camera, controller or owner, and when a batch of unrelated-looking bugs appears right after doing so.
---

# Adding a second of something the code assumed was singular

A codebase encodes its assumptions in conditions, not comments. When you add a second player,
camera or controller, the assumption doesn't fail loudly - it produces a scatter of unrelated-
looking bugs, each of which invites a local patch. Resist patching them one at a time.

## The tell

**Several unconnected symptoms appearing right after one structural change is one root cause
wearing many hats.** Adding an agent pawn owned by the same `Connection` as its player produced
five, and each looked like its own bug:

| Symptom | Site |
|---|---|
| Verbs lost track of the player | `Player.LocalPlayer` latched by the wrong pawn |
| Camera misbehaved | `ICameraModifier.ModifyCamera` |
| Tools fired when the human clicked | `ToolMode.DispatchActions` |
| Pawn drifted on jump/duck | `NoclipMoveMode.UpdateMove` |
| Second pair of arms on screen | `ViewModelPrefab` |

All five were the same sentence: `!IsProxy` had been standing in for "I am the human at this
keyboard", which stopped being true the moment a second pawn shared the owner's connection.

## The procedure

1. **Name the invariant that just stopped holding.** Write it as a sentence. Here: *one `Player`
   per `Connection`, so anything I own is me.*

2. **Grep for the checks that encoded it implicitly.** They rarely mention the concept by name:
   - the negated-proxy / negated-remote idiom (`!IsProxy`, `IsLocalPlayer`, `IsMine`)
   - `FirstOrDefault(x => x.Owner == c)` and other "the one for this owner" lookups
   - statics caching *the* instance
   - direct input reads (`Input.Pressed(...)`) on anything per-actor
   - "already has one?" guards - `GameManager.SpawnPlayer`'s dedup would have stopped the human
     ever respawning

3. **Split the meanings before you fix anything.** The same expression usually answers two
   different questions:
   - *"is this mine to drive?"* → needs the new, narrower predicate
   - *"do I have authority to mutate this?"* → correct as-is, leave alone

   The `IsProxy` checks inside the tools mean the second thing and were deliberately untouched.

4. **Introduce one named predicate and let it do the work.** `IsLocalPlayer` already existed and
   already meant the right thing, so redefining it as `!IsProxy && !IsAgent` fixed fifteen sites
   at once and left five needing hand edits. A named concept also gives future readers - and the
   next upstream merge - something to grep for.

5. **Check the engine's own knobs, not just your code.** Serialized component defaults will
   happily assume the same thing. Reading `player.prefab`'s `PlayerController` block found four
   in one go: `UseInputControls`, `UseLookControls`, `UseCameraControls`, and
   `HideBodyInFirstPerson` - the last of which would have made the new actor invisible.

6. **Check shared stores keyed by type rather than by owner.** Tool cookies key on
   `tool.stackertool.*`, so the second actor silently overwrote the first's saved settings. Any
   cache, cookie or preference keyed by class name is suspect.

## Prefer sharing identity to inventing one

Giving the pawn its owner's `Connection` meant undo, prop protection and prop limits kept working
with no new plumbing, and stopped the second actor being a way around either. The cost was this
audit. A separate identity would have avoided the audit and required null-tolerance across undo,
ownership, nameplates and player data instead. Sharing was much the cheaper trade - but only
because the audit was done deliberately rather than one bug report at a time.

## Afterwards

Record the new predicate somewhere durable. Upstream merges bring in fresh `!IsProxy` checks
written under the old assumption, and they need re-auditing each time.
