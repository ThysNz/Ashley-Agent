# Shared skills

Skills in this folder are available to **every agent**. A skill used by one agent only goes in that agent's own `skills\` folder
(for example `agent2\skills\`); if both have a skill with the same name, the agent's own wins.

A skill is a folder containing a `SKILL.md` and, optionally, other files (scripts, reference documents). The format is the same one
Claude uses, so skills written for Claude work here:

```
skills\
  commit-changes\
    SKILL.md              required
    reference\notes.md    optional extra files
    scripts\check.py      optional scripts
```

`SKILL.md` starts with a short header, then the instructions in markdown:

```
---
name: commit-changes
description: What the skill does and WHEN to use it. This line is what the agent reads to decide.
argument-hint: "[what to commit]"
---

# Instructions
1. ...
```

## Header fields that are used

| Field | Meaning |
|---|---|
| `name` | letters, digits, `-`, `.`, `_` (defaults to the folder name) |
| `description` | **the most important line**: say what it does and when to use it. The agent only sees this until it loads the skill |
| `argument-hint` | shown in the page, e.g. `[repo] [message]` |
| `disable-model-invocation: true` | only you can run it (by typing `/name`); the agent will not load it by herself |
| `user-invocable: false` | the agent can load it, but it is not offered as a `/name` command |

Other Claude fields (`allowed-tools`, `model`, `context`, `hooks`...) are ignored. In the body, `$ARGUMENTS`, `$ARGUMENTS[0]` and
`${CLAUDE_SKILL_DIR}` are filled in. Skills that name Claude's tools (Bash, Read, Write, Edit, Grep, Glob, WebFetch, WebSearch) work: the agent is
told to use her own equivalents (`run_command`, `read_file`, `write_file`, `edit_file`, `search_files`, `find_files`, `web_fetch`, `web_search`).
Not supported: subagents, hooks, and `!` command injection.

## Adding a skill

- **Drop the folder in** (here, or in an agent's own `skills\`). It is picked up within a couple of seconds, with no restart.
- **Or run** `add-skill.cmd` from `F:\AI-Agents`: `add-skill.cmd C:\Downloads\my-skill` (a folder, a `.zip`, or a lone `SKILL.md`).
  Add `-Agent agent2` to install it for one agent only. It checks the format first.
- **Or ask an agent to draft one:** "write a skill for X". She writes a draft into her workspace (`workspace\skill-drafts\<name>\`) and you install it
  with `add-skill.cmd`. She cannot install skills herself: these folders are read-only inside her container.

## Using a skill

- **Automatically:** the agent sees every skill's name and description, and loads one when a request matches.
- **By name:** type `/skill-name your request` in the chat (page or console). The page offers matching names as you type `/`.
- The page's **Skills** button lists what is installed.

## Only install skills you trust

A skill is a set of instructions the agent follows, and she has a shell. Read a skill from the internet before you install it.
