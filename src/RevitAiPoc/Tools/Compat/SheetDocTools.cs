using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitAiPoc.Llm;
using RevitAiPoc.Revit;

namespace RevitAiPoc.Tools.Compat;

public static class SheetDocTools
{
    public static IEnumerable<ToolEntry> Create(RevitBridge bridge, QueryCache cache)
    {
        yield return CreateSheet(bridge);
        yield return PlaceViewOnSheet(bridge);
        yield return DuplicateViewTool(bridge);
        yield return SetViewTemplate(bridge);
        yield return SetCropBox(bridge);
        yield return CreateDimension(bridge);
        yield return TagAll(bridge);
    }

    private static ToolEntry CreateSheet(RevitBridge bridge) => new()
    {
        Tier=ToolTier.L3,
        RequiresWriteConfirm=true,
        Definition=new ToolDefinition{ Function=new ToolFunction{ Name="create_sheet", Description="Create sheet (pyRevit: Create Sheets). Params: sheetNumber, sheetName, titleBlockName optional.", Parameters=new{type="object",properties=new{sheetNumber=new{type="string"},sheetName=new{type="string"},titleBlockName=new{type="string"}},required=new[]{"sheetNumber","sheetName"}}}},
        Handler=async json=>{
            string num="",name="",tb=""; try{using var d=JsonDocument.Parse(json); num=d.RootElement.GetProperty("sheetNumber").GetString()??""; name=d.RootElement.GetProperty("sheetName").GetString()??""; if(d.RootElement.TryGetProperty("titleBlockName",out var t)&&t.ValueKind==JsonValueKind.String) tb=t.GetString()??"";}catch(Exception ex){return $"Invalid args: {ex.Message}";}
            bool ok=await bridge.RunAsync(uiApp=>{
                var td=new TaskDialog("Revit AI POC"){MainInstruction=$"Create sheet {num} \"{name}\"?", CommonButtons=TaskDialogCommonButtons.Yes|TaskDialogCommonButtons.No, DefaultButton=TaskDialogResult.No}; return td.Show()==TaskDialogResult.Yes;
            }); if(!ok) return "Cancelled by user.";
            return await bridge.RunAsync(uiApp=>{
                var doc=uiApp.ActiveUIDocument?.Document; if(doc==null) return "Please open a model first.";
                try{
                    FamilySymbol? tbSym=null;
                    if(!string.IsNullOrWhiteSpace(tb)) tbSym=new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().FirstOrDefault(s=>s.FamilyName.Contains("Title")&&s.Name.Equals(tb,StringComparison.OrdinalIgnoreCase));
                    if(tbSym==null) tbSym=new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().FirstOrDefault(s=>s.Category?.Id.Value==(long)BuiltInCategory.OST_TitleBlocks);
                    if(tbSym==null) return "No title block family found.";
                    if(!tbSym.IsActive) tbSym.Activate();
                    using var tx=new Transaction(doc, $"Revit AI POC: create sheet {num}"); tx.Start();
                    var sheet=ViewSheet.Create(doc, tbSym.Id); sheet.SheetNumber=num; sheet.Name=name;
                    tx.Commit();
                    return $"Created sheet {sheet.Id} {num} \"{name}\"";
                }catch(Exception ex){return $"Failed: {ex.Message}";}
            });
        }
    };

    private static ToolEntry PlaceViewOnSheet(RevitBridge bridge) => new()
    {
        Tier=ToolTier.L3,
        Definition=new ToolDefinition{ Function=new ToolFunction{ Name="place_view_on_sheet", Description="Place view on sheet (pyRevit: Place Views). Params: viewId, sheetId, x,y optional.", Parameters=new{type="object",properties=new{viewId=new{type="string"},sheetId=new{type="string"},x=new{type="number"},y=new{type="number"}},required=new[]{"viewId","sheetId"}}}},
        Handler=async json=>{
            string vid="",sid=""; double x=0,y=0; try{using var d=JsonDocument.Parse(json); vid=d.RootElement.GetProperty("viewId").GetString()??""; sid=d.RootElement.GetProperty("sheetId").GetString()??""; if(d.RootElement.TryGetProperty("x",out var xv)&&xv.ValueKind==JsonValueKind.Number) x=xv.GetDouble(); if(d.RootElement.TryGetProperty("y",out var yv)&&yv.ValueKind==JsonValueKind.Number) y=yv.GetDouble();}catch(Exception ex){return $"Invalid args: {ex.Message}";}
            return await bridge.RunAsync(uiApp=>{
                var doc=uiApp.ActiveUIDocument?.Document; if(doc==null) return "Please open a model first.";
                try{
                    var view=doc.GetElement(AllTools.ParseEid(vid)) as View; var sheet=doc.GetElement(AllTools.ParseEid(sid)) as ViewSheet;
                    if(view==null) return $"View {vid} not found."; if(sheet==null) return $"Sheet {sid} not found.";
                    using var tx=new Transaction(doc, $"Revit AI POC: place {vid} on {sid}"); tx.Start();
                    var pt=new XYZ(x,y,0);
                    var vp=Viewport.Create(doc, sheet.Id, view.Id, pt);
                    tx.Commit();
                    return $"Placed view {vid} on sheet {sid} at ({x},{y}) viewport {vp.Id}";
                }catch(Exception ex){return $"Failed: {ex.Message}";}
            });
        }
    };

