---
name: presentations
description: Build clear PowerPoint-compatible PPTX presentations and verify slide structure and rendering.
---

# Presentations

Use this skill for `.pptx` deliverables.

1. Derive a concise narrative and slide outline from the user's material.
2. Prefer `python-pptx` from `$env:CATASSISTANT_PYTHON`; install extra packages only into `work/python-packages` with `pip --target`.
3. Keep one main idea per slide, readable type, consistent alignment, and accessible contrast.
4. Inspect slide text, relationships, and rendered previews where available. Correct overflow and accidental overlaps.
5. Save final decks in `outputs/` and use `deliver_files` after verification.

Do not fabricate sources, data, logos, or visual verification.
