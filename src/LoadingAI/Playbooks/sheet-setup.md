---
name: sheet-setup
description: Create sheets from a list, place a view on each and apply a view template.
tools: [get_sheets_views, resolve_names, create_sheet, place_view_on_sheet, set_view_template]
mode: build
---
1. Ask the user for the sheet numbers and names if the message does not list them. Ask which view goes on each sheet and which view template to use.
2. Run get_sheets_views. Use resolve_names for every view and template name, and stop to ask when a name has no exact match.
3. For each sheet: create_sheet, then place_view_on_sheet, then set_view_template on the placed view.
4. Report each sheet with its view, and anything skipped and why.
