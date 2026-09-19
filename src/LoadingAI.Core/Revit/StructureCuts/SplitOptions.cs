namespace LoadingAI.Revit.StructureCuts;

/// <summary>Shared knobs for every split tool, resolved once from the tool arguments.</summary>
public sealed class SplitOptions
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;
    public const string DefaultScope = "selection";

    /// <summary>Max elements to process; the host form defaults it and this clamps the LLM's number.</summary>
    public int Limit { get; init; } = DefaultLimit;

    public bool DryRun { get; init; }

    /// <summary>
    /// When on, the first piece keeps the original Mark and the rest become <c>&lt;Mark&gt;-1</c>,
    /// <c>-2</c>, ... in creation order. A blank Mark stays blank.
    /// </summary>
    public bool UniqueMark { get; init; }

    /// <summary>Off leaves the new pieces with their type defaults instead of copying the original's.</summary>
    public bool CopyParams { get; init; } = true;

    /// <summary>
    /// Where hosts come from when ids are omitted: <c>selection</c> (default), <c>visible</c> in the
    /// active view, or <c>all</c> in the model. Chat never has to paste hundreds of ids.
    /// </summary>
    public string Scope { get; init; } = DefaultScope;

    /// <summary>How many already-collected hosts to skip, so a capped run can continue from nextOffset.</summary>
    public int ContinueFrom { get; init; }
}