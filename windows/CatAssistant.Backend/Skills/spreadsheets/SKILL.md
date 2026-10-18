---
name: spreadsheets
description: Create, transform, and validate XLSX or CSV workbooks with formulas, formatting, and charts.
---

# Spreadsheets

Use this skill for `.xlsx`, `.csv`, and tabular analysis.

1. Inspect headers, types, missing values, formulas, and units before transformation.
2. Prefer `openpyxl` for XLSX and Python's CSV support for CSV; use `$env:CATASSISTANT_PYTHON` and put extras in `work/python-packages`.
3. Preserve source data unless the user requests replacement. Put generated workbooks in `outputs/`.
4. Validate formulas, sheet names, merged ranges, dimensions, number formats, and chart source ranges.
5. Re-open the saved workbook and verify key cells before `deliver_files`.

Do not silently replace formulas with guessed values.
