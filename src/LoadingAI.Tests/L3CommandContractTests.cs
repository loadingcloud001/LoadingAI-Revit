using System;
using System.Linq;
using LoadingAI.Revit;

namespace LoadingAI.Tests;

public class L3CommandContractTests
{
    [Fact]
    public void IRevitElementCommand_DeclaresPhase5L3Methods()
    {
        var names = typeof(IRevitElementCommand).GetMethods().Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("DeleteElementsAsync", names);
        Assert.Contains("MoveElementsAsync", names);
        Assert.DoesNotContain("IsolateInViewAsync", names);
        Assert.DoesNotContain("SetParameterAsync", names);
    }

    [Fact]
    public void IRevitCreateCommand_DeclaresCreateMethods()
    {
        var names = typeof(IRevitCreateCommand).GetMethods().Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("CreateWallAsync", names);
        Assert.Contains("CreateFamilyInstanceAsync", names);
        Assert.Contains("DuplicateElementAsync", names);
        Assert.Contains("CreateFloorByOutlineAsync", names);
        Assert.Contains("CreateRoofAsync", names);
        Assert.Contains("CreateGridLevelAsync", names);
        Assert.Contains("CreateFamilyInstanceArrayAsync", names);
        Assert.Contains("RotateElementsAsync", names);
        Assert.Contains("MirrorElementsAsync", names);
        Assert.Contains("SplitWallAsync", names);
        Assert.Contains("JoinGeometryAsync", names);
        Assert.DoesNotContain("DeleteElementsAsync", names);
        Assert.DoesNotContain("IsolateInViewAsync", names);
        Assert.DoesNotContain("SaveDocumentAsync", names);
        Assert.DoesNotContain("OverrideColorInViewAsync", names);
        Assert.DoesNotContain("ColorByConstructionScheduleAsync", names);
        Assert.DoesNotContain("ColorByParameterAsync", names);
        Assert.DoesNotContain("CreateSheetAsync", names);
        Assert.DoesNotContain("PlaceViewOnSheetAsync", names);
        Assert.DoesNotContain("DuplicateViewAsync", names);
        Assert.DoesNotContain("SetViewTemplateAsync", names);
        Assert.DoesNotContain("SetCropBoxAsync", names);
        Assert.DoesNotContain("CreateDimensionBetweenWallsAsync", names);
        Assert.DoesNotContain("TagAllInActiveViewAsync", names);
        Assert.DoesNotContain("RenameElementsAsync", names);
        Assert.DoesNotContain("ApplyConstructionDatesAsync", names);
        Assert.DoesNotContain("GetWorksetsAsync", names);
        Assert.DoesNotContain("GetClashByBoundingBoxAsync", names);
        Assert.DoesNotContain("ExportParamsAsync", names);
        Assert.DoesNotContain("ImportParamsAsync", names);
        Assert.DoesNotContain("CopyParamsAsync", names);
        Assert.DoesNotContain("ReplaceTextInParamAsync", names);
        Assert.DoesNotContain("NumberElementsAsync", names);
        Assert.DoesNotContain("ReloadLinksAsync", names);
        Assert.DoesNotContain("AssignWorksetAsync", names);
        Assert.DoesNotContain("GetQuantityTakeoffAsync", names);
    }

    [Fact]
    public void IRevitDocumentCommand_DeclaresSaveDocument()
    {
        var names = typeof(IRevitDocumentCommand).GetMethods().Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("SaveDocumentAsync", names);
        Assert.Contains("ReloadLinksAsync", names);
        Assert.Contains("AssignWorksetAsync", names);
        Assert.DoesNotContain("GetWarningsRawAsync", names);
        Assert.DoesNotContain("DeleteElementsAsync", names);
        Assert.DoesNotContain("CreateWallAsync", names);
        Assert.DoesNotContain("RenameElementsAsync", names);
        Assert.DoesNotContain("ApplyConstructionDatesAsync", names);
        Assert.DoesNotContain("GetWorksetsAsync", names);
        Assert.DoesNotContain("GetModelHealthScoreAsync", names);
        Assert.DoesNotContain("ExportParamsAsync", names);
        Assert.DoesNotContain("NumberElementsAsync", names);
        Assert.DoesNotContain("CreateSheetAsync", names);
        Assert.DoesNotContain("PlaceViewOnSheetAsync", names);
        Assert.DoesNotContain("TagAllInActiveViewAsync", names);
        Assert.DoesNotContain("GetQuantityTakeoffAsync", names);
    }
}
