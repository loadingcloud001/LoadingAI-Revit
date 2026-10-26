using Autodesk.Revit.UI;
using LoadingAI.Llm;
using LoadingAI.Revit;

namespace LoadingAI.Ui;

/// <summary>
/// A review is only a review if the person can see it. If the dockable pane is closed or collapsed when a card
/// needs an answer, bring it up (ADR-0017). There is no dialog fallback.
/// </summary>
internal static class PaneVisibility
{
    private static readonly TimeSpan ShowTimeout = TimeSpan.FromSeconds(5);

    public static async Task ShowIfHiddenAsync()
    {
        var bridge = App.Bridge;
        if (bridge == null) return;
        try
        {
            await bridge.RunAsync(app =>
            {
                var pane = app.GetDockablePane(new DockablePaneId(App.PaneId));
                if (!pane.IsShown()) pane.Show();
                return true;
            }, ShowTimeout);
        }
        catch (Exception ex)
        {
            // Best effort. The card is still posted; the review timeout cancels it if no one ever sees it.
            HostLog.Write($"Showing the pane for a review failed: {ex.Message}");
        }
    }
}
