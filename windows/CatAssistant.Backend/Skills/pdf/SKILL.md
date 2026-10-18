---
name: pdf
description: Extract, create, merge, or inspect PDF documents with page-level validation.
---

# PDF

Use this skill for PDF input or output.

1. Determine whether the PDF contains selectable text, scanned images, forms, or mixed content.
2. Prefer `pypdf` for structure, `pdfplumber` for extraction, and `reportlab` for generation from `$env:CATASSISTANT_PYTHON`.
3. Do not overwrite input files. Write final documents to `outputs/`.
4. Verify page count, text extraction, metadata, and rendered pages where available.
5. Call `deliver_files` only after the output opens and the required pages/content are present.

Do not state that OCR or visual layout was verified unless the corresponding tool actually ran.
