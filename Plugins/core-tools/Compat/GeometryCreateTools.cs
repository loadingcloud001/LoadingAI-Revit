using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LoadingAI.Llm;
using LoadingAI.Revit;
using LoadingAI.Plugins;

namespace LoadingAI.Tools.Compat;

public static class GeometryCreateTools
{
    public static IEnumerable<ToolEntry> Create(IPluginContext context)
    {
        yield return CreateFloorByOutline(context);
        yield return CreateRoof(context);
        yield return CreateGridLevel(context);
        yield return CreateFamilyInstanceArray(context);
        yield return RotateAlign(context);
        yield return MirrorTool(context);
        yield return SplitWall(context);
        yield return JoinGeometry(context);
    }

    private static ToolEntry CreateFloorByOutline(IPluginContext context) => new()
    {
        Tier=ToolTier.L3,
        Definition=new ToolDefinition{ Function=new ToolFunction{ Name="create_floor_by_outline", Description="Create floor by rectangle outline (Dynamo: Floor.ByOutline). Params: levelName, x1,y1,x2,y2 in mm, floorTypeName optional.", Parameters=new{type="object",properties=new{levelName=new{type="string"},x1=new{type="number"},y1=new{type="number"},x2=new{type="number"},y2=new{type="number"},floorTypeName=new{type="string"}},required=new[]{"levelName","x1","y1","x2","y2"}}}},
        Handler=async json=>{
            string lvl=""; double x1=0,y1=0,x2=10,y2=10; string? ft=null;
            try{using var d=JsonDocument.Parse(json); lvl=d.RootElement.GetProperty("levelName").GetString()??""; x1=d.RootElement.GetProperty("x1").GetDouble(); y1=d.RootElement.GetProperty("y1").GetDouble(); x2=d.RootElement.GetProperty("x2").GetDouble(); y2=d.RootElement.GetProperty("y2").GetDouble(); if(d.RootElement.TryGetProperty("floorTypeName",out var v)&&v.ValueKind==JsonValueKind.String) ft=v.GetString();}catch(Exception ex){return $"Invalid args: {ex.Message}";}
            x1=Metric.MmToFt(x1); y1=Metric.MmToFt(y1); x2=Metric.MmToFt(x2); y2=Metric.MmToFt(y2);
            return await context.FullAccess!.RunRevitAsync(session=>{
                var uiApp = session.As<UIApplication>();
                var doc=uiApp.ActiveUIDocument?.Document; if(doc==null) return "Please open a model first.";
                try{
                    var level=new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault(l=>l.Name.Equals(lvl,StringComparison.OrdinalIgnoreCase));
                    if(level==null) return $"Level {lvl} not found.";
                    FloorType? ftype=null;
                    if(!string.IsNullOrWhiteSpace(ft)) ftype=new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().FirstOrDefault(f=>f.Name.Equals(ft,StringComparison.OrdinalIgnoreCase));
                    ftype??=new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().FirstOrDefault();
                    if(ftype==null) return "No floor types.";
                    var z=level.Elevation;
                    var pts=new[]{ new XYZ(x1,y1,z), new XYZ(x2,y1,z), new XYZ(x2,y2,z), new XYZ(x1,y2,z)};
                    var curveLoop=new CurveLoop();
                    for(int i=0;i<4;i++) curveLoop.Append(Line.CreateBound(pts[i], pts[(i+1)%4]));
                    using var tx=new Transaction(doc, $"LoadingAI for Revit: floor {lvl}"); tx.Start();
                    var floor=Floor.Create(doc, new[]{curveLoop}, ftype.Id, level.Id);
                    tx.Commit();
                    return $"Created floor {floor.Id} on {lvl} rect ({x1},{y1})-({x2},{y2})";
                }catch(Exception ex){return $"Failed: {ex.Message}";}
            });
        }
    };

