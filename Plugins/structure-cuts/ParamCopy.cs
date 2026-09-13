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

    /// <summary>
    /// Copies the snapshot, then applies <paramref name="markOverride"/> last so a unique-Mark run wins
    /// over the copied Mark. A null override leaves whatever the copy produced.
    /// </summary>
    public static int Apply(Snapshot snap, Element dest, string? markOverride)
    {
        var skipped = Apply(snap, dest);
        SetMark(dest, markOverride);
        return skipped;
    }

    /// <summary>The element's current Mark, or null when it has none to build on.</summary>
    public static string? Mark(Element source)
    {
        try
        {
            var p = source.get_Parameter(BuiltInParameter.ALL_MODEL_MARK) ?? source.LookupParameter("Mark");
            if (p == null || p.StorageType != StorageType.String)
                return null;
            var value = p.AsString();
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Used on its own when the caller turned parameter copying off but still wants unique Marks.</summary>
    public static void SetMark(Element dest, string? mark)
    {
        if (mark == null) return;
        try
        {
            var p = dest.get_Parameter(BuiltInParameter.ALL_MODEL_MARK) ?? dest.LookupParameter("Mark");
            if (p != null && !p.IsReadOnly && p.StorageType == StorageType.String)
                p.Set(mark);
        }
        catch { }
    }

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
