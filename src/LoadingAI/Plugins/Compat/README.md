# Compat 30+ — pyRevit/Dynamo/AppStore Native Reimplementation (MIT)

All tools below are native C# re-implementations of popular pyRevit / Dynamo (Clockwork, archi-lab, GeniusLoci) / DiRoots / BIMOne functionality. No pyRevit/Dynamo runtime bundled.

Sources now live in `Plugins/core-tools/` (shipped Bridge-only plugin). This folder is documentation only.

6 packages (single DLL, grouped in PluginShelf):

- **query-health** (9, now Bridge-only Core): get_worksets, get_phases, get_design_options, get_sheets_views, get_family_types, get_materials, get_room_area, get_clash_by_bb, get_model_health_score — `Plugins/core-tools/Core/QueryTools.cs`
- **param-excel** (5, now Bridge-only Core): export_params_to_excel, import_excel_to_params, copy_params, replace_text, numbering_auto — `Plugins/core-tools/Core/ParamTools.cs`
- **sheet-doc** (7, now Bridge-only Core): create_sheet, place_view_on_sheet, duplicate_view_ex, set_view_template, set_crop_box, create_dimension, tag_all — `Plugins/core-tools/Core/ViewTools.cs`
- **geometry-create** (8, now Bridge-only Core): create_floor_by_outline, create_roof, create_grid_level, create_family_instance_array, rotate_elements, mirror_elements, split_wall, join_geometry — `Plugins/core-tools/Core/GeometryTools.cs`
- **query-save** (1, now Bridge-only Core): save_document — `Plugins/core-tools/Core/DocumentTools.cs`
- **view-color** (3, now Bridge-only Core): override_color_in_view, color_by_construction_schedule, color_by_parameter — `Plugins/core-tools/Core/ViewTools.cs`
- **param-mutate** (2, now Bridge-only Core): rename_elements, apply_construction_dates — `Plugins/core-tools/Core/ParamTools.cs`
- **worksharing-health** (4, now Bridge-only Core): reload_links, workset_assign — `Plugins/core-tools/Core/DocumentTools.cs`; sync_info, audit_warnings_fix — `Plugins/core-tools/Core/QueryTools.cs`
- **ifc-cost** (1, now Bridge-only Core): quantity_takeoff_by_category — `Plugins/core-tools/Core/QueryTools.cs`
- **export-tools** (plugin): export_pdf, export_dwg, export_ifc, purge_unused — `Plugins/export-tools/`
- **view-tools** (plugin): create_schedule, get_schedule_data, create_floor_plan, create_3d_view, create_section_view, path_of_travel — `Plugins/view-tools/`
- **modeling-tools** (plugin): load_family, change_element_type, create_topography — `Plugins/modeling-tools/`
- **structure-cuts** (plugin): split_beams_by_columns, split_floors_by_beams, split_by_levels, split_by_grids — `Plugins/structure-cuts/`
- **qs-formwork** (plugin): formwork_setup, create_formwork_panels, cleanup_formwork, copy_formwork_parent_params — `Plugins/qs-formwork/`

Total new 37 + core 28 = ~65 tools. Each description embeds alias e.g. "pyRevit: Create Sheets" for LLM matching. L3 default off.
