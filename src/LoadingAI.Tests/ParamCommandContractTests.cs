using System;
using System.Linq;
using LoadingAI.Revit;

namespace LoadingAI.Tests;

public class ParamCommandContractTests
{
    [Fact]
    public void IRevitParamCommand_DeclaresPhase3ParamMethods()
    {
        var names = typeof(IRevitParamCommand).GetMethods().Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("SetParameterAsync", names);
        Assert.Contains("SetParametersAsync", names);
        Assert.Contains("RenameElementsAsync", names);
        Assert.Contains("ApplyConstructionDatesAsync", names);
        Assert.Contains("ExportParamsAsync", names);
        Assert.Contains("ImportParamsAsync", names);
        Assert.Contains("CopyParamsAsync", names);
        Assert.Contains("ReplaceTextInParamAsync", names);
        Assert.Contains("NumberElementsAsync", names);
        Assert.DoesNotContain("RunBatchAsync", names);
        Assert.DoesNotContain("IsolateInViewAsync", names);
        Assert.DoesNotContain("DeleteElementsAsync", names);
        Assert.DoesNotContain("ColorByConstructionScheduleAsync", names);
        Assert.DoesNotContain("CreateSheetAsync", names);
        Assert.DoesNotContain("PlaceViewOnSheetAsync", names);
        Assert.DoesNotContain("DuplicateViewAsync", names);
        Assert.DoesNotContain("SetViewTemplateAsync", names);
        Assert.DoesNotContain("SetCropBoxAsync", names);
        Assert.DoesNotContain("CreateDimensionBetweenWallsAsync", names);
        Assert.DoesNotContain("TagAllInActiveViewAsync", names);
        Assert.DoesNotContain("SaveDocumentAsync", names);
        Assert.DoesNotContain("ReloadLinksAsync", names);
        Assert.DoesNotContain("AssignWorksetAsync", names);
        Assert.DoesNotContain("GetWorksetsAsync", names);
        Assert.DoesNotContain("GetClashByBoundingBoxAsync", names);
        Assert.DoesNotContain("GetModelHealthScoreAsync", names);
        Assert.DoesNotContain("GetQuantityTakeoffAsync", names);
    }
}
