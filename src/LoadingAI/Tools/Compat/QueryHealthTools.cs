using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using LoadingAI.Llm;
using LoadingAI.Revit;
using LoadingAI.Plugins;
using Autodesk.Revit.UI;

namespace LoadingAI.Tools.Compat;

public static class QueryHealthTools
{
    public static IEnumerable<ToolEntry> Create(IPluginContext context)
    {
        yield return CreateGetWorksets(context);
        yield return CreateGetPhases(context);
        yield return CreateGetDesignOptions(context);
        yield return CreateGetSheetsViews(context);
        yield return CreateGetFamilyTypes(context);
        yield return CreateGetMaterials(context);
        yield return CreateGetRoomArea(context);
        yield return CreateGetClashByBB(context);
        yield return CreateGetModelHealthScore(context);
    }

    private static ToolEntry CreateGetWorksets(IPluginContext context) => new()
    {
        Tier = ToolTier.L1,
        Definition = new ToolDefinition{ Function = new ToolFunction{ Name="get_worksets", Description="List worksets (pyRevit: Worksets). No params. Compatible with worksharing models.", Parameters=new{type="object",properties=new{},required=Array.Empty<string>()}}},
        Handler = async _ => await context.FullAccess!.RunRevitAsync(session=>{
            var uiApp = session.As<UIApplication>();
            var doc=uiApp.ActiveUIDocument?.Document; if(doc==null) return "Please open a model first.";
            try{
                var tbl=new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToWorksets();
                var s=$"Worksets ({tbl.Count}):\n"+string.Join("\n",tbl.Select(w=>$"  {w.Id.IntegerValue} \"{w.Name}\" Open:{w.IsOpen} Vis:{w.IsVisibleByDefault}"));
                if(doc.IsWorkshared==false) s="(Not workshared) "+s;
                return s;
            }catch(Exception ex){return $"Failed: {ex.Message}";}
        })
    };

    private static ToolEntry CreateGetPhases(IPluginContext context) => new()
    {
        Tier = ToolTier.L1,
        Definition = new ToolDefinition{ Function = new ToolFunction{ Name="get_phases", Description="List phases (pyRevit: Phases). No params.", Parameters=new{type="object",properties=new{},required=Array.Empty<string>()}}},
        Handler = async _ => await context.FullAccess!.RunRevitAsync(session=>{
            var uiApp = session.As<UIApplication>();
            var doc=uiApp.ActiveUIDocument?.Document; if(doc==null) return "Please open a model first.";
            try{
                var phases=doc.Phases.Cast<Phase>().ToList();
                return $"Phases ({phases.Count}):\n"+string.Join("\n",phases.Select(p=>$"  {p.Id} \"{p.Name}\""));
            }catch(Exception ex){return $"Failed: {ex.Message}";}
        })
    };

    private static ToolEntry CreateGetDesignOptions(IPluginContext context) => new()
    {
        Tier = ToolTier.L1,
        Definition = new ToolDefinition{ Function = new ToolFunction{ Name="get_design_options", Description="List design options and option sets (Dynamo: DesignOptions). No params.", Parameters=new{type="object",properties=new{},required=Array.Empty<string>()}}},
        Handler = async _ => await context.FullAccess!.RunRevitAsync(session=>{
            var uiApp = session.As<UIApplication>();
            var doc=uiApp.ActiveUIDocument?.Document; if(doc==null) return "Please open a model first.";
            try{
                var sets=new FilteredElementCollector(doc).OfClass(typeof(DesignOption)).Cast<DesignOption>().ToList();
                return $"DesignOptions ({sets.Count}):\n"+string.Join("\n",sets.Select(o=>$"  {o.Id} \"{o.Name}\" Primary:{o.IsPrimary}"));
            }catch(Exception ex){return $"Failed: {ex.Message}";}
        })
    };