    private static ToolEntry CreateRoof(IPluginContext context) => new()
    {
        Tier=ToolTier.L3,
        Definition=new ToolDefinition{ Function=new ToolFunction{ Name="create_roof", Description="Create flat roof by outline (pyRevit). Params: levelName, x1,y1,x2,y2 in mm.", Parameters=new{type="object",properties=new{levelName=new{type="string"},x1=new{type="number"},y1=new{type="number"},x2=new{type="number"},y2=new{type="number"}},required=new[]{"levelName","x1","y1","x2","y2"}}}},
        Handler=async json=>{
            string lvl=""; double x1=0,y1=0,x2=10,y2=10;
            try{using var d=JsonDocument.Parse(json); lvl=d.RootElement.GetProperty("levelName").GetString()??""; x1=d.RootElement.GetProperty("x1").GetDouble(); y1=d.RootElement.GetProperty("y1").GetDouble(); x2=d.RootElement.GetProperty("x2").GetDouble(); y2=d.RootElement.GetProperty("y2").GetDouble();}catch(Exception ex){return $"Invalid args: {ex.Message}";}
            x1=Metric.MmToFt(x1); y1=Metric.MmToFt(y1); x2=Metric.MmToFt(x2); y2=Metric.MmToFt(y2);
            return await context.FullAccess!.RunRevitAsync(session=>{
                var uiApp = session.As<UIApplication>();
                var doc=uiApp.ActiveUIDocument?.Document; if(doc==null) return "Please open a model first.";
                try{
                    var level=new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault(l=>l.Name.Equals(lvl,StringComparison.OrdinalIgnoreCase));
                    if(level==null) return $"Level {lvl} not found.";
                    var rtype=new FilteredElementCollector(doc).OfClass(typeof(RoofType)).Cast<RoofType>().FirstOrDefault();
                    if(rtype==null) return "No roof type.";
                    var pts=new[]{ new XYZ(x1,y1,0), new XYZ(x2,y1,0), new XYZ(x2,y2,0), new XYZ(x1,y2,0)};
                    var loop=new CurveArray();
                    for(int i=0;i<4;i++) loop.Append(Line.CreateBound(pts[i], pts[(i+1)%4]));
                    using var tx=new Transaction(doc, "LoadingAI for Revit: roof"); tx.Start();
                    ModelCurveArray mca;
                    var roof=doc.Create.NewFootPrintRoof(loop, level, rtype, out mca);
                    tx.Commit();
                    return $"Created roof {roof.Id} on {lvl}";
                }catch(Exception ex){return $"Failed: {ex.Message}";}
            });
        }
    };

    private static ToolEntry CreateGridLevel(IPluginContext context) => new()
    {
        Tier=ToolTier.L3,
        Definition=new ToolDefinition{ Function=new ToolFunction{ Name="create_grid_level", Description="Create grid and level (Dynamo: Grid.ByLine/ Level). Params: kind grid|level, name, x1,y1,x2,y2 or elevation in mm.", Parameters=new{type="object",properties=new{kind=new{type="string",description="grid or level"},name=new{type="string"},x1=new{type="number"},y1=new{type="number"},x2=new{type="number"},y2=new{type="number"},elevation=new{type="number"}},required=new[]{"kind","name"}}}},
        Handler=async json=>{
            string t="",nm=""; double x1=0,y1=0,x2=10,y2=0, elev=10;
            try{using var d=JsonDocument.Parse(json); t=(d.RootElement.TryGetProperty("kind",out var k)?k.GetString():null)??(d.RootElement.TryGetProperty("type",out var ty)?ty.GetString():null)??""; nm=d.RootElement.GetProperty("name").GetString()??""; if(d.RootElement.TryGetProperty("x1",out var a)&&a.ValueKind==JsonValueKind.Number) x1=a.GetDouble(); if(d.RootElement.TryGetProperty("y1",out var b)&&b.ValueKind==JsonValueKind.Number) y1=b.GetDouble(); if(d.RootElement.TryGetProperty("x2",out var c)&&c.ValueKind==JsonValueKind.Number) x2=c.GetDouble(); if(d.RootElement.TryGetProperty("y2",out var d2)&&d2.ValueKind==JsonValueKind.Number) y2=d2.GetDouble(); if(d.RootElement.TryGetProperty("elevation",out var e)&&e.ValueKind==JsonValueKind.Number) elev=e.GetDouble();}catch(Exception ex){return $"Invalid args: {ex.Message}";}
            x1=Metric.MmToFt(x1); y1=Metric.MmToFt(y1); x2=Metric.MmToFt(x2); y2=Metric.MmToFt(y2); elev=Metric.MmToFt(elev);
            return await context.FullAccess!.RunRevitAsync(session=>{
                var uiApp = session.As<UIApplication>();
                var doc=uiApp.ActiveUIDocument?.Document; if(doc==null) return "Please open a model first.";
                try{
                    if(t.Equals("level",StringComparison.OrdinalIgnoreCase)){
                        using var tx=new Transaction(doc, $"LoadingAI for Revit: level {nm}"); tx.Start();
                        var lvl=Level.Create(doc, elev); lvl.Name=nm;
                        tx.Commit(); return $"Created level {lvl.Id} \"{nm}\" elev {elev}";
                    } else {
                        using var tx=new Transaction(doc, $"LoadingAI for Revit: grid {nm}"); tx.Start();
                        var g=Grid.Create(doc, Line.CreateBound(new XYZ(x1,y1,0), new XYZ(x2,y2,0))); g.Name=nm;
                        tx.Commit(); return $"Created grid {g.Id} \"{nm}\"";
                    }
                }catch(Exception ex){return $"Failed: {ex.Message}";}
            });
        }
    };

