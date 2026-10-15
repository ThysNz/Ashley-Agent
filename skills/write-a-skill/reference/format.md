# Skill format (the same as Claude's)

A skill is a folder. The only required file is `SKILL.md`.

```
my-skill/
  SKILL.md           required: header + instructions
  reference/*.md     optional: long documents the skill points to
  scripts/*.py       optional: scripts the skill tells the agent to run
```

## SKILL.md

```
---
name: my-skill
description: One or two sentences: what it does and when to use it.
argument-hint: "[what to pass in]"
---

# Title

1. First step.
2. Second step, using $ARGUMENTS if the user passed any.
```

## Header fields

- `name`: letters, digits, dash, dot, underscore. Matches the folder name by convention.
- `description`: **required in practice.** This is the only part the agent sees until she loads the skill, so include the situations that should trigger it.
- `argument-hint`: a hint shown to the user, such as `[file] [question]`.
- `disable-model-invocation: true`: the user must run it by typing `/my-skill`.
- `user-invocable: false`: only the agent can load it.

## Body

- Plain markdown instructions. Numbered steps work well.
- `$ARGUMENTS` is replaced with what the user typed after `/my-skill`; `$ARGUMENTS[0]` is the first word.
- `${CLAUDE_SKILL_DIR}` is the skill's folder inside the container, for referring to its scripts: `python3 ${CLAUDE_SKILL_DIR}/scripts/check.py`.
- Refer to extra files by relative name (`reference/notes.md`); the agent reads them with the skill tool's `file` argument.

## Good habits

- Keep `SKILL.md` short (under about 150 lines); move detail into reference files.
- One skill, one job.
- Say what to report back at the end.
- No secrets in skills.
