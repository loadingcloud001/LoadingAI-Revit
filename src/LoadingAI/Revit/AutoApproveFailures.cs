using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using LoadingAI.Llm;

namespace LoadingAI.Revit;

/// <summary>
/// While Auto-approve is on and a LoadingAI turn is open, dismiss Revit's failure dialog.
/// Warnings are cleared. An error is resolved only when Revit offers a fix that does not
/// delete or create elements. Otherwise the transaction rolls back and no dialog is shown.
/// </summary>
public static class AutoApproveFailures
{
    private static readonly FailureResolutionType[] Prefer =
    {
        FailureResolutionType.DetachElements,
        FailureResolutionType.UnlockConstraints,
        FailureResolutionType.FixElements,
        FailureResolutionType.MoveElements,
        FailureResolutionType.SkipElements,
    };

    public static void OnFailures(object? sender, FailuresProcessingEventArgs args)
    {
        if (!AutoApproveGate.Enabled) return;
        if (App.Bridge?.HasActiveTurnGroup != true) return;
        // A transaction preprocessor that already chose commit or roll-back must keep that choice.
        if (args.GetProcessingResult() != FailureProcessingResult.Continue) return;

        var accessor = args.GetFailuresAccessor();
        if (accessor == null) return;
        var messages = accessor.GetFailureMessages();
        if (messages == null || messages.Count == 0) return;

        var warnings = new List<FailureMessageAccessor>();
        var hasError = false;
        var resolved = 0;
        foreach (var failure in messages)
        {
            if (failure.GetSeverity() == FailureSeverity.Warning)
            {
                warnings.Add(failure);
                continue;
            }

            if (TryResolve(accessor, failure))
                resolved++;
            else
                hasError = true;
        }

        foreach (var warning in warnings)
            accessor.DeleteWarning(warning);

        if (hasError)
            args.SetProcessingResult(FailureProcessingResult.ProceedWithRollBack);
        else if (resolved > 0)
            args.SetProcessingResult(FailureProcessingResult.ProceedWithCommit);
    }

    private static bool TryResolve(FailuresAccessor accessor, FailureMessageAccessor failure)
    {
        foreach (var type in Prefer)
        {
            try
            {
                if (!failure.HasResolutionOfType(type)) continue;
                failure.SetCurrentResolutionType(type);
                accessor.ResolveFailure(failure);
                return true;
            }
            catch
            {
                return false;
            }
        }
        return false;
    }
}
