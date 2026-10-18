---
name: documents
description: Create and edit Word-compatible DOCX documents with structured content and visual verification.
---

# Documents

Use this skill for `.docx` deliverables. Work only under the current conversation workspace unless an external operation is explicitly approved.

1. Inspect the source material before drafting.
2. Prefer `python-docx` from `$env:CATASSISTANT_PYTHON`. Put task-specific packages in `work/python-packages` with `pip --target`, never in the shared runtime.
3. Preserve headings, lists, tables, links, page breaks, headers, and footers intentionally.
4. Render or inspect the finished document before delivery. Fix obvious clipping, empty pages, broken tables, and inconsistent typography.
5. Save final files under `outputs/` and call `deliver_files` only after verification.

Never claim a document was visually checked when only its XML or text was inspected.
