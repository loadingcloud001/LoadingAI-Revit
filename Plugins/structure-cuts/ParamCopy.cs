using Autodesk.Revit.DB;

namespace LoadingAI.Plugins.StructureCuts;

internal static class ParamCopy
{
    internal sealed class Snapshot
    {
        public List<Item> Items { get; } = new();
    }

    internal sealed class Item
    {
        public string Name { get; init; } = "";
        public BuiltInParameter BuiltIn { get; init; }
        public StorageType Storage { get; init; }
        public string? StringValue { get; init; }
        public int IntValue { get; init; }
        public double DoubleValue { get; init; }
    }

    public static Snapshot Take(Element source)
    {
        var snap = new Snapshot();
        foreach (Parameter p in source.Parameters)
        {
            if (!IsCopyable(p)) continue;
            string? name;
            try { name = p.Definition?.Name; }
            catch { continue; }
            if (string.IsNullOrEmpty(name)) continue;
            if (name.IndexOf("Extension", StringComparison.OrdinalIgnoreCase) >= 0) continue;

            var builtIn = BuiltInOf(p);
            try
            {
                switch (p.StorageType)
                {
                    case StorageType.String:
                        snap.Items.Add(new Item
                        {
                            Name = name,
                            BuiltIn = builtIn,
                            Storage = StorageType.String,
                            StringValue = p.AsString()
                        });
                        break;
                    case StorageType.Integer:
                        snap.Items.Add(new Item
                        {
                            Name = name,
                            BuiltIn = builtIn,
                            Storage = StorageType.Integer,
                            IntValue = p.AsInteger()
                        });
                        break;
                    case StorageType.Double:
                        snap.Items.Add(new Item
                        {
                            Name = name,
                            BuiltIn = builtIn,
                            Storage = StorageType.Double,
                            DoubleValue = p.AsDouble()
                        });
                        break;
                }
            }
            catch { }
        }
        return snap;
    }

    public static int Apply(Element source, Element dest)
        => Apply(Take(source), dest);

    public static int Apply(Snapshot snap, Element dest)
    {
        var skipped = 0;
        foreach (var item in snap.Items)
        {
            Parameter? destParam = null;
            try
            {
                if (item.BuiltIn != BuiltInParameter.INVALID)
                    destParam = dest.get_Parameter(item.BuiltIn);
                destParam ??= dest.LookupParameter(item.Name);
            }
            catch
            {
                skipped++;
                continue;
            }
            if (destParam == null || destParam.IsReadOnly || destParam.StorageType != item.Storage)
            {
                skipped++;
                continue;
            }
            try
            {
                var ok = item.Storage switch
                {
                    StorageType.String => destParam.Set(item.StringValue ?? ""),
                    StorageType.Integer => destParam.Set(item.IntValue),
                    StorageType.Double => destParam.Set(item.DoubleValue),
                    _ => false
                };
                if (!ok) skipped++;
            }
            catch
            {
                skipped++;
            }
        }
        return skipped;
    }

    private static bool IsCopyable(Parameter p)
    {
        try
        {
            if (p == null || p.IsReadOnly) return false;
            if (p.StorageType is StorageType.ElementId or StorageType.None) return false;
            if (!p.HasValue) return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static BuiltInParameter BuiltInOf(Parameter p)
    {
        try
        {
            if (p.Definition is InternalDefinition def)
                return def.BuiltInParameter;
        }
        catch { }
        return BuiltInParameter.INVALID;
    }
}
