using System.IO;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitAiPoc.Llm;
using RevitAiPoc.Revit;

namespace RevitAiPoc.Tools.Compat;

public static class ParamExcelTools
{
    public static IEnumerable<ToolEntry> Create(RevitBridge bridge, QueryCache cache)
    {
        yield return CreateExportParamsToExcel(bridge);
        yield return CreateImportExcelToParams(bridge);
        yield return CreateCopyParams(bridge);
        yield return CreateReplaceText(bridge);
        yield return CreateNumberingAuto(bridge);
    }

    private static ToolEntry CreateExportParamsToExcel(RevitBridge bridge) => new()
    {
        Tier = ToolTier.L1,
        Definition = new ToolDefinition{ Function = new ToolFunction{ Name="export_params_to_excel", Description="Export element parameters to CSV (Excel compatible) — pyRevit: Export/DiRoots. Params: category, paramNames (comma), filePath optional, limit.", Parameters=new{type="object",properties=new{category=new{type="string"},paramNames=new{type="string",description="Comma separated e.g. Comments,Mark,Level"},filePath=new{type="string"},limit=new{type="integer"}},required=new[]{"category"}}}},
        Handler = async json=>{
            string cat=""; string? paramNames=null; string? filePath=null; int lim=200;
            try{using var d=JsonDocument.Parse(json); cat=d.RootElement.GetProperty("category").GetString()??""; if(d.RootElement.TryGetProperty("paramNames",out var pn)&&pn.ValueKind==JsonValueKind.String) paramNames=pn.GetString(); if(d.RootElement.TryGetProperty("filePath",out var fp)&&fp.ValueKind==JsonValueKind.String) filePath=fp.GetString(); if(d.RootElement.TryGetProperty("limit",out var l)&&l.ValueKind==JsonValueKind.Number) lim=l.GetInt32();}catch(Exception ex){return $"Invalid args: {ex.Message}";}
            lim=Math.Clamp(lim,1,1000);
            var res=await bridge.RunAsync(uiApp=>{
                var doc=uiApp.ActiveUIDocument?.Document; if(doc==null) return "Please open a model first.";
                try{
                    var bic=AllTools.ParseCategory(cat); if(bic==null) return $"Unknown category {cat}";
                    var elems=new FilteredElementCollector(doc).OfCategory(bic.Value).WhereElementIsNotElementType().ToElements().Take(lim).ToList();
                    string[] cols=string.IsNullOrWhiteSpace(paramNames)? new[]{"Comments","Mark"} : paramNames.Split(',', StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
                    var path=ExportPaths.ResolveWrite(filePath, $"export_{cat}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
                    var sb=new StringBuilder();
                    sb.AppendLine("Id,"+string.Join(",", cols));
                    foreach(var el in elems){
                        var vals=new List<string>{el.Id.ToString()};
                        foreach(var c in cols){
                            var p=el.LookupParameter(c); string v="";
                            if(p!=null) v= p.StorageType==StorageType.String? (p.AsString()??"") : p.AsValueString()?? p.AsString()?? "";
                            vals.Add($"\"{v.Replace("\"","\"\"")}\"");
                        }
                        sb.AppendLine(string.Join(",", vals));
                    }
                    File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
                    return $"Exported {elems.Count} {cat} to {path}\nColumns: {string.Join(", ", cols)}\nFirst 3:\n"+string.Join("\n", sb.ToString().Split('\n').Take(4));
                }catch(Exception ex){return $"Failed: {ex.Message}";}
            });
            return res;
        }
    };

