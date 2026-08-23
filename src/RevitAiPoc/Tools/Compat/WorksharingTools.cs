using System.Text.Json;
using Autodesk.Revit.DB;
using RevitAiPoc.Llm;
using RevitAiPoc.Revit;

namespace RevitAiPoc.Tools.Compat;

public static class WorksharingTools
{
    public static IEnumerable<ToolEntry> Create(RevitBridge bridge, QueryCache cache)
    {
        yield return CreateReloadLinks(bridge);
        yield return CreateWorksetAssign(bridge);
        yield return CreateSyncInfo(bridge);
        yield return CreateAuditWarningsFix(bridge);
    }

    private static ToolEntry CreateReloadLinks(RevitBridge bridge) => new()
    {
        Tier=ToolTier.L2,
        Definition=new ToolDefinition{ Function=new ToolFunction{ Name="reload_links", Description="Reload all RVT links (pyRevit: Reload Links). No params.", Parameters=new{type="object",properties=new{},required=Array.Empty<string>()}}},
        Handler=async _ => await bridge.RunAsync(uiApp=>{
            var doc=uiApp.ActiveUIDocument?.Document; if(doc==null) return "Please open a model first.";
            try{
                var links=new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>().ToList();
                int ok=0;
                using var tx=new Transaction(doc, "Revit AI POC: reload links"); tx.Start();
                foreach(var lt in links){ try{ lt.Reload(); ok++; }catch{} }
                tx.Commit();
                return $"Reloaded {ok}/{links.Count} RVT links";
            }catch(Exception ex){return $"Failed: {ex.Message}";}
        })
    };

    private static ToolEntry CreateWorksetAssign(RevitBridge bridge) => new()
    {
        Tier=ToolTier.L2,
        Definition=new ToolDefinition{ Function=new ToolFunction{ Name="workset_assign", Description="Assign elements to workset (BIMOne). Params: elementIds, worksetName.", Parameters=new{type="object",properties=new{elementIds=new{type="array",items=new{type="string"}},worksetName=new{type="string"}},required=new[]{"elementIds","worksetName"}}}},
        Handler=async json=>{
            List<string> ids=new(); string ws=""; try{using var d=JsonDocument.Parse(json); ids=d.RootElement.GetProperty("elementIds").EnumerateArray().Where(v=>v.ValueKind==JsonValueKind.String).Select(v=>v.GetString()!).ToList(); ws=d.RootElement.GetProperty("worksetName").GetString()??"";}catch(Exception ex){return $"Invalid args: {ex.Message}";}
            return await bridge.RunAsync(uiApp=>{
                var doc=uiApp.ActiveUIDocument?.Document; if(doc==null) return "Please open a model first.";
                if(!doc.IsWorkshared) return "Document is not workshared.";
                try{
                    var w=new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToWorksets().FirstOrDefault(x=>x.Name.Equals(ws,StringComparison.OrdinalIgnoreCase));
                    if(w==null) return $"Workset {ws} not found.";
                    using var tx=new Transaction(doc, $"Revit AI POC: workset {ws}"); tx.Start();
                    int upd=0;
                    foreach(var idStr in ids){
                        var el=doc.GetElement(AllTools.ParseEid(idStr)); if(el==null) continue;
                        var param=el.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM); if(param!=null && !param.IsReadOnly){ param.Set(w.Id.IntegerValue); upd++; }
                    }
                    tx.Commit();
                    return $"Assigned {upd}/{ids.Count} to workset \"{ws}\"";
                }catch(Exception ex){return $"Failed: {ex.Message}";}
            });
        }
    };

    private static ToolEntry CreateSyncInfo(RevitBridge bridge) => new()
    {
        Tier=ToolTier.L1,
        Definition=new ToolDefinition{ Function=new ToolFunction{ Name="sync_info", Description="Get worksharing central and sync status (pyRevit). No params.", Parameters=new{type="object",properties=new{},required=Array.Empty<string>()}}},
        Handler=async _ => await bridge.RunAsync(uiApp=>{
            var doc=uiApp.ActiveUIDocument?.Document; if(doc==null) return "Please open a model first.";
            try{
                var isWS=doc.IsWorkshared;
                var central=isWS? doc.GetWorksharingCentralModelPath()?.ToString()??"(unknown)" : "(not workshared)";
                return $"Workshared: {isWS}\nCentral: {central}\nTitle: {doc.Title}\nPath: {doc.PathName}";
            }catch(Exception ex){return $"Failed: {ex.Message}";}
        })
    };

    private static ToolEntry CreateAuditWarningsFix(RevitBridge bridge) => new()
    {
        Tier=ToolTier.L1,
        Definition=new ToolDefinition{ Function=new ToolFunction{ Name="audit_warnings_fix", Description="List warnings with suggested fix (pyRevit: Audit). Params: limit.", Parameters=new{type="object",properties=new{limit=new{type="integer"}},required=Array.Empty<string>()}}},
        Handler=async json=>{
            int lim=10; try{using var d=JsonDocument.Parse(json); if(d.RootElement.TryGetProperty("limit",out var l)&&l.ValueKind==JsonValueKind.Number) lim=l.GetInt32();}catch{}
            lim=Math.Clamp(lim,1,50);
            return await bridge.RunAsync(uiApp=>{
                var doc=uiApp.ActiveUIDocument?.Document; if(doc==null) return "Please open a model first.";
                try{
                    var warns=doc.GetWarnings().Take(lim).ToList();
                    if(warns.Count==0) return "No warnings.";
                    var s=$"Warnings {warns.Count} (top {lim}):\n";
                    int i=0; foreach(var w in warns){
                        s+=$"{++i}. {w.GetDescriptionText()} - Suggest: check {string.Join(",", w.GetFailingElements().Take(2))}\n";
                    }
                    return s;
                }catch(Exception ex){return $"Failed: {ex.Message}";}
            });
        }
    };
}
