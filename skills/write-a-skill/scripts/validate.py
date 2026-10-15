#!/usr/bin/env python3
"""Checks a skill folder: python3 validate.py <skill folder>

Prints problems (and warnings) and exits 1 if the skill would not load.
"""
import os
import re
import sys


def main():
    if len(sys.argv) != 2:
        print("usage: validate.py <skill folder>")
        return 2

    folder = sys.argv[1].rstrip("/\\")
    if not os.path.isdir(folder):
        print(f"ERROR: '{folder}' is not a folder.")
        return 1

    files = {f.lower(): f for f in os.listdir(folder)}
    if "skill.md" not in files:
        print("ERROR: no SKILL.md in the folder.")
        return 1

    text = open(os.path.join(folder, files["skill.md"]), encoding="utf-8-sig").read().replace("\r\n", "\n")
    errors, warnings = [], []

    if not text.startswith("---\n"):
        errors.append("SKILL.md must start with a '---' header containing name and description.")
        meta, body = {}, text
    else:
        end = text.find("\n---", 4)
        if end < 0:
            errors.append("The header is not closed with a second '---' line.")
            meta, body = {}, text
        else:
            header, body = text[4:end], text[end + 4:]
            meta = {}
            for line in header.split("\n"):
                m = re.match(r"^([A-Za-z0-9_-]+):\s*(.*)$", line)
                if m:
                    meta[m.group(1).lower()] = m.group(2).strip().strip("\"'")

    name = meta.get("name") or os.path.basename(os.path.abspath(folder))
    if not re.match(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", name):
        errors.append(f"The name '{name}' is not valid (letters, digits, dash, dot, underscore; at most 64 characters).")
    if "name" in meta and meta["name"] != os.path.basename(os.path.abspath(folder)):
        warnings.append(f"The name '{meta['name']}' differs from the folder name '{os.path.basename(os.path.abspath(folder))}'.")

    desc = meta.get("description", "")
    if not desc or desc in (">", "|"):
        warnings.append("No one-line description: the agent decides whether to use the skill from it. Add 'description: ...' saying what it does and when to use it.")
    elif len(desc) < 30:
        warnings.append("The description is very short; say what the skill does AND when to use it.")
    elif len(desc) > 500:
        warnings.append("The description is long (over 500 characters) and will be cut.")

    lines = body.strip().split("\n")
    if len(body.strip()) < 40:
        errors.append("The instructions are empty or too short.")
    if len(lines) > 200:
        warnings.append(f"SKILL.md is {len(lines)} lines; move detail into reference files.")

    # files mentioned as reference/... or scripts/... should exist
    for rel in set(re.findall(r"\b((?:reference|references|scripts)/[\w./-]+)", body)):
        rel = rel.rstrip(".,)")
        if not os.path.exists(os.path.join(folder, rel)):
            warnings.append(f"The instructions mention '{rel}' but that file is not in the skill folder.")

    for e in errors:
        print("ERROR:  " + e)
    for w in warnings:
        print("warning: " + w)
    if not errors and not warnings:
        print(f"OK: '{name}' looks good ({len(lines)} lines).")
    elif not errors:
        print(f"OK, with {len(warnings)} warning(s): '{name}' will load.")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
