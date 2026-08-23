using System.IO;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using RevitAiPoc.Llm;
using RevitAiPoc.Revit;

namespace RevitAiPoc.Tools.Compat;

public static class IfcCostTools
{
    public static IEnumerable<ToolEntry> Create(RevitBridge bridge, QueryCache cache)
    {
        yield return CreateExportIfc(bridge);
        yield return CreateQuantityTakeoff(bridge);
    }

    private static ToolEntry CreateExportIfc(RevitBridge bridge) => new()
    {
        Tier=ToolTier.L1,
        Definition=new ToolDefinition{ Function=new ToolFunction{ Name="export_ifc", Description="Export IFC to folder (BIM Interoperability). Params: folder, name optional.", Parameters=new{type="object",properties=new{folder=new{type="string"},name=new{type="string"}},required=Array.Empty<string>()}}},
        Handler=async json=>{
            string? folder=null, name=null; try{using var d=JsonDocument.Parse(json); if(d.RootElement.TryGetProperty("folder",out var f)&&f.ValueKind==JsonValueKind.String) folder=f.GetString(); if(d.RootElement.TryGetProperty("name",out var n)&&n.ValueKind==JsonValueKind.String) name=n.GetString();}catch{}
            var capFolder=folder; var capName=name;
            return await bridge.RunAsync(uiApp=>{
                var doc=uiApp.ActiveUIDocument?.Document; if(doc==null) return "Please open a model first.";
                try{
                    var dir=string.IsNullOrWhiteSpace(capFolder)? Path.GetTempPath() : capFolder;
                    Directory.CreateDirectory(dir);
                    var fname=string.IsNullOrWhiteSpace(capName)? doc.Title : capName;
                    // Use built-in IFC export options if available
                    var opts=new IFCExportOptions();
                    // doc.Export(dir, fname, opts); // may need transaction? No
                    return $"Ready to export IFC \"{fname}\" to {dir} (POC: doc.Export with IFCExportOptions). Use IFCExportOptions for config.";
                }catch(Exception ex){return $"Failed: {ex.Message}";}
            });
        }
    };

    private static ToolEntry CreateQuantityTakeoff(RevitBridge bridge) => new()
    {
        Tier=ToolTier.L1,
        Definition=new ToolDefinition{ Function=new ToolFunction{ Name="quantity_takeoff_by_category", Description="Quantity takeoff by category: count, total area, volume, length aggregated to CSV (Dynamo: Quantity Takeoff). Params: category, filePath optional.", Parameters=new{type="object",properties=new{category=new{type="string"},filePath=new{type="string"}},required=new[]{"category"}}}},
        Handler=async json=>{
            string cat=""; string? fp=null; try{using var d=JsonDocument.Parse(json); cat=d.RootElement.GetProperty("category").GetString()??""; if(d.RootElement.TryGetProperty("filePath",out var f)&&f.ValueKind==JsonValueKind.String) fp=f.GetString();}catch(Exception ex){return $"Invalid args: {ex.Message}";}
            return await bridge.RunAsync(uiApp=>{
                var doc=uiApp.ActiveUIDocument?.Document; if(doc==null) return "Please open a model first.";
                try{
                    var bic=AllTools.ParseCategory(cat); if(bic==null) return $"Unknown category {cat}";
                    var elems=new FilteredElementCollector(doc).OfCategory(bic.Value).WhereElementIsNotElementType().ToElements().ToList();
                    double totalArea=0,totalVol=0,totalLen=0;
                    var sb=new StringBuilder();
                    sb.AppendLine("Id,Name,Level,AreaSqft,VolumeCuft,LengthFt");
                    foreach(var el in elems){
                        double a=el.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble()?? 0;
                        double v=el.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED)?.AsDouble()??0;
                        double l=el.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)?.AsDouble()??0;
                        totalArea+=a; totalVol+=v; totalLen+=l;
                        var lvl=doc.GetElement(el.LevelId) as Level;
                        sb.AppendLine($"{el.Id},\"{el.Name}\",\"{lvl?.Name}\",{a:F2},{v:F2},{l:F2}");
                    }
                    string path=fp?? Path.Combine(Path.GetTempPath(), $"takeoff_{cat}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
                    File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
                    return $"{cat} takeoff: {elems.Count} instances Area {totalArea:F2} sqft Vol {totalVol:F2} cuft Len {totalLen:F2} ft\nCSV: {path}\nPreview:\n"+string.Join("\n", sb.ToString().Split('\n').Take(6));
                }catch(Exception ex){return $"Failed: {ex.Message}";}
            });
        }
    };
}
