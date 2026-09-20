using Autodesk.Revit.DB;

namespace LoadingAI.Revit.StructureCuts;

/// <summary>
/// The numbers a split run already computes but never puts in its prose report. A caller passes one in
/// to <c>Run</c> and reads the counters back afterwards; when it is null the run behaves exactly as
/// before. Every counter stays zero on a path that does not write, so a dry run can never read as a
/// write and a partial run cannot be mistaken for a whole one.
/// </summary>
internal sealed class SplitOutcome
{
    /// <summary>Hosts that were replaced by more than one piece.</summary>
    public int Created { get; set; }

    /// <summary>Openings cut into the new floor pieces.</summary>
    public int Openings { get; set; }

    /// <summary>Hosts left alone, for every reason the run names.</summary>
    public int Skipped { get; set; }

    /// <summary>The new pieces, in the same order the run's report names them.</summary>
    public List<string> NewIds { get; } = new();

    /// <summary>Records a completed run. Pass an empty list when nothing was written.</summary>
    public void Record(IReadOnlyList<ElementId> created, int openings, int skipped)
    {
        Created = created.Count;
        Openings = openings;
        Skipped = skipped;
        NewIds.Clear();
        foreach (var id in created)
            NewIds.Add(SplitElements.Fmt(id));
    }
}
