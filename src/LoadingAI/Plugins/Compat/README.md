# Compat 30+ — pyRevit/Dynamo/AppStore Native Reimplementation (MIT)

All tools below are native C# re-implementations of popular pyRevit / Dynamo (Clockwork, archi-lab, GeniusLoci) / DiRoots / BIMOne functionality. No pyRevit/Dynamo runtime bundled.

6 packages (single DLL, grouped in PluginShelf):

- **query-health** (9): get_worksets, get_phases, get_design_options, get_sheets_views, get_family_types, get_materials, get_room_area, get_clash_by_bb, get_model_health_score
- **param-excel** (5): export_params_to_excel, import_excel_to_params, copy_params, replace_text, numbering_auto
- **sheet-doc** (7): create_sheet, place_view_on_sheet, duplicate_view_ex, set_view_template, set_crop_box, create_dimension, tag_all
- **geometry-create** (8): create_floor_by_outline, create_roof, create_grid_level, create_family_instance_array, rotate_elements, mirror_elements, split_wall, join_geometry
- **worksharing-health** (4): reload_links, workset_assign, sync_info, audit_warnings_fix
- **ifc-cost** (1): quantity_takeoff_by_category
- **export-tools** (plugin): export_pdf, export_dwg, export_ifc, purge_unused — `Plugins/export-tools/`
- **view-tools** (plugin): create_schedule, get_schedule_data, create_floor_plan, create_3d_view, create_section_view, path_of_travel — `Plugins/view-tools/`
- **modeling-tools** (plugin): load_family, change_element_type, create_topography — `Plugins/modeling-tools/`
- **structure-cuts** (plugin): split_beams_by_columns, split_floors_by_beams — `Plugins/structure-cuts/`
- **qs-formwork** (plugin): formwork_setup, create_formwork_panels, cleanup_formwork, copy_formwork_parent_params — `Plugins/qs-formwork/`

Total new 37 + core 28 = ~65 tools. Each description embeds alias e.g. "pyRevit: Create Sheets" for LLM matching. L3 default off.
