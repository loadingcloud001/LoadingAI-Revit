using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using LoadingAI.Gate;
using LoadingAI.Llm;

namespace LoadingAI.Revit;

/// <summary>
/// Feeds <c>Application.DocumentChanged</c> into a <see cref="TurnChangeSet"/> while a turn's TransactionGroup
/// is open, so the pane can show what the turn changed (ADR-0017). Only commits made inside LoadingAI's own turn
/// group are counted; a rolled-back group clears the set. It reads, never writes, and never throws into Revit.
/// </summary>
internal static class TurnChangeTracker
{
    private static TurnChangeSet? _current;

    public static void Begin(TurnChangeSet set)
    {
        set.Reset();
        Volatile.Write(ref _current, set);
    }

    public static void End() => Volatile.Write(ref _current, null);

    public static void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        var set = Volatile.Read(ref _current);
        if (set == null) return;
        try
        {
            var bridge = App.Bridge;
            var doc = e.GetDocument();
            if (bridge == null || doc == null || !bridge.IsTurnDocument(doc)) return;

            switch (e.Operation)
            {
                case UndoOperation.TransactionGroupRolledBack:
                    set.Reset();
                    return;
                case UndoOperation.TransactionCommitted:
                    break;
                default:
                    return;
            }

            foreach (var id in e.GetAddedElementIds())
                if (TryDescribe(doc, id, out var category)) set.Added(id.Value, category);
            foreach (var id in e.GetModifiedElementIds())
                if (TryDescribe(doc, id, out var category)) set.Modified(id.Value, category);
            // A deleted element is gone, so its category is unknown here.
            foreach (var id in e.GetDeletedElementIds())
                set.Deleted(id.Value);
        }
        catch (Exception ex)
        {
            HostLog.Write($"Turn change tracking failed: {ex.Message}");
        }
    }

    /// <summary>Types and other definitions are not model changes the engineer would look for.</summary>
    private static bool TryDescribe(Document doc, ElementId id, out string? category)
    {
        category = null;
        var el = doc.GetElement(id);
        if (el == null || el is ElementType) return false;
        category = el.Category?.Name;
        return true;
    }
}
