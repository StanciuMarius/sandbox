---
name: feature-env
description: Build and prove a feature in an isolated env - its own worktree, branch and s&box editor - then leave a conclusion behind. Use when asked to develop something independently, in the background, or without disturbing the editor the user is working in.
---

# Developing a feature in its own env

An env is a git worktree, a branch and an editor instance that belong to you alone. The point is
that you can compile, hotload, enter play mode and break things without touching the session the
user has open. Nothing here is shared except the s&box install itself.

```
./tools/agent-env/env.ps1 setup <feature>
```

`<feature>` is lowercase, digits and dashes - it becomes the branch `agent/<feature>`, the
directory `../sandbox-envs/<feature>`, and the project Ident `sandboxmcp-<feature>`.

Setup branches from the current HEAD, seeds the worktree with the main checkout's compiled and
cloud assets, launches the editor and waits until that editor answers. About a minute, most of it
the 1.4GB seed.

**Don't pass `-NoSeed`** unless you are deliberately testing a cold build. A new Ident means the
engine otherwise compiles and re-downloads everything, which costs minutes - and until it
finishes the game renders untextured, so a screenshot taken then looks like a rendering bug
rather than a half-built cache. That is a good way to report a fault that doesn't exist.

## What isolation actually buys, and what it costs

The worktree isolates the code. Rewriting `Ident` isolates everything the engine keys off it -
the asset cache, `data/marsz/<ident>#local`, the input config, the saved convars. A per-env MCP
port isolates the editor's tool server.

Three consequences you have to live with:

- **`sandbox.sbproj` and `.mcp.json` are modified in your worktree and hidden from git** with
  `update-index --skip-worktree`. They will not show in `git status` and cannot be committed by
  accident. **Do not edit either file, and never clear the skip-worktree bit** - the Ident is what
  keeps your editor out of the user's asset cache.
- **Envs are launched one at a time.** The MCP port is an editor-wide preference read at startup,
  so setup writes it, launches, and waits for the bind before anything else may launch. Run
  `setup` for one env, let it finish, then the next.
- **At most two envs can hold an `sbx` bridge at once**, and the user's own session competes for
  the same two. The engine only lets game code dial localhost on 80/443/8080/8443, and the last
  two need elevation to bind. Setup assigns 8080 then 8443, and tells you when there are none
  left. An env without a bridge is still fully usable - everything except the `sbx` verbs runs
  over MCP, which has no such cap.

## Always confirm which editor you are driving

This is the failure that matters. If you drive the wrong editor you will be measuring the user's
session and reporting it as your own result.

```
./tools/agent-env/env.ps1 mcp <feature> -Tool editor_status
```

`Paths.ProjectRoot` must be `...\sandbox-envs\<feature>`. The `env.ps1` commands all check this
for you - they find the editor by matching the project root and refuse to talk to anything else,
so the recorded port is only ever a hint.

If you are working **inside the worktree in your own session**, its `.mcp.json` already points at
your env's port, so the `mcp__sbox__*` tools reach your editor directly. Check `editor_status`
once at the start anyway. If you are working from the main checkout, the `mcp__sbox__*` tools go
to whatever holds the default port - which is the user's editor. Use `env.ps1 mcp` instead.

## Driving the game

Setup leaves the editor in play mode, because a stopped editor has nothing to measure. Control it
with:

```
./tools/agent-env/env.ps1 play <feature>            # press play, wait until it really is playing
./tools/agent-env/env.ps1 play <feature> -Stop      # back to edit mode
```

`play_start` plays whichever scene is current, and the MCP registry has no way to open one - so
setup seeds the worktree's project cookies with the project's `StartupScene` before launching.
That is why the env comes up somewhere playable.

**Use `env.ps1 play`, not the `play_start` tool directly.** Entering play mode reloads the saved
convars, which wipes this env's bridge pin and drops the game back to scanning from 8080 - where
it can be answered by the user's session. `env.ps1 play` reapplies the pin afterwards; the raw
tool doesn't. If you must use the tool, run `env.ps1 play <feature>` again straight after.

On an env's first launch the editor answers MCP long before the engine has finished compiling
assets, and a `play_start` issued in that window is dropped without an error. That is why
`env.ps1 play` keeps asking rather than asking once - if you drive play yourself, do the same.

If your env got a bridge port, the `sbx` verbs are pinned to it:

```
$env:SBX_PORT = '<the port setup printed>'
& "<engine>\data\marsz\sandboxmcp-<feature>#local\agent\sbx.ps1" list_props
```

**Always set `SBX_PORT`.** Left to scan, every `sbx` call starts at 8080, so without it your call
can be answered by the user's game instead of yours - and it will look like it worked.

## Build

Ordinary work in `../sandbox-envs/<feature>`, on the `agent/<feature>` branch. Commit there as you
go; the branch is what survives teardown.

The editor compiles on save. Never ask the user to focus a window, and check `compile_status`
rather than reading the console for success - see the **sbox-hotload** skill, which also explains
why a change can appear not to take and what to do about it.

## Prove it

Follow the **verify-in-game** skill: measure the running scene, don't assert from the code. A
number that matches a constant to the decimal is proof; "it looked right" is not.

Then, **if the change is visible at all, capture it**:

```
./tools/agent-env/env.ps1 shot <feature> -Name after-fix
./tools/agent-env/env.ps1 shot <feature> -Name overview -EditorView
./tools/agent-env/env.ps1 shot <feature> -Name closeup -Camera <id> -Width 1600 -Height 900
```

Without `-EditorView` this renders the scene's main camera, which is what a player sees; with it,
the editor viewport, which is better for showing spatial layout. Point the viewport first with
`set_editor_camera`. Files land in `.agent-runs/<feature>/`.

**Take the shot after you have measured, not instead of measuring.** A screenshot shows a reader
what happened; it does not establish that the numbers are right. Where a before/after pair makes
the change legible, take both - capture `before` prior to the change rather than reconstructing it
afterwards.

## Leave a conclusion

Write `.agent-runs/<feature>/conclusion.md` before tearing anything down. This is the only thing
the user is guaranteed to read, and by the time they read it the editor is gone - so it has to
stand on its own.

```markdown
# <feature>

**Outcome:** working | partly working | not working | abandoned
**Branch:** agent/<feature> (from <base commit>)

## What changed
Two or three sentences, and the files.

## Evidence
The measurement, with the numbers, and what they were checked against.
Screenshots by filename, each with what it shows.

## What I could not prove
The parts still resting on reading the code rather than measuring it.

## What is left
Anything unfinished, and anything the user needs to decide.
```

State the outcome plainly. A conclusion saying a thing half works is useful; one that implies it
works because the code looks right is worse than nothing, because the env is gone and nobody can
check. If you never got it working, say so and write down what you learned - that is a result.

## Tear down

```
./tools/agent-env/env.ps1 teardown <feature>
```

Closes the editor, removes the worktree, **keeps the branch and keeps `.agent-runs/<feature>/`**.
Removing the worktree reclaims the ~1.4GB of seeded assets, so tear an env down once you are done
with it rather than leaving it parked.

Add `-Purge` to also delete the engine state your Ident created - the asset cache and data
directory. Worth doing when you are finished with a line of work; skip it if you will set the
same env up again.

Add `-DeleteBranch` only once the work is merged or explicitly abandoned. Never delete a branch
you have not been told to.

## Checking on things

```
./tools/agent-env/env.ps1 status
```

Every env, its branch, and whether its editor is up.
