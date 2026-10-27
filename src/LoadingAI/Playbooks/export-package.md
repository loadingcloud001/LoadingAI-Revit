---
name: export-package
description: Export the chosen sheets to PDF and DWG, and the model to IFC, into one folder.
tools: [get_sheets_views, export_pdf, export_dwg, export_ifc]
mode: build
---
1. Ask which sheets (or all), and the folder, if the message does not say.
2. Run get_sheets_views to confirm the sheet list.
3. Run export_pdf, then export_dwg for the views on those sheets, then export_ifc, into the same folder.
4. Report every file written, and any sheet that failed.