    private static ToolEntry CreateImportExcelToParams(RevitBridge bridge) => new()
    {
        Tier = ToolTier.L3,
        RequiresWriteConfirm=true,
        Definition = new ToolDefinition{ Function = new ToolFunction{ Name="import_excel_to_params", Description="Import CSV (Excel) to set parameters by Id — pyRevit: Import. Params: filePath, paramName.", Parameters=new{type="object",properties=new{filePath=new{type="string"},paramName=new{type="string"}},required=new[]{"filePath","paramName"}}}},
        Handler = async json=>{
            string fp="", pn=""; try{using var d=JsonDocument.Parse(json); fp=d.RootElement.GetProperty("filePath").GetString()??""; pn=d.RootElement.GetProperty("paramName").GetString()??"";}catch(Exception ex){return $"Invalid args: {ex.Message}";}
            try { fp = ExportPaths.ResolveRead(fp); } catch (Exception ex) { return ex.Message; }
            if(!File.Exists(fp)) return $"File not found: {fp}";
            var lines=File.ReadAllLines(fp);
            if(lines.Length<2) return "File empty or no data rows";
            var header=lines[0].Split(','); int idIdx=0; int valIdx=1;
            // find param column if header contains param name
            for(int i=0;i<header.Length;i++) if(header[i].Trim('\"').Equals(pn,StringComparison.OrdinalIgnoreCase)) valIdx=i;
            var rows=new List<(string id,string val)>();
            for(int i=1;i<lines.Length;i++){
                var parts=SplitCsv(lines[i]); if(parts.Length<2) continue; rows.Add((parts[idIdx].Trim('\"'), parts[valIdx].Trim('\"')));
            }
            if(rows.Count==0) return "No rows parsed";
            bool ok=await bridge.RunAsync(uiApp=>{
                var td=new TaskDialog("Revit AI POC"){MainInstruction=$"Import {pn} to {rows.Count} elements from {Path.GetFileName(fp)}?", MainContent=string.Join("\n", rows.Take(5).Select(r=>$"{r.id} -> {r.val}"))+(rows.Count>5?"\n...":""), CommonButtons=TaskDialogCommonButtons.Yes|TaskDialogCommonButtons.No, DefaultButton=TaskDialogResult.No};
                return td.Show()==TaskDialogResult.Yes;
            });
            if(!ok) return "Cancelled by user.";
            var res=await bridge.RunAsync(uiApp=>{
                var doc=uiApp.ActiveUIDocument?.Document; if(doc==null) return "Please open a model first.";
                int upd=0,skip=0;
                using var tx=new Transaction(doc, $"Revit AI POC: import {pn}");
                tx.Start();
                try{
                    foreach(var (id,val) in rows){
                        try{ var el=doc.GetElement(AllTools.ParseEid(id)); if(el==null){skip++;continue;} if(!AllTools.TrySetParameter(el,pn,val,out _)){skip++;continue;} upd++; }catch{skip++;}
                    }
                    tx.Commit();
                }catch{ try{tx.RollBack();}catch{} throw;}
                return $"Imported {upd}/{rows.Count} ({skip} skipped) {pn} from {fp}";
            });
            return res;
        }
    };

    private static string[] SplitCsv(string line){
        var res=new List<string>(); var cur=new StringBuilder(); bool inQ=false;
        foreach(var ch in line){
            if(ch=='\"') inQ=!inQ;
            else if(ch==',' && !inQ){res.Add(cur.ToString()); cur.Clear();}
            else cur.Append(ch);
        }
        res.Add(cur.ToString()); return res.ToArray();
    }