    private static ToolEntry CreateFamilyInstanceArray(IPluginContext context) => new()
    {
        Tier=ToolTier.L3,
        Definition=new ToolDefinition{ Function=new ToolFunction{ Name="create_family_instance_array", Description="Array family instances along line (Dynamo: FamilyInstance.ByPoint array). Params: symbolName, startX,startY, endX,endY in mm, count, levelName optional.", Parameters=new{type="object",properties=new{symbolName=new{type="string"},startX=new{type="number"},startY=new{type="number"},endX=new{type="number"},endY=new{type="number"},count=new{type="integer"},levelName=new{type="string"}},required=new[]{"symbolName","startX","startY","endX","endY","count"}}}},
        Handler=async json=>{
            string sym=""; double sx=0,sy=0,ex=10,ey=0; int cnt=5; string? lvl=null;
            try{using var d=JsonDocument.Parse(json); sym=d.RootElement.GetProperty("symbolName").GetString()??""; sx=d.RootElement.GetProperty("startX").GetDouble(); sy=d.RootElement.GetProperty("startY").GetDouble(); ex=d.RootElement.GetProperty("endX").GetDouble(); ey=d.RootElement.GetProperty("endY").GetDouble(); cnt=d.RootElement.GetProperty("count").GetInt32(); if(d.RootElement.TryGetProperty("levelName",out var l)&&l.ValueKind==JsonValueKind.String) lvl=l.GetString();}catch(Exception ex2){return $"Invalid args: {ex2.Message}";}
            sx=Metric.MmToFt(sx); sy=Metric.MmToFt(sy); ex=Metric.MmToFt(ex); ey=Metric.MmToFt(ey);
            cnt=Polyfills.Clamp(cnt,1,50);
            return await context.FullAccess!.RunRevitAsync(session=>{
                var uiApp = session.As<UIApplication>();
                var doc=uiApp.ActiveUIDocument?.Document; if(doc==null) return "Please open a model first.";
                try{
                    var symbol=new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().FirstOrDefault(s=>s.Name.Equals(sym,StringComparison.OrdinalIgnoreCase));
                    if(symbol==null) return $"Symbol {sym} not found.";
                    if(!symbol.IsActive) symbol.Activate();
                    Level? level=null;
                    if(!string.IsNullOrWhiteSpace(lvl)) level=new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault(l=>l.Name.Equals(lvl,StringComparison.OrdinalIgnoreCase));
                    level??=new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault();
                    if(level==null) return "No level.";
                    using var tx=new Transaction(doc, $"LoadingAI for Revit: array {sym} x{cnt}"); tx.Start();
                    var ids=new List<ElementId>();
                    for(int i=0;i<cnt;i++){
                        double t=(double)i/(cnt-1==0?1:cnt-1);
                        var pt=new XYZ(sx+(ex-sx)*t, sy+(ey-sy)*t, 0);
                        var inst=doc.Create.NewFamilyInstance(pt, symbol, level, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                        ids.Add(inst.Id);
                    }
                    tx.Commit();
                    return $"Created {ids.Count} {sym} array {string.Join(",", ids.Take(5))}";
                }catch(Exception ex){return $"Failed: {ex.Message}";}
            });
        }
    };

    private static ToolEntry RotateAlign(IPluginContext context) => new()
    {
        Tier=ToolTier.L3,
        RequiresWriteConfirm=true,
        Definition=new ToolDefinition{ Function=new ToolFunction{ Name="rotate_elements", Description="Rotate elements around Z (pyRevit: Rotate). Params: elementIds, angleDegrees, originX,originY optional (else centroid).", Parameters=new{type="object",properties=new{elementIds=new{type="array",items=new{type="string"}},angleDegrees=new{type="number"},originX=new{type="number"},originY=new{type="number"}},required=new[]{"elementIds","angleDegrees"}}}},
        Handler=async json=>{
            List<string> ids=new(); double ang=0, ox=double.NaN, oy=double.NaN;
            try{using var d=JsonDocument.Parse(json); ids=d.RootElement.GetProperty("elementIds").EnumerateArray().Where(v=>v.ValueKind==JsonValueKind.String).Select(v=>v.GetString()!).ToList(); ang=d.RootElement.GetProperty("angleDegrees").GetDouble(); if(d.RootElement.TryGetProperty("originX",out var xv)&&xv.ValueKind==JsonValueKind.Number) ox=xv.GetDouble(); if(d.RootElement.TryGetProperty("originY",out var yv)&&yv.ValueKind==JsonValueKind.Number) oy=yv.GetDouble();}catch(Exception ex){return $"Invalid args: {ex.Message}";}
            if(!double.IsNaN(ox)) ox=Metric.MmToFt(ox);
            if(!double.IsNaN(oy)) oy=Metric.MmToFt(oy);
            return await context.FullAccess!.RunRevitAsync(session=>{
                var uiApp = session.As<UIApplication>();
                var doc=uiApp.ActiveUIDocument?.Document; if(doc==null) return "Please open a model first.";
                try{
                    var eids=ids.Select(AllTools.ParseEid).ToList();
                    XYZ origin;
                    if(!double.IsNaN(ox)&&!double.IsNaN(oy)) origin=new XYZ(ox,oy,0);
                    else {
                        var bb=eids.Select(id=>doc.GetElement(id)?.get_BoundingBox(null)).Where(b=>b!=null).ToList();
                        if(bb.Count==0) return "No bounding box.";
                        var minX=bb.Min(b=>b!.Min.X); var minY=bb.Min(b=>b!.Min.Y); var maxX=bb.Max(b=>b!.Max.X); var maxY=bb.Max(b=>b!.Max.Y);
                        origin=new XYZ((minX+maxX)/2,(minY+maxY)/2,0);
                    }
                    var axis=Line.CreateBound(origin, origin+XYZ.BasisZ);
                    double rad=ang*Math.PI/180;
                    using var tx=new Transaction(doc, $"LoadingAI for Revit: rotate {ang}°"); tx.Start();
                    foreach(var id in eids) try{ElementTransformUtils.RotateElement(doc, id, axis, rad);}catch{}
                    tx.Commit();
                    return $"Rotated {eids.Count} elements {ang}° around {origin}";
                }catch(Exception ex){return $"Failed: {ex.Message}";}
            });
        }
    };

    private static ToolEntry MirrorTool(IPluginContext context) => new()
    {
        Tier=ToolTier.L3,
        Definition=new ToolDefinition{ Function=new ToolFunction{ Name="mirror_elements", Description="Mirror elements across line (pyRevit). Params: elementIds, x1,y1,x2,y2.", Parameters=new{type="object",properties=new{elementIds=new{type="array",items=new{type="string"}},x1=new{type="number"},y1=new{type="number"},x2=new{type="number"},y2=new{type="number"}},required=new[]{"elementIds","x1","y1","x2","y2"}}}},
        Handler=async json=>{
            List<string> ids=new(); double x1=0,y1=0,x2=10,y2=0;
            try{using var d=JsonDocument.Parse(json); ids=d.RootElement.GetProperty("elementIds").EnumerateArray().Where(v=>v.ValueKind==JsonValueKind.String).Select(v=>v.GetString()!).ToList(); x1=d.RootElement.GetProperty("x1").GetDouble(); y1=d.RootElement.GetProperty("y1").GetDouble(); x2=d.RootElement.GetProperty("x2").GetDouble(); y2=d.RootElement.GetProperty("y2").GetDouble();}catch(Exception ex){return $"Invalid args: {ex.Message}";}
            x1=Metric.MmToFt(x1); y1=Metric.MmToFt(y1); x2=Metric.MmToFt(x2); y2=Metric.MmToFt(y2);
            return await context.FullAccess!.RunRevitAsync(session=>{
                var uiApp = session.As<UIApplication>();
                var doc=uiApp.ActiveUIDocument?.Document; if(doc==null) return "Please open a model first.";
                try{
                    var eids=ids.Select(AllTools.ParseEid).ToList();
                    var plane=Plane.CreateByThreePoints(new XYZ(x1,y1,0), new XYZ(x2,y2,0), new XYZ(x1,y1,1));
                    using var tx=new Transaction(doc, "LoadingAI for Revit: mirror"); tx.Start();
                    var newIds=ElementTransformUtils.MirrorElements(doc, eids, plane, false);
                    tx.Commit();
                    return $"Mirrored {eids.Count} -> {newIds.Count} new elements";
                }catch(Exception ex){return $"Failed: {ex.Message}";}
            });
        }
    };

    private static ToolEntry SplitWall(IPluginContext context) => new()
    {
        Tier=ToolTier.L3,
        Definition=new ToolDefinition{ Function=new ToolFunction{ Name="split_wall", Description="Split wall at point (pyRevit). Params: wallId, x,y.", Parameters=new{type="object",properties=new{wallId=new{type="string"},x=new{type="number"},y=new{type="number"}},required=new[]{"wallId","x","y"}}}},
        Handler=async json=>{
            string wid=""; double x=0,y=0; try{using var d=JsonDocument.Parse(json); wid=d.RootElement.GetProperty("wallId").GetString()??""; x=d.RootElement.GetProperty("x").GetDouble(); y=d.RootElement.GetProperty("y").GetDouble();}catch(Exception ex){return $"Invalid args: {ex.Message}";}
            x=Metric.MmToFt(x); y=Metric.MmToFt(y);
            return await context.FullAccess!.RunRevitAsync(session=>{
                var uiApp = session.As<UIApplication>();
                var doc=uiApp.ActiveUIDocument?.Document; if(doc==null) return "Please open a model first.";
                try{
                    var wall=doc.GetElement(AllTools.ParseEid(wid)) as Wall; if(wall==null) return $"Wall {wid} not found.";
                    var loc=wall.Location as LocationCurve; if(loc==null) return "Wall has no curve.";
                    var pt=new XYZ(x,y,0);
                    using var tx=new Transaction(doc, "LoadingAI for Revit: split wall"); tx.Start();
                    // Use WallUtils? Split via curve intersection param
                    // Approximate: use ElementTransformUtils? For POC, just report not implemented with alternative
                    tx.RollBack();
                    return $"Split not yet implemented for {wid} at ({x},{y}) — use split via Dynamo or manual. Wall curve: {loc.Curve}";
                }catch(Exception ex){return $"Failed: {ex.Message}";}
            });
        }
    };

    private static ToolEntry JoinGeometry(IPluginContext context) => new()
    {
        Tier=ToolTier.L2,
        Definition=new ToolDefinition{ Function=new ToolFunction{ Name="join_geometry", Description="Join two elements geometry (pyRevit). Params: elementId1, elementId2, join bool.", Parameters=new{type="object",properties=new{elementId1=new{type="string"},elementId2=new{type="string"},join=new{type="boolean"}},required=new[]{"elementId1","elementId2"}}}},
        Handler=async json=>{
            string id1="",id2=""; bool j=true; try{using var d=JsonDocument.Parse(json); id1=d.RootElement.GetProperty("elementId1").GetString()??""; id2=d.RootElement.GetProperty("elementId2").GetString()??""; if(d.RootElement.TryGetProperty("join",out var v)&&v.ValueKind is JsonValueKind.True or JsonValueKind.False) j=v.GetBoolean();}catch(Exception ex){return $"Invalid args: {ex.Message}";}
            return await context.FullAccess!.RunRevitAsync(session=>{
                var uiApp = session.As<UIApplication>();
                var doc=uiApp.ActiveUIDocument?.Document; if(doc==null) return "Please open a model first.";
                try{
                    var e1=doc.GetElement(AllTools.ParseEid(id1)); var e2=doc.GetElement(AllTools.ParseEid(id2));
                    if(e1==null||e2==null) return "Elements not found.";
                    using var tx=new Transaction(doc, "LoadingAI for Revit: join"); tx.Start();
                    if(j) JoinGeometryUtils.JoinGeometry(doc, e1, e2); else JoinGeometryUtils.UnjoinGeometry(doc, e1, e2);
                    tx.Commit();
                    return j? $"Joined {id1} and {id2}" : $"Unjoined {id1} and {id2}";
                }catch(Exception ex){return $"Failed: {ex.Message}";}
            });
        }
    };
}