    private static ToolEntry DuplicateViewTool(RevitBridge bridge) => new()
    {
        Tier=ToolTier.L2,
        Definition=new ToolDefinition{ Function=new ToolFunction{ Name="duplicate_view_ex", Description="Duplicate view with options (pyRevit: Duplicate). Params: viewId, withDetailing bool.", Parameters=new{type="object",properties=new{viewId=new{type="string"},withDetailing=new{type="boolean"}},required=new[]{"viewId"}}}},
        Handler=async json=>{
            string vid=""; bool det=false; try{using var d=JsonDocument.Parse(json); vid=d.RootElement.GetProperty("viewId").GetString()??""; if(d.RootElement.TryGetProperty("withDetailing",out var w)&&w.ValueKind is JsonValueKind.True or JsonValueKind.False) det=w.GetBoolean();}catch(Exception ex){return $"Invalid args: {ex.Message}";}
            return await bridge.RunAsync(uiApp=>{
                var doc=uiApp.ActiveUIDocument?.Document; if(doc==null) return "Please open a model first.";
                try{
                    var v=doc.GetElement(AllTools.ParseEid(vid)) as View; if(v==null) return $"View {vid} not found.";
                    using var tx=new Transaction(doc, $"Revit AI POC: duplicate view {vid}"); tx.Start();
                    var nid=v.Duplicate(det? ViewDuplicateOption.WithDetailing : ViewDuplicateOption.Duplicate);
                    tx.Commit();
                    return $"Duplicated {vid} -> {nid} {(det? "with detailing":"")}";
                }catch(Exception ex){return $"Failed: {ex.Message}";}
            });
        }
    };

    private static ToolEntry SetViewTemplate(RevitBridge bridge) => new()
    {
        Tier=ToolTier.L2,
        Definition=new ToolDefinition{ Function=new ToolFunction{ Name="set_view_template", Description="Set view template (DiRoots). Params: viewId, templateId (or empty to clear).", Parameters=new{type="object",properties=new{viewId=new{type="string"},templateId=new{type="string"}},required=new[]{"viewId"}}}},
        Handler=async json=>{
            string vid="",tid=""; try{using var d=JsonDocument.Parse(json); vid=d.RootElement.GetProperty("viewId").GetString()??""; if(d.RootElement.TryGetProperty("templateId",out var t)&&t.ValueKind==JsonValueKind.String) tid=t.GetString()??"";}catch(Exception ex){return $"Invalid args: {ex.Message}";}
            return await bridge.RunAsync(uiApp=>{
                var doc=uiApp.ActiveUIDocument?.Document; if(doc==null) return "Please open a model first.";
                try{
                    var v=doc.GetElement(AllTools.ParseEid(vid)) as View; if(v==null) return $"View {vid} not found.";
                    using var tx=new Transaction(doc, $"Revit AI POC: set template {vid}"); tx.Start();
                    if(string.IsNullOrWhiteSpace(tid)) v.ViewTemplateId=ElementId.InvalidElementId;
                    else v.ViewTemplateId=AllTools.ParseEid(tid);
                    tx.Commit();
                    return string.IsNullOrWhiteSpace(tid)? $"Cleared template for {vid}" : $"Set template {tid} for {vid}";
                }catch(Exception ex){return $"Failed: {ex.Message}";}
            });
        }
    };

    private static ToolEntry SetCropBox(RevitBridge bridge) => new()
    {
        Tier=ToolTier.L2,
        Definition=new ToolDefinition{ Function=new ToolFunction{ Name="set_crop_box", Description="Set view crop box active/enable (pyRevit). Params: viewId, enabled bool.", Parameters=new{type="object",properties=new{viewId=new{type="string"},enabled=new{type="boolean"}},required=new[]{"viewId","enabled"}}}},
        Handler=async json=>{
            string vid=""; bool en=true; try{using var d=JsonDocument.Parse(json); vid=d.RootElement.GetProperty("viewId").GetString()??""; en=d.RootElement.GetProperty("enabled").GetBoolean();}catch(Exception ex){return $"Invalid args: {ex.Message}";}
            return await bridge.RunAsync(uiApp=>{
                var doc=uiApp.ActiveUIDocument?.Document; if(doc==null) return "Please open a model first.";
                try{
                    var v=doc.GetElement(AllTools.ParseEid(vid)) as View; if(v==null) return $"View {vid} not found.";
                    using var tx=new Transaction(doc, $"Revit AI POC: crop {vid}"); tx.Start();
                    v.CropBoxActive=en; v.CropBoxVisible=en;
                    tx.Commit();
                    return $"Set crop {(en?"on":"off")} for {vid}";
                }catch(Exception ex){return $"Failed: {ex.Message}";}
            });
        }
    };

