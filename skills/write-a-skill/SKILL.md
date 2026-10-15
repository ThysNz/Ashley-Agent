---
name: write-a-skill
description: Draft a new skill (a SKILL.md folder) for the agents when asked to write, create or make a skill. Writes the draft into the workspace for the user to install; cannot install it itself.
argument-hint: "[what the skill should do]"
---

# Write a skill

Draft a skill for: $ARGUMENTS

Skills are folders with a `SKILL.md`. You cannot install them (the skills folders are read-only for you). You write a **draft** in the workspace and Thys installs it.

1. **Pick a name:** lowercase words joined by dashes, short and specific (`summarise-l5x`, `weekly-report`).
2. **Read the format reference** if you need it: use the skill tool with `file` = `reference/format.md` on this skill.
3. **Write the draft** to `skill-drafts/<name>/SKILL.md` in the workspace:
   - The header needs `name` and a `description`. The description decides whether the skill gets used, so say **what it does and when to use it**, in one or two sentences.
   - The body is plain instructions: numbered steps, what to check, what to avoid, what to report back. Be concrete and short. Say which tools or files to use.
   - Put long reference material or scripts in extra files (`reference/...`, `scripts/...`) and mention them in the body, rather than making `SKILL.md` long.
4. **Check it** by running the validator on the draft: `python3 ${CLAUDE_SKILL_DIR}/scripts/validate.py skill-drafts/<name>` (run from the workspace). Fix anything it reports.
5. **Tell Thys how to install it:** the draft is in `ashley\workspace\skill-drafts\<name>` (use the real agent folder) and he runs
   `add-skill.cmd <that folder>` from `F:\AI-Agents` (add `-Agent <agent>` to keep it to one agent).

Don't claim the skill is installed. Say it is a draft waiting for him to install.
