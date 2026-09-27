using System.IO;
using Autodesk.Revit.DB;

namespace LoadingAI.Revit.Formwork;

internal static class FormworkParameters
{
    public static (int ok, int fail, List<string> missing) SetupSharedParameters(Document doc)
    {
        var app = doc.Application;
        var original = app.SharedParametersFilename;
        var ok = 0;
        var fail = 0;
        var missing = new List<string>();
        try
        {
            var path = WriteDefinitionFile();
            app.SharedParametersFilename = path;
            var file = app.OpenSharedParameterFile();
            if (file == null)
                return (0, FormworkConfig.Parameters.Length, FormworkConfig.Parameters.Select(p => p.Name).ToList());

            var group = file.Groups.Cast<DefinitionGroup>().FirstOrDefault(g => g.Name == FormworkConfig.SharedParamGroup)
                        ?? file.Groups.Create(FormworkConfig.SharedParamGroup);

            var cats = app.Create.NewCategorySet();
            var gm = Category.GetCategory(doc, BuiltInCategory.OST_GenericModel);
            if (gm != null) cats.Insert(gm);

            foreach (var def in FormworkConfig.Parameters)
            {
                try
                {
                    var existing = group.Definitions.get_Item(def.Name);
                    var ext = existing as ExternalDefinition ?? CreateExternal(group, def);
                    if (ext == null) { fail++; missing.Add(def.Name); continue; }
                    if (BindInstance(doc, ext, cats, GroupType(def.Group))) ok++;
                    else { fail++; missing.Add(def.Name); }
                }
                catch
                {
                    fail++;
                    missing.Add(def.Name);
                }
            }
            return (ok, fail, missing);
        }
        finally
        {
            try { app.SharedParametersFilename = original; }
            catch { /* restore best-effort */ }
        }
    }