    private static ToolEntry CreateGetSheetsViews(IPluginContext context) => new()
    {
        Tier = ToolTier.L1,
        Definition = new ToolDefinition{ Function = new ToolFunction{ Name="get_sheets_views", Description="List sheets and views counts by type (DiRoots SheetLink). No params.", Parameters=new{type="object",properties=new{},required=Array.Empty<string>()}}},
        Handler = async _ => await context.FullAccess!.RunRevitAsync(session=>{
            var uiApp = session.As<UIApplication>();
            var doc=uiApp.ActiveUIDocument?.Document; if(doc==null) return "Please open a model first.";
            try{
                var sheets=new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().ToList();
                var views=new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v=>!v.IsTemplate).ToList();
                var byType=views.GroupBy(v=>v.ViewType.ToString()).Select(g=>$"  {g.Key}: {g.Count()}");
                return $"Sheets: {sheets.Count}\n"+string.Join("\n",sheets.Take(10).Select(s=>$"  {s.Id} \"{s.Name}\" - {s.SheetNumber}"))+(sheets.Count>10?"\n...":"")+$"\nViews: {views.Count}\n"+string.Join("\n",byType);
            }catch(Exception ex){return $"Failed: {ex.Message}";}
        })
    };

    private static ToolEntry CreateGetFamilyTypes(IPluginContext context) => new()
    {
        Tier = ToolTier.L1,
        Definition = new ToolDefinition{ Function = new ToolFunction{ Name="get_family_types", Description="List family symbols by category (Dynamo: archi-lab Family Types). Params: category optional.", Parameters=new{type="object",properties=new{category=new{type="string",description="Category e.g. Doors, Windows"}},required=Array.Empty<string>()}}},
        Handler = async json=>{
            string? cat=null; try{using var d=JsonDocument.Parse(json); if(d.RootElement.TryGetProperty("category",out var c)&&c.ValueKind==JsonValueKind.String) cat=c.GetString();}catch{}
            var cap=cat;
            return await context.FullAccess!.RunRevitAsync(session=>{
                var uiApp = session.As<UIApplication>();
                var doc=uiApp.ActiveUIDocument?.Document; if(doc==null) return "Please open a model first.";
                try{
                    var syms=new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().ToList();
                    if(!string.IsNullOrWhiteSpace(cap)){
                        // filter by family category
                        syms=syms.Where(s=>s.Category?.Name.Equals(cap,StringComparison.OrdinalIgnoreCase)==true || s.FamilyName.IndexOf(cap,StringComparison.OrdinalIgnoreCase)>=0).ToList();
                    }
                    return $"FamilySymbols ({syms.Count}):\n"+string.Join("\n",syms.Take(20).Select(s=>$"  {s.Id} \"{s.FamilyName}:{s.Name}\" Cat:{s.Category?.Name}"))+(syms.Count>20?"\n...":"");
                }catch(Exception ex){return $"Failed: {ex.Message}";}
            });
        }
    };

    private static ToolEntry CreateGetMaterials(IPluginContext context) => new()
    {
        Tier = ToolTier.L1,
        Definition = new ToolDefinition{ Function = new ToolFunction{ Name="get_materials", Description="List materials in model (BIMOne). No params.", Parameters=new{type="object",properties=new{},required=Array.Empty<string>()}}},
        Handler = async _ => await context.FullAccess!.RunRevitAsync(session=>{
            var uiApp = session.As<UIApplication>();
            var doc=uiApp.ActiveUIDocument?.Document; if(doc==null) return "Please open a model first.";
            try{
                var mats=new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().ToList();
                return $"Materials ({mats.Count}):\n"+string.Join("\n",mats.Take(20).Select(m=>$"  {m.Id} \"{m.Name}\" Class:{m.MaterialClass}"))+(mats.Count>20?"\n...":"");
            }catch(Exception ex){return $"Failed: {ex.Message}";}
        })
    };

    private static ToolEntry CreateGetRoomArea(IPluginContext context) => new()
    {
        Tier = ToolTier.L1,
        Definition = new ToolDefinition{ Function = new ToolFunction{ Name="get_room_area", Description="Sum room area by level (Dynamo: Room.Area). Optional levelName.", Parameters=new{type="object",properties=new{levelName=new{type="string",description="Level filter"}},required=Array.Empty<string>()}}},
        Handler = async json=>{
            string? lvl=null; try{using var d=JsonDocument.Parse(json); if(d.RootElement.TryGetProperty("levelName",out var v)&&v.ValueKind==JsonValueKind.String) lvl=v.GetString();}catch{}
            var cap=lvl;
            return await context.FullAccess!.RunRevitAsync(session=>{
                var uiApp = session.As<UIApplication>();
                var doc=uiApp.ActiveUIDocument?.Document; if(doc==null) return "Please open a model first.";
                try{
                    var rooms=new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().Cast<Room>().ToList();
                    if(!string.IsNullOrWhiteSpace(cap)) rooms=rooms.Where(r=>doc.GetElement(r.LevelId) is Level l && l.Name.Equals(cap,StringComparison.OrdinalIgnoreCase)).ToList();
                    double total=rooms.Sum(r=> r.get_Parameter(BuiltInParameter.ROOM_AREA)?.AsDouble()??0);
                    return $"Rooms: {rooms.Count} Total area: {total:F2} sqft ({total*0.092903:F2} m2)" + (rooms.Count>0?"\n"+string.Join("\n",rooms.Take(10).Select(r=>$"  {r.Id} \"{r.Name}\" Level:{(doc.GetElement(r.LevelId) as Level)?.Name} Area:{(r.get_Parameter(BuiltInParameter.ROOM_AREA)?.AsDouble()??0):F1} sqft")):"");
                }catch(Exception ex){return $"Failed: {ex.Message}";}
            });
        }
    };

    private static ToolEntry CreateGetClashByBB(IPluginContext context) => new()
    {
        Tier = ToolTier.L1,
        Definition = new ToolDefinition{ Function = new ToolFunction{ Name="get_clash_by_bb", Description="Find clashing element pairs by bounding box overlap (Navis clash). Params: category1, category2, limit.", Parameters=new{type="object",properties=new{category1=new{type="string"},category2=new{type="string"},limit=new{type="integer"}},required=new[]{"category1","category2"}}}},
        Handler = async json=>{
            string c1="",c2=""; int lim=20; try{using var d=JsonDocument.Parse(json); c1=d.RootElement.GetProperty("category1").GetString()??""; c2=d.RootElement.GetProperty("category2").GetString()??""; if(d.RootElement.TryGetProperty("limit",out var l)&&l.ValueKind==JsonValueKind.Number) lim=l.GetInt32();}catch(Exception ex){return $"Invalid args: {ex.Message}";}
            var cap1=c1; var cap2=c2; lim=Polyfills.Clamp(lim,1,100);
            return await context.FullAccess!.RunRevitAsync(session=>{
                var uiApp = session.As<UIApplication>();
                var doc=uiApp.ActiveUIDocument?.Document; if(doc==null) return "Please open a model first.";
                try{
                    var bic1=AllTools.ParseCategory(cap1); var bic2=AllTools.ParseCategory(cap2);
                    if(bic1==null||bic2==null) return $"Unknown categories {cap1}/{cap2}";
                    var a=new FilteredElementCollector(doc).OfCategory(bic1.Value).WhereElementIsNotElementType().ToElements().Take(200).ToList();
                    var b=new FilteredElementCollector(doc).OfCategory(bic2.Value).WhereElementIsNotElementType().ToElements().Take(200).ToList();
                    var clashes=new List<string>();
                    foreach(var elA in a){
                        var bbA=elA.get_BoundingBox(null); if(bbA==null) continue;
                        foreach(var elB in b){
                            var bbB=elB.get_BoundingBox(null); if(bbB==null) continue;
                            if(bbA.Max.X < bbB.Min.X || bbA.Min.X > bbB.Max.X || bbA.Max.Y < bbB.Min.Y || bbA.Min.Y > bbB.Max.Y || bbA.Max.Z < bbB.Min.Z || bbA.Min.Z > bbB.Max.Z) continue;
                            clashes.Add($"{elA.Id}({elA.Category?.Name}) <-> {elB.Id}({elB.Category?.Name})");
                            if(clashes.Count>=lim) break;
                        }
                        if(clashes.Count>=lim) break;
                    }
                    return clashes.Count==0? $"No clashes found between {cap1} and {cap2} (sampled 200 each)." : $"Clashes ({clashes.Count}):\n"+string.Join("\n",clashes);
                }catch(Exception ex){return $"Failed: {ex.Message}";}
            });
        }
    };

    private static ToolEntry CreateGetModelHealthScore(IPluginContext context) => new()
    {
        Tier = ToolTier.L1,
        Definition = new ToolDefinition{ Function = new ToolFunction{ Name="get_model_health_score", Description="Overall health score: warnings, views, worksets, rooms. No params. (pyRevit Health)", Parameters=new{type="object",properties=new{},required=Array.Empty<string>()}}},
        Handler = async _ => await context.FullAccess!.RunRevitAsync(session=>{
            var uiApp = session.As<UIApplication>();
            var doc=uiApp.ActiveUIDocument?.Document; if(doc==null) return "Please open a model first.";
            try{
                var warns=doc.GetWarnings().Count;
                var views=new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Count(v=>!v.IsTemplate);
                var warnScore=Math.Max(0,100-warns*2);
                var viewScore=Math.Min(20, views/20);
                var total= (warnScore*0.7+viewScore*1.5);
                return $"Health score: {total:F0}/100\nWarnings: {warns} (score {warnScore})\nViews: {views}\nWorkshared: {doc.IsWorkshared}\nTitle: {doc.Title}";
            }catch(Exception ex){return $"Failed: {ex.Message}";}
        })
    };
}
