---
name: agent-verb-design
description: How to add or change a verb in the agent bridge so an agent can script against it reliably - use when adding a verb, exposing a tool setting, or writing the description and errors an agent will read.
---

# Designing verbs an agent scripts against

The bridge's verbs are an API whose only consumer is a language model. It cannot see the game,
cannot inspect state between calls, and will not notice that a call quietly did the wrong thing.
That constrains the design more than a human-facing API would be.

## Every call is complete on its own

**No hidden state between calls.** A verb writes every setting it cares about on every call -
the value passed, or a default declared next to the parameter. It never inherits whatever the
tool happened to be left on.

```csharp
new() { Name = "count", Property = "StackCount", Default = 1f,
    Description = "How many copies to make, 1 to 50." }
```

`Prepare()` writes all of them, not just the ones passed. So:

```
sbx stack --target A --count 12 --direction Right --gap 4
sbx stack --target A                    # count 1, Up, flush - always
```

This is not a preference. The previous design had `set_tool_option` followed by `use_tool`, and
activating a tool loaded its cookies, so any setting written while the tool was inactive was
overwritten *before* the action ran - and the verb still reported success. Asking for one box
produced three, using a count left in a cookie by a session days earlier.

**Persistence that a player would enjoy is a trap for an agent.** Tool cookies are off for the
agent's toolgun for exactly this reason (see `ToolMode.RemembersSettings`), and it also stopped
the agent overwriting the player's own saved settings.

## Don't leak the implementation

An agent shouldn't need to know about tool modes, activation order, or cookie storage to place a
prop. If using a verb correctly requires knowing the internals, the verb is wrong - add the
parameter rather than documenting the dance.

## Write descriptions and errors for a reader who cannot look

- **State the default.** It's folded into the advertised argument list automatically; make sure
  the wording is worth reading.
- **A shared argument name needs wording that fits every kind that uses it.** `DescribeKindParams`
  takes the *first* description in a group, which is how `--definition` briefly read "Which
  thruster to place" for wheels.
- **Errors should name the valid values**, not just reject. Unknown verb → list the verbs; bad
  enum → list the names; unknown tool → list the tools.
- **Say when an empty result is success.** Tools that modify rather than create return no ids;
  without a note the agent retries. See the `use_tool` description.
- **Never silently no-op.** A setter that accepts a value and discards it is the worst failure
  mode available, because the agent has no way to detect it.

## Keep the surface narrow, and route through real gameplay

Adding a verb widens what an agent can do - prefer a narrow verb over a general one. Every verb
goes through the game's own tools, commands and RPCs, so host authority, ownership, prop limits
and undo all still apply. An agent gets no path a player doesn't have.

Where a tool has a dedicated verb, the generic escape hatch must **refuse** it and name the verb.
Otherwise it's a back door onto exactly the stale state the verb exists to remove.

## After changing a verb

`AgentVerbs._verbs` is a static and survives hotload. Run `bridge_reload_verbs`, then confirm the
new count. See the `sbox-hotload` skill.