    private static ToolEntry CreateDimension(RevitBridge bridge) => new()
    {
        Tier=ToolTier.L3,
        Definition=new ToolDefinition{ Function = new ToolFunction{ Name="create_dimension", Description="Create linear dimension between two parallel walls (pyRevit). Params: wallId1, wallId2, viewId optional.", Parameters=new{type="object",properties=new{wallId1=new{type="string"},wallId2=new{type="string"},viewId=new{type="string"}},required=new[]{"wallId1","wallId2"}}}},
        Handler=async json=>{
            string w1="",w2="",vid=""; try{using var d=JsonDocument.Parse(json); w1=d.RootElement.GetProperty("wallId1").GetString()??""; w2=d.RootElement.GetProperty("wallId2").GetString()??""; if(d.RootElement.TryGetProperty("viewId",out var v)&&v.ValueKind==JsonValueKind.String) vid=v.GetString()??"";}catch(Exception ex){return $"Invalid args: {ex.Message}";}
            return await bridge.RunAsync(uiApp=>{
                var doc=uiApp.ActiveUIDocument?.Document; if(doc==null) return "Please open a model first.";
                var view=string.IsNullOrWhiteSpace(vid)? doc.ActiveView : doc.GetElement(AllTools.ParseEid(vid)) as View;
                if(view==null) return "View not found.";
                try{
                    var el1=doc.GetElement(AllTools.ParseEid(w1)) as Wall; var el2=doc.GetElement(AllTools.ParseEid(w2)) as Wall;
                    if(el1==null||el2==null) return "Walls not found.";
                    var loc1=(el1.Location as LocationCurve)?.Curve; var loc2=(el2.Location as LocationCurve)?.Curve;
                    if(loc1==null||loc2==null) return "Walls have no location curve.";
                    // create dimension line offset
                    var mid=(loc1.GetEndPoint(0)+loc1.GetEndPoint(1))/2;
                    var dir= (loc2.GetEndPoint(0)-loc1.GetEndPoint(0)).Normalize();
                    var perp=XYZ.BasisZ.CrossProduct(dir).Normalize()*5;
                    var p1=mid+perp; var p2=mid+perp+dir*10;
                    var line=Line.CreateBound(p1,p2);
                    using var tx=new Transaction(doc, "Revit AI POC: dimension"); tx.Start();
                    var refs=new ReferenceArray(); refs.Append(new Reference(el1)); refs.Append(new Reference(el2));
                    var dim=doc.Create.NewDimension(view, line, refs);
                    tx.Commit();
                    return $"Created dimension {dim.Id} between {w1} and {w2} in {view.Name}";
                }catch(Exception ex){return $"Failed: {ex.Message}";}
            });
        }
    };

    private static ToolEntry TagAll(RevitBridge bridge) => new()
    {
        Tier=ToolTier.L3,
        Definition=new ToolDefinition{ Function=new ToolFunction{ Name="tag_all", Description="Tag all elements of category in active view (DiRoots). Params: category.", Parameters=new{type="object",properties=new{category=new{type="string"}},required=new[]{"category"}}}},
        Handler=async json=>{
            string cat=""; try{using var d=JsonDocument.Parse(json); cat=d.RootElement.GetProperty("category").GetString()??"";}catch(Exception ex){return $"Invalid args: {ex.Message}";}
            return await bridge.RunAsync(uiApp=>{
                var doc=uiApp.ActiveUIDocument?.Document; if(doc==null) return "Please open a model first.";
                var view=doc.ActiveView; if(view==null) return "No active view.";
                try{
                    var bic=AllTools.ParseCategory(cat); if(bic==null) return $"Unknown category {cat}";
                    var elems=new FilteredElementCollector(doc, view.Id).OfCategory(bic.Value).WhereElementIsNotElementType().ToElements().Take(50).ToList();
                    int tagged=0;
                    using var tx=new Transaction(doc, $"Revit AI POC: tag {cat}"); tx.Start();
                    foreach(var el in elems){
                        try{
                            XYZ pt;
                            if (el.Location is LocationPoint lp) pt = lp.Point;
                            else if (el.get_BoundingBox(view) is { } bb) pt = (bb.Min + bb.Max) / 2;
                            else continue;
                            var tag=IndependentTag.Create(doc, view.Id, new Reference(el), false, TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, pt);
                            if(tag!=null) tagged++;
                        }catch{}
                    }
                    tx.Commit();
                    return $"Tagged {tagged}/{elems.Count} {cat} in {view.Name}";
                }catch(Exception ex){return $"Failed: {ex.Message}";}
            });
        }
    };
}