    private static ToolEntry CreateCopyParams(RevitBridge bridge) => new()
    {
        Tier = ToolTier.L2,
        RequiresWriteConfirm=true,
        Definition = new ToolDefinition{ Function = new ToolFunction{ Name="copy_params", Description="Copy parameter value from source param to target param for a category (pyRevit: Copy Param). Params: category, sourceParam, targetParam, limit.", Parameters=new{type="object",properties=new{category=new{type="string"},sourceParam=new{type="string"},targetParam=new{type="string"},limit=new{type="integer"}},required=new[]{"category","sourceParam","targetParam"}}}},
        Handler = async json=>{
            string cat="",src="",tgt=""; int lim=200; try{using var d=JsonDocument.Parse(json); cat=d.RootElement.GetProperty("category").GetString()??""; src=d.RootElement.GetProperty("sourceParam").GetString()??""; tgt=d.RootElement.GetProperty("targetParam").GetString()??""; if(d.RootElement.TryGetProperty("limit",out var l)&&l.ValueKind==JsonValueKind.Number) lim=l.GetInt32();}catch(Exception ex){return $"Invalid args: {ex.Message}";}
            lim=Math.Clamp(lim,1,1000);
            bool ok=await bridge.RunAsync(uiApp=>{
                var td=new TaskDialog("Revit AI POC"){MainInstruction=$"Copy {src} -> {tgt} for {lim} {cat}?", CommonButtons=TaskDialogCommonButtons.Yes|TaskDialogCommonButtons.No, DefaultButton=TaskDialogResult.No}; return td.Show()==TaskDialogResult.Yes;
            });
            if(!ok) return "Cancelled by user.";
            return await bridge.RunAsync(uiApp=>{
                var doc=uiApp.ActiveUIDocument?.Document; if(doc==null) return "Please open a model first.";
                try{
                    var bic=AllTools.ParseCategory(cat); if(bic==null) return $"Unknown category {cat}";
                    var elems=new FilteredElementCollector(doc).OfCategory(bic.Value).WhereElementIsNotElementType().ToElements().Take(lim).ToList();
                    int upd=0; using var tx=new Transaction(doc, $"Revit AI POC: copy {src}->{tgt}"); tx.Start();
                    try{
                        foreach(var el in elems){
                            var pSrc=el.LookupParameter(src); if(pSrc==null) continue;
                            string v=pSrc.StorageType==StorageType.String? (pSrc.AsString()??"") : pSrc.AsValueString()??"";
                            if(AllTools.TrySetParameter(el,tgt,v,out _)) upd++;
                        }
                        tx.Commit();
                    }catch{ try{tx.RollBack();}catch{} throw;}
                    return $"Copied {upd}/{elems.Count} {src}->{tgt} for {cat}";
                }catch(Exception ex){return $"Failed: {ex.Message}";}
            });
        }
    };

    private static ToolEntry CreateReplaceText(RevitBridge bridge) => new()
    {
        Tier = ToolTier.L2,
        RequiresWriteConfirm=true,
        Definition = new ToolDefinition{ Function = new ToolFunction{ Name="replace_text", Description="Regex replace in parameter (pyRevit: Replace). Params: category, paramName, pattern, replacement, limit.", Parameters=new{type="object",properties=new{category=new{type="string"},paramName=new{type="string"},pattern=new{type="string"},replacement=new{type="string"},limit=new{type="integer"}},required=new[]{"category","paramName","pattern","replacement"}}}},
        Handler = async json=>{
            string cat="",pn="",pat="",rep=""; int lim=200; try{using var d=JsonDocument.Parse(json); cat=d.RootElement.GetProperty("category").GetString()??""; pn=d.RootElement.GetProperty("paramName").GetString()??""; pat=d.RootElement.GetProperty("pattern").GetString()??""; rep=d.RootElement.GetProperty("replacement").GetString()??""; if(d.RootElement.TryGetProperty("limit",out var l)&&l.ValueKind==JsonValueKind.Number) lim=l.GetInt32();}catch(Exception ex){return $"Invalid args: {ex.Message}";}
            lim=Math.Clamp(lim,1,1000);
            bool ok=await bridge.RunAsync(uiApp=>{
                var td=new TaskDialog("Revit AI POC"){MainInstruction=$"Replace /{pat}/ -> \"{rep}\" in {pn} for {cat}?", CommonButtons=TaskDialogCommonButtons.Yes|TaskDialogCommonButtons.No, DefaultButton=TaskDialogResult.No}; return td.Show()==TaskDialogResult.Yes;
            });
            if(!ok) return "Cancelled by user.";
            return await bridge.RunAsync(uiApp=>{
                var doc=uiApp.ActiveUIDocument?.Document; if(doc==null) return "Please open a model first.";
                try{
                    var bic=AllTools.ParseCategory(cat); if(bic==null) return $"Unknown category {cat}";
                    var elems=new FilteredElementCollector(doc).OfCategory(bic.Value).WhereElementIsNotElementType().ToElements().Take(lim).ToList();
                    int upd=0; var re=new System.Text.RegularExpressions.Regex(pat);
                    using var tx=new Transaction(doc, $"Revit AI POC: replace {pn}"); tx.Start();
                    try{
                        foreach(var el in elems){
                            var p=el.LookupParameter(pn); if(p==null||p.StorageType!=StorageType.String) continue;
                            var cur=p.AsString()??""; var nxt=re.Replace(cur,rep); if(cur==nxt) continue;
                            if(AllTools.TrySetParameter(el,pn,nxt,out _)) upd++;
                        }
                        tx.Commit();
                    }catch{ try{tx.RollBack();}catch{} throw;}
                    return $"Replaced {upd}/{elems.Count} {pn} /{pat}/ -> {rep}";
                }catch(Exception ex){return $"Failed: {ex.Message}";}
            });
        }
    };