    public static int EnsureNamedParameters(Document doc, IEnumerable<string> names)
    {
        var app = doc.Application;
        var original = app.SharedParametersFilename;
        var created = 0;
        try
        {
            var path = WriteDefinitionFile(extraNames: names.ToList());
            app.SharedParametersFilename = path;
            var file = app.OpenSharedParameterFile();
            if (file == null) return 0;
            var group = file.Groups.Cast<DefinitionGroup>().FirstOrDefault(g => g.Name == FormworkConfig.SharedParamGroup)
                        ?? file.Groups.Create(FormworkConfig.SharedParamGroup);
            var cats = app.Create.NewCategorySet();
            var gm = Category.GetCategory(doc, BuiltInCategory.OST_GenericModel);
            if (gm != null) cats.Insert(gm);
            foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                try
                {
                    var existing = group.Definitions.get_Item(name);
                    var ext = existing as ExternalDefinition;
                    if (ext == null)
                    {
                        var opt = new ExternalDefinitionCreationOptions(name, SpecTypeId.String.Text);
                        ext = group.Definitions.Create(opt) as ExternalDefinition;
                    }
                    if (ext != null && BindInstance(doc, ext, cats, GroupTypeId.IdentityData))
                        created++;
                }
                catch { /* skip one name */ }
            }
        }
        finally
        {
            try { app.SharedParametersFilename = original; }
            catch { /* */ }
        }
        return created;
    }

    public static void WritePanelParameters(
        DirectShape ds,
        FaceInfo face,
        FormworkSource source,
        FormworkHostInfo host,
        double areaFt2,
        double areaNetFt2,
        double thicknessFt,
        double contactRatio)
    {
        var src = host.SourceElem ?? source.Element;
        SetInt(ds, "FW_ParentElementId", ParseIdInt(src.Id));
        SetText(ds, "FW_ParentUniqueId", src.UniqueId);
        SetText(ds, "FW_ParentCategory", host.CategoryName);
        SetText(ds, "FW_ParentFamily", FormworkCore.FamilyName(src));
        SetText(ds, "FW_ParentType", FormworkCore.TypeName(src));
        SetText(ds, "FW_LinkName", source.LinkName);
        SetText(ds, "FW_Level", FormworkCore.GetLevelName(src));
        SetText(ds, "FW_PanelType", face.FaceType);
        SetDouble(ds, "FW_Area", areaFt2);
        SetDouble(ds, "FW_AreaNet", areaNetFt2);
        SetDouble(ds, "FW_Thickness", thicknessFt);
        if (contactRatio > 0)
            SetDouble(ds, "FW_ContactRatio", contactRatio);
        SetText(ds, "FW_CreatedDate", DateTime.Now.ToString("yyyy-MM-dd"));
    }

    public static IEnumerable<Parameter> IterCopyable(Element parent)
    {
        foreach (Parameter p in parent.Parameters)
        {
            if (p == null || p.IsReadOnly) continue;
            string? name;
            try { name = p.Definition?.Name; }
            catch { continue; }
            if (name is not { Length: > 0 }) continue;
            if (name.StartsWith("FW_", StringComparison.OrdinalIgnoreCase)) continue;
            if (p.StorageType is StorageType.ElementId or StorageType.None) continue;
            yield return p;
        }
    }

    public static bool CopyValue(Parameter src, Parameter dest)
    {
        if (src.StorageType != dest.StorageType) return false;
        try
        {
            switch (src.StorageType)
            {
                case StorageType.String:
                    dest.Set(src.AsString() ?? "");
                    return true;
                case StorageType.Integer:
                    dest.Set(src.AsInteger());
                    return true;
                case StorageType.Double:
                    dest.Set(src.AsDouble());
                    return true;
                default:
                    return false;
            }
        }
        catch { return false; }
    }

    public static bool IsEmpty(Parameter p)
    {
        try
        {
            return p.StorageType switch
            {
                StorageType.String => string.IsNullOrWhiteSpace(p.AsString()),
                StorageType.Integer => false,
                StorageType.Double => false,
                _ => true
            };
        }
        catch { return true; }
    }

    static ExternalDefinition? CreateExternal(DefinitionGroup group, FormworkParamDef def)
    {
        var opt = new ExternalDefinitionCreationOptions(def.Name, SpecType(def.Type));
        opt.Description = def.Description;
        return group.Definitions.Create(opt) as ExternalDefinition;
    }

    static bool BindInstance(Document doc, ExternalDefinition ext, CategorySet cats, ForgeTypeId group)
    {
        var map = doc.ParameterBindings;
        if (map.Contains(ext))
        {
            if (map.get_Item(ext) is InstanceBinding) return true;
        }
        var binding = doc.Application.Create.NewInstanceBinding(cats);
        if (map.Contains(ext))
            return map.ReInsert(ext, binding, group);
        return map.Insert(ext, binding, group);
    }

    static ForgeTypeId SpecType(string type) => type switch
    {
        "Integer" => SpecTypeId.Int.Integer,
        "Double" => SpecTypeId.Number,
        "Area" => SpecTypeId.Area,
        "Length" => SpecTypeId.Length,
        _ => SpecTypeId.String.Text
    };

    static ForgeTypeId GroupType(string group) => group switch
    {
        "PG_AREA" => GroupTypeId.IdentityData,
        "PG_GEOMETRY" => GroupTypeId.Geometry,
        "PG_ANALYSIS_RESULTS" => GroupTypeId.AnalysisResults,
        _ => GroupTypeId.IdentityData
    };

    static string WriteDefinitionFile(List<string>? extraNames = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "LoadingAI-Formwork");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, FormworkConfig.SharedParamFile);
        var lines = new List<string>
        {
            "# This is a Revit shared parameter file.",
            "*META\tVERSION\tMINVERSION",
            "META\t2\t1",
            "*GROUP\tID\tNAME",
            "GROUP\t1\t" + FormworkConfig.SharedParamGroup,
            "*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\tVISIBLE\tDESCRIPTION\tUSERMODIFIABLE"
        };
        var n = 0;
        foreach (var def in FormworkConfig.Parameters)
        {
            n++;
            var guid = DeterministicGuid(def.Name);
            var dt = SharedDataType(def.Type);
            lines.Add($"PARAM\t{guid}\t{def.Name}\t{dt}\t\t1\t1\t{def.Description}\t1");
        }
        if (extraNames != null)
        {
            foreach (var name in extraNames)
            {
                if (FormworkConfig.Parameters.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var guid = DeterministicGuid(name);
                lines.Add($"PARAM\t{guid}\t{name}\tTEXT\t\t1\t1\tCopied parent parameter\t1");
            }
        }
        File.WriteAllLines(path, lines);
        return path;
    }

    static string SharedDataType(string type) => type switch
    {
        "Integer" => "INTEGER",
        "Double" => "NUMBER",
        "Area" => "AREA",
        "Length" => "LENGTH",
        _ => "TEXT"
    };

    static Guid DeterministicGuid(string name)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        var bytes = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes("RevitAI_Formwork|" + name));
        return new Guid(bytes);
    }

    static int ParseIdInt(ElementId id)
    {
        if (long.TryParse(id.ToString(), out var v) && v >= int.MinValue && v <= int.MaxValue)
            return (int)v;
        return 0;
    }

    static void SetText(Element el, string name, string value)
    {
        var p = el.LookupParameter(name);
        if (p == null || p.IsReadOnly) return;
        try { p.Set(value ?? ""); } catch { /* */ }
    }

    static void SetInt(Element el, string name, int value)
    {
        var p = el.LookupParameter(name);
        if (p == null || p.IsReadOnly) return;
        try { p.Set(value); } catch { /* */ }
    }

    static void SetDouble(Element el, string name, double value)
    {
        var p = el.LookupParameter(name);
        if (p == null || p.IsReadOnly) return;
        try { p.Set(value); } catch { /* */ }
    }
}
