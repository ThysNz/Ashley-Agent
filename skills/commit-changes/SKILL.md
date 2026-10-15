---
name: commit-changes
description: Review and commit the current changes in a git repository in the workspace, with a clear commit message. Use when asked to commit, save work to git, or write a commit message. Never pushes (the user pushes).
argument-hint: "[repo folder] [what the change is about]"
---

# Commit changes

Commit the work in a git repository in the workspace. Arguments, if given: $ARGUMENTS

1. **Find the repo.** If a folder was named (for example `demo`), that folder is the repo: from now on run EVERY git command with the run_command tool's
   `workdir` set to that folder (`workdir: "demo"`), because commands otherwise run in the workspace root, which is not a repo. If no folder was named, use the folder you
   were working in, or list the workspace with list_files to find the one with a `.git` folder. If there is no repo, say so and stop. Never run `git init` unless asked.
2. **Look before you commit.** Run `git status --short` and `git diff --stat` (with the same `workdir`). For anything non-obvious run `git diff` on that file.
   Lines starting with `??` in `git status` are NEW files and count as changes even though `git diff` shows nothing for them: they need `git add`. Only if `git status --short` prints nothing is there nothing to commit.
3. **Say briefly what changed** (files and a one-line summary), then carry straight on: Thys asked for the commit by running this skill, so do NOT ask for
   confirmation. If something looks unintended (large generated files, secrets, tokens,
   `.env` files, build output), leave it out and say why.
4. **Stage only what belongs to this change:** `git add <files>`. Don't use `git add -A` blindly.
5. **Write the message:** a short summary line (about 60 characters, imperative: "Add ...", "Fix ..."), then a blank line and a few lines on *why* if it isn't obvious.
   One logical change per commit; if the work is several changes, make several commits.
6. **Commit** with `git commit -m "..."` and show `git log --oneline -3`.
7. **Do not push.** Pushing is turned off here. Finish by telling Thys the repo folder and the branch, and that he pushes from his own PC, for example:
   "Committed on `main` in `RA-Code-Reader` (in my workspace); push it from there on your PC."

If the repo has no commits or the branch is detached, say so rather than guessing.
