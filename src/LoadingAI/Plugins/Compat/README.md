# Compat — what moved

These tools are native C# re-implementations of popular pyRevit, Dynamo and DiRoots workflows. Nothing here is code: the sources live in the shipped plugins and this folder is documentation only.

| Group | Tools | Where |
|---|---|---|
| Query and health | `get_worksets`, `get_phases`, `get_design_options`, `get_sheets_views`, `get_family_types`, `get_materials`, `get_room_area`, `get_clash_by_bb`, `get_model_health_score`, `sync_info`, `audit_warnings_fix`, `quantity_takeoff_by_category` | `Plugins/core-tools/Core/QueryTools.cs` |
| Parameters | `export_params_to_excel`, `import_excel_to_params`, `copy_params`, `replace_text`, `numbering_auto`, `rename_elements`, `apply_construction_dates` | `Plugins/core-tools/Core/ParamTools.cs` |
| Sheets and views | `create_sheet`, `place_view_on_sheet`, `duplicate_view_ex`, `set_view_template`, `set_crop_box`, `create_dimension`, `tag_all`, `override_color_in_view`, `color_by_construction_schedule`, `color_by_parameter` | `Plugins/core-tools/Core/ViewTools.cs` |
| Geometry | `create_floor_by_outline`, `create_roof`, `create_grid_level`, `create_family_instance_array`, `rotate_elements`, `mirror_elements`, `join_geometry` | `Plugins/core-tools/Core/GeometryTools.cs` |
| Worksharing and document | `reload_links`, `workset_assign`, `save_document` | `Plugins/core-tools/Core/DocumentTools.cs` |
| Export, views, modeling | `export_*`, `purge_unused`, `create_schedule` and friends, `load_family`, `change_element_type`, `create_topography` | `Plugins/export-tools`, `view-tools`, `modeling-tools` |
| Structure and formwork | `split_*`, `formwork_*` | `Plugins/structure-cuts`, `Plugins/qs-formwork` |

`split_wall` is not a shipped tool; `AdapterRevitBridge.SplitWallAsync` stays a stub. L3 tools default off. Each description carries its pyRevit or Dynamo alias so the model can match a familiar name.
