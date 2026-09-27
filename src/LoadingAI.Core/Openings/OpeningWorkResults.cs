using LoadingAI.Revit;

namespace LoadingAI.Core.Openings;

/// <summary>Authoritative non-Revit placement outcome produced by the host algorithm.</summary>
public sealed record OpeningPlaceWorkResult(
    string Report,
    int Placed,
    int Punched,
    int Failed,
    int Skipped,
    int JoinsLost,
    IReadOnlyList<string> InstanceIds,
    IReadOnlyList<string> Points,
    string? Error = null);

/// <summary>Authoritative non-Revit repair-cut outcome produced by the host algorithm.</summary>
public sealed record OpeningCutWorkResult(
    string Report,
    int Cut,
    int SkippedLinked,
    int SkippedCannot,
    int Failed,
    string? Error = null);

/// <summary>Maps host work outcomes to the public Bridge DTOs without parsing report text.</summary>
public static class OpeningOutcomeMapper
{
    public static OpeningPlaceResultDto ToPlaceDto(OpeningPlaceWorkResult work)
        => new(
            work.Report,
            work.Placed,
            work.Punched,
            work.Failed,
            work.Skipped,
            work.JoinsLost,
            work.InstanceIds,
            work.Points,
            work.Error);

    public static OpeningCutResultDto ToCutDto(OpeningCutWorkResult work)
        => new(
            work.Report,
            work.Cut,
            work.SkippedLinked,
            work.SkippedCannot,
            work.Failed,
            work.Error);
}