    private static ToolEntry CreateNumberingAuto(RevitBridge bridge) => new()
    {
        Tier = ToolTier.L2,
        RequiresWriteConfirm=true,
        Definition = new ToolDefinition{ Function = new ToolFunction{ Name="numbering_auto", Description="Auto number Mark by selection order or category (pyRevit: Auto Number). Params: category or use selection if omitted, prefix, start, pad.", Parameters=new{type="object",properties=new{category=new{type="string"},prefix=new{type="string"},start=new{type="integer"},pad=new{type="integer"}},required=Array.Empty<string>()}}},
        Handler = async json=>{
            string? cat=null; string pref=""; int start=1,pad=3;
            try{using var d=JsonDocument.Parse(json); if(d.RootElement.TryGetProperty("category",out var c)&&c.ValueKind==JsonValueKind.String) cat=c.GetString(); if(d.RootElement.TryGetProperty("prefix",out var p)&&p.ValueKind==JsonValueKind.String) pref=p.GetString()??""; if(d.RootElement.TryGetProperty("start",out var s)&&s.ValueKind==JsonValueKind.Number) start=s.GetInt32(); if(d.RootElement.TryGetProperty("pad",out var pa)&&pa.ValueKind==JsonValueKind.Number) pad=pa.GetInt32();}catch{}
            var cap=cat;
            bool ok=await bridge.RunAsync(uiApp=>{
                string msg=string.IsNullOrWhiteSpace(cap)? "selection" : cap;
                var td=new TaskDialog("Revit AI POC"){MainInstruction=$"Auto number Mark for {msg} start {pref}{start.ToString().PadLeft(pad,'0')}?", CommonButtons=TaskDialogCommonButtons.Yes|TaskDialogCommonButtons.No, DefaultButton=TaskDialogResult.No}; return td.Show()==TaskDialogResult.Yes;
            });
            if(!ok) return "Cancelled by user.";
            return await bridge.RunAsync(uiApp=>{
                var uidoc=uiApp.ActiveUIDocument; if(uidoc==null) return "Please open a model first.";
                var doc=uidoc.Document;
                try{
                    ICollection<ElementId> ids;
                    if(!string.IsNullOrWhiteSpace(cap)){
                        var bic=AllTools.ParseCategory(cap); if(bic==null) return $"Unknown category {cap}";
                        ids=new FilteredElementCollector(doc).OfCategory(bic.Value).WhereElementIsNotElementType().ToElementIds();
                    } else ids=uidoc.Selection.GetElementIds();
                    if(ids.Count==0) return "Please select elements first or provide category.";
                    int n=start; int upd=0;
                    using var tx=new Transaction(doc, "Revit AI POC: auto numbering"); tx.Start();
                    try{
                        foreach(var id in ids){
                            var el=doc.GetElement(id); if(el==null) continue;
                            var mark=pref+n.ToString().PadLeft(pad,'0');
                            if(AllTools.TrySetParameter(el,"Mark",mark,out _)) upd++;
                            n++;
                        }
                        tx.Commit();
                    }catch{ try{tx.RollBack();}catch{} throw;}
                    return $"Numbered {upd}/{ids.Count} Mark from {pref}{start.ToString().PadLeft(pad,'0')}";
                }catch(Exception ex){return $"Failed: {ex.Message}";}
            });
        }
    };
}
