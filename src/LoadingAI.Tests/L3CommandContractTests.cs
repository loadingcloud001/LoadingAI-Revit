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
        Assert.Contains("ChangeElementTypeAsync", names);
        Assert.Contains("PurgeUnusedAsync", names);
        Assert.DoesNotContain("ExportPdfAsync", names);
        Assert.DoesNotContain("ExportDwgAsync", names);
        Assert.DoesNotContain("ExportIfcAsync", names);
        Assert.DoesNotContain("IsolateInViewAsync", names);
        Assert.DoesNotContain("SetParameterAsync", names);
        Assert.DoesNotContain("LoadFamilyAsync", names);
        Assert.DoesNotContain("CreateTopographyAsync", names);
        Assert.DoesNotContain("SplitBeamsByColumnsAsync", names);
        Assert.DoesNotContain("SplitFloorsByBeamsAsync", names);
        Assert.DoesNotContain("SplitByLevelsAsync", names);
        Assert.DoesNotContain("SplitByGridsAsync", names);
        Assert.DoesNotContain("SplitSecondaryBeamsByMainBeamsAsync", names);
        Assert.DoesNotContain("FormworkSetupAsync", names);
        Assert.DoesNotContain("CreateFormworkPanelsAsync", names);
        Assert.DoesNotContain("CleanupFormworkAsync", names);
        Assert.DoesNotContain("CopyFormworkParentParamsAsync", names);
        Assert.DoesNotContain("CollectHostsAsync", names);
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
        Assert.Contains("CreateTopographyAsync", names);
        Assert.Contains("SplitBeamsByColumnsAsync", names);
        Assert.Contains("SplitFloorsByBeamsAsync", names);
        Assert.Contains("SplitByLevelsAsync", names);
        Assert.Contains("SplitByGridsAsync", names);
        Assert.Contains("SplitSecondaryBeamsByMainBeamsAsync", names);
        Assert.Contains("FormworkSetupAsync", names);
        Assert.Contains("CreateFormworkPanelsAsync", names);
        Assert.Contains("CleanupFormworkAsync", names);
        Assert.Contains("CopyFormworkParentParamsAsync", names);
        Assert.DoesNotContain("CollectHostsAsync", names);
        Assert.DoesNotContain("DeleteElementsAsync", names);
        Assert.DoesNotContain("LoadFamilyAsync", names);
        Assert.DoesNotContain("ChangeElementTypeAsync", names);
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
        Assert.Contains("LoadFamilyAsync", names);
        Assert.Contains("ExportPdfAsync", names);
        Assert.Contains("ExportDwgAsync", names);
        Assert.Contains("ExportIfcAsync", names);
        Assert.DoesNotContain("PurgeUnusedAsync", names);
        Assert.DoesNotContain("GetWarningsRawAsync", names);
        Assert.DoesNotContain("DeleteElementsAsync", names);
        Assert.DoesNotContain("ChangeElementTypeAsync", names);
        Assert.DoesNotContain("CreateTopographyAsync", names);
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
        Assert.DoesNotContain("CreateScheduleAsync", names);
        Assert.DoesNotContain("CreateFloorPlanAsync", names);
        Assert.DoesNotContain("Create3DViewAsync", names);
        Assert.DoesNotContain("CreateSectionViewAsync", names);
        Assert.DoesNotContain("CreatePathOfTravelAsync", names);
        Assert.DoesNotContain("GetScheduleDataAsync", names);
        Assert.DoesNotContain("SplitBeamsByColumnsAsync", names);
        Assert.DoesNotContain("SplitFloorsByBeamsAsync", names);
        Assert.DoesNotContain("SplitByLevelsAsync", names);
        Assert.DoesNotContain("SplitByGridsAsync", names);
        Assert.DoesNotContain("SplitSecondaryBeamsByMainBeamsAsync", names);
        Assert.DoesNotContain("FormworkSetupAsync", names);
        Assert.DoesNotContain("CreateFormworkPanelsAsync", names);
        Assert.DoesNotContain("CleanupFormworkAsync", names);
        Assert.DoesNotContain("CopyFormworkParentParamsAsync", names);
        Assert.DoesNotContain("CollectHostsAsync", names);
    }

    [Fact]
    public void IRevitViewCommand_DeclaresScheduleAndViewCreateMethods()
    {
        var names = typeof(IRevitViewCommand).GetMethods().Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("CreateScheduleAsync", names);
        Assert.Contains("CreateFloorPlanAsync", names);
        Assert.Contains("Create3DViewAsync", names);
        Assert.Contains("CreateSectionViewAsync", names);
        Assert.Contains("CreatePathOfTravelAsync", names);
        Assert.DoesNotContain("GetScheduleDataAsync", names);
        Assert.DoesNotContain("CreateWallAsync", names);
        Assert.DoesNotContain("SaveDocumentAsync", names);
        Assert.DoesNotContain("DeleteElementsAsync", names);
        Assert.DoesNotContain("SetParameterAsync", names);
        Assert.DoesNotContain("PurgeUnusedAsync", names);
        Assert.DoesNotContain("SplitBeamsByColumnsAsync", names);
        Assert.DoesNotContain("SplitFloorsByBeamsAsync", names);
        Assert.DoesNotContain("SplitByLevelsAsync", names);
        Assert.DoesNotContain("SplitByGridsAsync", names);
        Assert.DoesNotContain("SplitSecondaryBeamsByMainBeamsAsync", names);
        Assert.DoesNotContain("FormworkSetupAsync", names);
        Assert.DoesNotContain("CreateFormworkPanelsAsync", names);
        Assert.DoesNotContain("CleanupFormworkAsync", names);
        Assert.DoesNotContain("CopyFormworkParentParamsAsync", names);
        Assert.DoesNotContain("CollectHostsAsync", names);
    }

    [Fact]
    public void IRevitQuery_DeclaresScheduleReadMethod()
    {
        var names = typeof(IRevitQuery).GetMethods().Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("GetScheduleDataAsync", names);
        Assert.Contains("CollectHostsAsync", names);
        Assert.DoesNotContain("SplitBeamsByColumnsAsync", names);
        Assert.DoesNotContain("SplitFloorsByBeamsAsync", names);
        Assert.DoesNotContain("SplitByLevelsAsync", names);
        Assert.DoesNotContain("SplitByGridsAsync", names);
        Assert.DoesNotContain("SplitSecondaryBeamsByMainBeamsAsync", names);
        Assert.DoesNotContain("FormworkSetupAsync", names);
        Assert.DoesNotContain("CreateFormworkPanelsAsync", names);
        Assert.DoesNotContain("CleanupFormworkAsync", names);
        Assert.DoesNotContain("CopyFormworkParentParamsAsync", names);
        Assert.DoesNotContain("CreateScheduleAsync", names);
        Assert.DoesNotContain("CreateFloorPlanAsync", names);
        Assert.DoesNotContain("Create3DViewAsync", names);
        Assert.DoesNotContain("CreateSectionViewAsync", names);
        Assert.DoesNotContain("CreatePathOfTravelAsync", names);
        Assert.DoesNotContain("ExportPdfAsync", names);
        Assert.DoesNotContain("DeleteElementsAsync", names);
    }
}
