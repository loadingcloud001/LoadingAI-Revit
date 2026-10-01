using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Analysis;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using LoadingAI.Core.Openings;
using LoadingAI.Llm;
using LoadingAI.Revit.Formwork;
using LoadingAI.Revit.Openings;
using LoadingAI.Revit.StructureCuts;
using LoadingAI.Tools;

namespace LoadingAI.Revit;

/// <summary>
/// Cross-slice workflows plus the shared private helpers: schedules and views, host collection, structure splits, formwork, openings, and the resolve/colour/print utilities they share.
/// Implements <c>IRevitBridge</c>. Part of <see cref="AdapterRevitBridge"/>; see that type for the contract.
/// </summary>
public sealed partial class AdapterRevitBridge
{
    public Task<PurgeUnusedResultDto> PurgeUnusedAsync(bool preview = true, CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null)
                return new PurgeUnusedResultDto(preview, 0, 0, 0, Array.Empty<string>(), "Please open a model first.");
            try
            {
                var unused = doc.GetUnusedElements(new HashSet<ElementId>());
                if (unused == null || unused.Count == 0)
                    return new PurgeUnusedResultDto(preview, 0, 0, 0, Array.Empty<string>());
                var count = unused.Count;

                if (preview)
                {
                    var sample = unused.Take(15).Select(id =>
                    {
                        var el = doc.GetElement(id);
                        if (el == null) return id.ToString();
                        return $"{el.Category?.Name ?? "(no category)"}/{el.Name} ({id})";
                    }).ToList();
                    return new PurgeUnusedResultDto(true, count, 0, 0, sample);
                }

                int deleted = 0, passes = 0;
                using var tx = new Transaction(doc, "LoadingAI for Revit: purge unused");
                tx.Start();
                try
                {
                    for (var i = 0; i < 8; i++)
                    {
                        var batch = doc.GetUnusedElements(new HashSet<ElementId>());
                        if (batch == null || batch.Count == 0) break;
                        doc.Delete(batch);
                        deleted += batch.Count;
                        passes++;
                    }
                    tx.Commit();
                }
                catch (Exception inner)
                {
                    try { tx.RollBack(); } catch { }
                    return new PurgeUnusedResultDto(preview, count, 0, passes, Array.Empty<string>(), $"Failed: {inner.Message}");
                }
                return new PurgeUnusedResultDto(false, count, deleted, passes, Array.Empty<string>());
            }
            catch (Exception ex)
            {
                return new PurgeUnusedResultDto(preview, 0, 0, 0, Array.Empty<string>(), $"Failed: {ex.Message}");
            }
        }, ct);
    }

    public Task<ScheduleDataDto?> GetScheduleDataAsync(
        string? scheduleId, string? scheduleName, int rowLimit, CancellationToken ct = default)
    {
        // The row window is bounded here rather than by the caller so the cap holds for every slice consumer.
        if (rowLimit <= 0) rowLimit = DefaultScheduleRows;
        if (rowLimit > MaxScheduleRows) rowLimit = MaxScheduleRows;
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return null;
            try
            {
                var schedule = FindSchedule(doc, scheduleId, scheduleName);
                if (schedule == null)
                    return new ScheduleDataDto(string.Empty, string.Empty, 0, 0, 0,
                        Array.Empty<IReadOnlyList<string>>(), "Schedule not found. Pass scheduleId or name.");
                var body = schedule.GetTableData().GetSectionData(SectionType.Body);
                var rows = Math.Min(body.NumberOfRows, rowLimit);
                var cols = body.NumberOfColumns;
                var grid = new List<IReadOnlyList<string>>(rows);
                for (var r = 0; r < rows; r++)
                {
                    var cells = new List<string>(cols);
                    for (var c = 0; c < cols; c++)
                        cells.Add(schedule.GetCellText(SectionType.Body, r, c).Replace('\t', ' '));
                    grid.Add(cells);
                }
                return new ScheduleDataDto(schedule.Id.ToString(), schedule.Name, body.NumberOfRows, cols, rows, grid);
            }
            catch (Exception ex)
            {
                return new ScheduleDataDto(string.Empty, string.Empty, 0, 0, 0,
                    Array.Empty<IReadOnlyList<string>>(), $"Failed: {ex.Message}");
            }
        }, ct);
    }

    public Task<ViewRefDto?> CreateScheduleAsync(
        string name, IReadOnlyList<string> fields, CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return null;
            try
            {
                // The one token carries both frozen tool arguments: "<category>" when the caller
                // wants the conventional name, or "<category>:<schedule name>" when an explicit
                // schedule name was asked for.
                var token = name ?? string.Empty;
                var colon = token.IndexOf(':');
                var categoryToken = colon >= 0 ? token.Substring(0, colon) : token;
                var explicitName = colon >= 0 ? token.Substring(colon + 1).Trim() : null;
                var bic = ResolveScheduleCategory(categoryToken);
                if (bic == null)
                    return new ViewRefDto(string.Empty, string.Empty, string.Empty, $"Unknown category {categoryToken.Trim()}");
                var scheduleName = string.IsNullOrWhiteSpace(explicitName)
                    ? $"{categoryToken.Trim()} Schedule"
                    : explicitName!;
                using var tx = new Transaction(doc, "LoadingAI for Revit: create schedule");
                tx.Start();
                try
                {
                    var schedule = ViewSchedule.CreateSchedule(doc, new ElementId(bic.Value), ElementId.InvalidElementId);
                    schedule.Name = scheduleName;
                    var added = AddScheduleFields(schedule, fields);
                    tx.Commit();
                    return new ViewRefDto(schedule.Id.ToString(), schedule.Name,
                        $"Created schedule {schedule.Id} \"{schedule.Name}\" with {added} field(s).");
                }
                catch (Exception inner)
                {
                    try { tx.RollBack(); } catch { }
                    return new ViewRefDto(string.Empty, string.Empty, string.Empty, $"Failed: {inner.Message}");
                }
            }
            catch (Exception ex)
            {
                return new ViewRefDto(string.Empty, string.Empty, string.Empty, $"Failed: {ex.Message}");
            }
        }, ct);
    }

    public Task<ViewRefDto?> CreateFloorPlanAsync(
        string levelIdOrName, string name, CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return null;
            try
            {
                var typeId = FindViewFamily(doc, ViewFamily.FloorPlan);
                if (typeId == null) return new ViewRefDto(string.Empty, string.Empty, string.Empty, "No FloorPlan view type found.");
                var level = ResolveLevelToken(doc, levelIdOrName);
                if (level == null) return new ViewRefDto(string.Empty, string.Empty, string.Empty, "Level not found. Pass levelId or levelName.");
                using var tx = new Transaction(doc, "LoadingAI for Revit: create floor plan");
                tx.Start();
                try
                {
                    var view = ViewPlan.Create(doc, typeId, level.Id);
                    if (!string.IsNullOrWhiteSpace(name)) view.Name = name.Trim();
                    tx.Commit();
                    return new ViewRefDto(view.Id.ToString(), view.Name,
                        $"Created floor plan {view.Id} \"{view.Name}\" on {level.Name}");
                }
                catch (Exception inner)
                {
                    try { tx.RollBack(); } catch { }
                    return new ViewRefDto(string.Empty, string.Empty, string.Empty, $"Failed: {inner.Message}");
                }
            }
            catch (Exception ex)
            {
                return new ViewRefDto(string.Empty, string.Empty, string.Empty, $"Failed: {ex.Message}");
            }
        }, ct);
    }

    public Task<ViewRefDto?> Create3DViewAsync(
        string name, bool isometric, CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return null;
            try
            {
                var typeId = FindViewFamily(doc, ViewFamily.ThreeDimensional);
                if (typeId == null) return new ViewRefDto(string.Empty, string.Empty, string.Empty, "No 3D view type found.");
                using var tx = new Transaction(doc, "LoadingAI for Revit: create 3D view");
                tx.Start();
                try
                {
                    var view = isometric ? View3D.CreateIsometric(doc, typeId) : View3D.CreatePerspective(doc, typeId);
                    if (!string.IsNullOrWhiteSpace(name)) view.Name = name.Trim();
                    tx.Commit();
                    return new ViewRefDto(view.Id.ToString(), view.Name,
                        $"Created 3D view {view.Id} \"{view.Name}\"");
                }
                catch (Exception inner)
                {
                    try { tx.RollBack(); } catch { }
                    return new ViewRefDto(string.Empty, string.Empty, string.Empty, $"Failed: {inner.Message}");
                }
            }
            catch (Exception ex)
            {
                return new ViewRefDto(string.Empty, string.Empty, string.Empty, $"Failed: {ex.Message}");
            }
        }, ct);
    }

    public Task<ViewRefDto?> CreateSectionViewAsync(
        string name, PointMmDto min, PointMmDto max, string? viewDirection, CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return null;
            try
            {
                var a = new XYZ(Metric.MmToFt(min.X), Metric.MmToFt(min.Y), Metric.MmToFt(min.Z));
                var b = new XYZ(Metric.MmToFt(max.X), Metric.MmToFt(max.Y), Metric.MmToFt(max.Z));
                var typeId = FindViewFamily(doc, ViewFamily.Section);
                if (typeId == null) return new ViewRefDto(string.Empty, string.Empty, string.Empty, "No Section view type found.");
                var box = SectionBox(a, b, SectionHeight, SectionDepth, viewDirection);
                using var tx = new Transaction(doc, "LoadingAI for Revit: create section");
                tx.Start();
                try
                {
                    var view = ViewSection.CreateSection(doc, typeId, box);
                    if (!string.IsNullOrWhiteSpace(name)) view.Name = name.Trim();
                    tx.Commit();
                    return new ViewRefDto(view.Id.ToString(), view.Name,
                        $"Created section {view.Id} \"{view.Name}\"");
                }
                catch (Exception inner)
                {
                    try { tx.RollBack(); } catch { }
                    return new ViewRefDto(string.Empty, string.Empty, string.Empty, $"Failed: {inner.Message}");
                }
            }
            catch (Exception ex)
            {
                return new ViewRefDto(string.Empty, string.Empty, string.Empty, $"Failed: {ex.Message}");
            }
        }, ct);
    }

    public Task<PathOfTravelResultDto?> CreatePathOfTravelAsync(
        string viewIdOrName, IReadOnlyList<PathPairDto> pairs, CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null) return null;
            var wanted = pairs?.Count ?? 0;
            PathOfTravelResultDto Fail(string message) =>
                new PathOfTravelResultDto(string.Empty, wanted, 0, Array.Empty<string>(), Array.Empty<string>(), message);
            try
            {
                var view = ResolveViewPlan(doc, viewIdOrName);
                if (view == null)
                    return Fail("path_of_travel requires a floor plan view. Pass viewId of a ViewPlan.");
                if (pairs == null || pairs.Count == 0)
                    return Fail("Pass startX/startY/endX/endY or starts/ends arrays.");
                // The pair cap is enforced here rather than by the caller so it holds for every slice consumer.
                if (pairs.Count > MaxPathOfTravelPairs)
                    return Fail($"Max {MaxPathOfTravelPairs} path pairs per call.");

                var starts = new List<XYZ>(pairs.Count);
                var ends = new List<XYZ>(pairs.Count);
                foreach (var pair in pairs)
                {
                    starts.Add(new XYZ(Metric.MmToFt(pair.Start.X), Metric.MmToFt(pair.Start.Y), Metric.MmToFt(pair.Start.Z)));
                    ends.Add(new XYZ(Metric.MmToFt(pair.End.X), Metric.MmToFt(pair.End.Y), Metric.MmToFt(pair.End.Z)));
                }

                using var tx = new Transaction(doc, "LoadingAI for Revit: path of travel");
                tx.Start();
                try
                {
                    var paths = PathOfTravel.CreateMultiple(view, starts, ends, out IList<PathOfTravelCalculationStatus> statuses);
                    tx.Commit();
                    // CreateMultiple returns null — and can carry null slots — when no path was found
                    // (points too close, crop box exclusion, no route). Walk the request, not the
                    // result, so every unsuccessful pair is reported instead of throwing.
                    var ids = new List<string>();
                    var failures = new List<string>();
                    for (var i = 0; i < starts.Count; i++)
                    {
                        var path = paths != null && i < paths.Count ? paths[i] : null;
                        if (path != null)
                        {
                            ids.Add(path.Id.ToString());
                            continue;
                        }
                        failures.Add(statuses != null && i < statuses.Count
                            ? $"#{i}:{statuses[i]}"
                            : $"#{i}:No path found");
                    }
                    return new PathOfTravelResultDto(view.Name, starts.Count, ids.Count, ids, failures);
                }
                catch (Exception inner)
                {
                    try { tx.RollBack(); } catch { }
                    return Fail($"Failed: {inner.Message}");
                }
            }
            catch (Exception ex)
            {
                return Fail($"Failed: {ex.Message}");
            }
        }, ct);
    }

    /// <summary>
    /// One page of host elements for the structure-cuts algorithms. The page is capped
    /// Adapter-side so the bound holds for every caller, and <c>Total</c> is the size of
    /// the whole collection so a caller can ask for the next page without re-collecting.
    /// </summary>
    public Task<ElementIdListDto?> CollectHostsAsync(
        string scope, IReadOnlyList<string>? categories, int limit, int offset, CancellationToken ct = default)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var uidoc = uiApp.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (uidoc == null || doc == null)
                return new ElementIdListDto(Array.Empty<string>(), offset, 0, "Please open a model first.");

            var cats = categories ?? Array.Empty<string>();
            var cap = Math.Max(1, limit);
            var start = Math.Max(0, offset);
            var wanted = SplitElements.HostsIn(cats);
            var collected = SplitScope.ByScope(doc, uidoc, wanted, scope);

            if (start > collected.Count)
                start = collected.Count;

            var ids = collected
                .Skip(start)
                .Take(cap)
                .Select(element => SplitElements.Fmt(element.Id))
                .ToList();
            return new ElementIdListDto(ids, start, collected.Count);
        }, ct);
    }

    public Task<SplitResultDto?> SplitBeamsByColumnsAsync(SplitRequestDto request, CancellationToken ct = default)
        => RunSplit(request, ct, (uiApp, options, outcome) => BeamSplit.Run(
            uiApp,
            request.HostIds ?? Array.Empty<string>(),
            request.Cutters ?? Array.Empty<string>(),
            options,
            outcome));

    public Task<SplitResultDto?> SplitSecondaryBeamsByMainBeamsAsync(SplitRequestDto request, CancellationToken ct = default)
        => RunSplit(request, ct, (uiApp, options, outcome) => BeamCut.Run(
            uiApp,
            request.HostIds ?? Array.Empty<string>(),
            request.Cutters ?? Array.Empty<string>(),
            options,
            outcome));

    public Task<SplitResultDto?> SplitFloorsByBeamsAsync(SplitRequestDto request, CancellationToken ct = default)
        => RunSplit(request, ct, (uiApp, options, outcome) =>
        {
            // The floor cut takes beams and columns as two lists while the DTO carries one
            // Cutters list. SplitElements.Resolve filters by category, so handing the same
            // list to both parameters separates them without inventing an encoding.
            var cutters = request.Cutters ?? Array.Empty<string>();
            return FloorSplit.Run(
                uiApp,
                request.HostIds ?? Array.Empty<string>(),
                cutters,
                cutters,
                options,
                outcome);
        });

    public Task<SplitResultDto?> SplitByLevelsAsync(SplitRequestDto request, CancellationToken ct = default)
        => RunSplit(request, ct, (uiApp, options, outcome) => LevelSplit.Run(
            uiApp,
            request.HostIds ?? Array.Empty<string>(),
            request.Cutters ?? Array.Empty<string>(),
            request.Categories ?? Array.Empty<string>(),
            options,
            outcome));

    public Task<SplitResultDto?> SplitByGridsAsync(SplitRequestDto request, CancellationToken ct = default)
        => RunSplit(request, ct, (uiApp, options, outcome) => GridSplit.Run(
            uiApp,
            request.HostIds ?? Array.Empty<string>(),
            request.Cutters ?? Array.Empty<string>(),
            request.Categories ?? Array.Empty<string>(),
            options,
            outcome));

    /// <summary>
    /// Shared shape of the four cuts: one transaction per call, the algorithms' own prose in
    /// <c>Report</c>, and the counters captured out of band so a partial run stays legible.
    /// </summary>
    private Task<SplitResultDto?> RunSplit(
        SplitRequestDto request,
        CancellationToken ct,
        Func<UIApplication, SplitOptions, SplitOutcome, string> run)
    {
        return _bridge.RunAsync(uiApp =>
        {
            var doc = uiApp.ActiveUIDocument?.Document;
            if (doc == null)
                return new SplitResultDto("Please open a model first.", 0, 0, 0, Array.Empty<string>());

            var options = new SplitOptions
            {
                Limit = request.Limit,
                DryRun = request.DryRun,
                UniqueMark = request.UniqueMark,
                CopyParams = request.CopyParams,
                Scope = request.Scope,
                ContinueFrom = request.ContinueFrom,
                IncludeLinks = request.IncludeLinks,
            };
            var outcome = new SplitOutcome();
            try
            {
                var report = run(uiApp, options, outcome);
                return new SplitResultDto(report, outcome.Created, outcome.Openings, outcome.Skipped, outcome.NewIds);
            }
            catch (Exception ex)
            {
                return new SplitResultDto($"Failed: {ex.Message}", 0, 0, 0, Array.Empty<string>());
            }
        }, ct);
    }

    public Task<FormworkSetupResultDto?> FormworkSetupAsync(FormworkSetupRequestDto request, CancellationToken ct = default)
        => RunFormwork(uiApp => FormworkSetup.Run(uiApp), r => new FormworkSetupResultDto(r), null, ct);

    public Task<FormworkPanelsResultDto?> CreateFormworkPanelsAsync(FormworkPanelsRequestDto request, CancellationToken ct = default)
        => RunFormwork(
            uiApp => FormworkPanels.Run(
                uiApp,
                request.Ids ?? Array.Empty<string>(),
                request.ThicknessMm,
                request.FaceTypes ?? Array.Empty<string>(),
                request.Collision,
                request.DeleteExisting,
                request.IncludeUnselectedLinks),
            r => new FormworkPanelsResultDto(r),
            FormworkPanels.GenerateTimeout,
            ct);

    public Task<CleanupResultDto?> CleanupFormworkAsync(CleanupRequestDto request, CancellationToken ct = default)
        => RunFormwork(
            uiApp => FormworkCleanup.Run(uiApp, request.Action, request.FaceType),
            r => new CleanupResultDto(r),
            null,
            ct);

    public Task<CopyParamsResultDto?> CopyFormworkParentParamsAsync(CopyParamsRequestDto request, CancellationToken ct = default)
        => RunFormwork(
            uiApp => FormworkParentParams.Run(
                uiApp,
                request.Ids ?? Array.Empty<string>(),
                request.Names ?? Array.Empty<string>()),
            r => new CopyParamsResultDto(r),
            null,
            ct);

    public Task<OpeningPlanResultDto?> OpeningProbeClashAsync(OpeningClashRequestDto request, CancellationToken ct = default)
        => RunOpening(
            uiApp =>
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                var uidoc = uiApp.ActiveUIDocument;
                if (doc == null || uidoc == null)
                    return new OpeningPlanResultDto("Please open a model first.", 0, 0, 0, Array.Empty<OpeningPlanHitDto>());
                try
                {
                    return PlaceOpenings.Plan(
                        doc, uidoc,
                        request.MepIds, request.HostIds, request.Shape, request.ClearanceMm,
                        request.LevelName, request.Limit);
                }
                catch (Exception ex)
                {
                    return new OpeningPlanResultDto($"Failed: {ex.Message}", 0, 0, 0, Array.Empty<OpeningPlanHitDto>(), ex.Message);
                }
            },
            PlaceOpenings.ClashTimeout,
            ct);

    public Task<OpeningPlaceResultDto?> PlaceOpeningsAsync(OpeningClashRequestDto request, CancellationToken ct = default)
        => RunOpening(
            uiApp =>
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                var uidoc = uiApp.ActiveUIDocument;
                if (doc == null || uidoc == null)
                    return new OpeningPlaceResultDto("Please open a model first.", 0, 0, 0, 0, 0, Array.Empty<string>(), Array.Empty<string>());
                if (request.DryRun)
                {
                    var plan = PlaceOpenings.Plan(
                        doc, uidoc, request.MepIds, request.HostIds, request.Shape,
                        request.ClearanceMm, request.LevelName, request.Limit);
                    return new OpeningPlaceResultDto(
                        plan.Report, 0, 0, 0, 0, 0, Array.Empty<string>(), Array.Empty<string>(), plan.Error);
                }
                try
                {
                    var work = PlaceOpenings.Run(
                        doc, uidoc, request.MepIds, request.HostIds, request.Shape,
                        request.ClearanceMm, request.FamilyName, request.LevelName, request.Limit);
                    return OpeningOutcomeMapper.ToPlaceDto(work);
                }
                catch (Exception ex)
                {
                    return new OpeningPlaceResultDto($"Failed: {ex.Message}", 0, 0, 0, 0, 0, Array.Empty<string>(), Array.Empty<string>(), ex.Message);
                }
            },
            PlaceOpenings.ClashTimeout,
            ct);

    public Task<OpeningCutResultDto?> CutHostsByOpeningsAsync(OpeningCutRequestDto request, CancellationToken ct = default)
        => RunOpening(
            uiApp =>
            {
                var doc = uiApp.ActiveUIDocument?.Document;
                var uidoc = uiApp.ActiveUIDocument;
                if (doc == null || uidoc == null)
                    return new OpeningCutResultDto("Please open a model first.", 0, 0, 0, 0);
                try
                {
                    var work = CutLocal.Run(doc, uidoc, request.OpeningIds, request.HostIds);
                    return OpeningOutcomeMapper.ToCutDto(work);
                }
                catch (Exception ex)
                {
                    return new OpeningCutResultDto($"Failed: {ex.Message}", 0, 0, 0, 0, ex.Message);
                }
            },
            null,
            ct);

    private async Task<TDto?> RunOpening<TDto>(
        Func<UIApplication, TDto> run,
        TimeSpan? timeout,
        CancellationToken ct) where TDto : class
    {
        var dto = timeout is { } budget
            ? await _bridge.RunAsync(run, budget, ct)
            : await _bridge.RunAsync(run, ct);
        return dto;
    }

    /// <summary>
    /// Shared shape of the four formwork tools. The host entry points already return their own
    /// prose and absorb their own exceptions, so this only picks the timeout and lifts the
    /// string into the slice's result DTO. <c>null</c> means the bridge was cancelled before
    /// it ran, which the slices surface as <c>null</c> rather than a made-up report.
    /// </summary>
    private async Task<TDto?> RunFormwork<TDto>(
        Func<UIApplication, string> run,
        Func<string, TDto> project,
        TimeSpan? timeout,
        CancellationToken ct)
    {
        var report = timeout is { } budget
            ? await _bridge.RunAsync(run, budget, ct)
            : await _bridge.RunAsync(run, ct);
        return report == null ? default : project(report);
    }

    /// <summary>Body rows shown when a caller passes no usable <c>rowLimit</c>.</summary>
    private const int DefaultScheduleRows = 25;

    /// <summary>Hard ceiling on schedule body rows returned in one call.</summary>
    private const int MaxScheduleRows = 40;

    /// <summary>Hard ceiling on path-of-travel pairs accepted in one call.</summary>
    private const int MaxPathOfTravelPairs = 5;

    /// <summary>Section box height used when a caller does not describe one (3000 mm).</summary>
    private static readonly double SectionHeight = Metric.MmToFt(3000);

    /// <summary>Section box depth used when a caller does not describe one (2500 mm).</summary>
    private static readonly double SectionDepth = Metric.MmToFt(2500);

    private static int AddScheduleFields(ViewSchedule schedule, IReadOnlyList<string>? wanted)
    {
        var focus = wanted ?? Array.Empty<string>();
        var added = 0;
        foreach (var sf in schedule.Definition.GetSchedulableFields())
        {
            if (sf.FieldType != ScheduleFieldType.Instance && sf.FieldType != ScheduleFieldType.ElementType)
                continue;
            if (sf.ParameterId.Value == (long)BuiltInParameter.ALL_MODEL_MARK)
                continue;
            var fname = sf.GetName(schedule.Document);
            if (focus.Count > 0)
            {
                if (!focus.Any(w => fname.Equals(w, StringComparison.OrdinalIgnoreCase)))
                    continue;
            }
            else if (added >= 8) break;
            schedule.Definition.AddField(sf);
            added++;
        }
        return added;
    }

    private static ViewSchedule? FindSchedule(Document doc, string? scheduleId, string? scheduleName)
    {
        if (!string.IsNullOrWhiteSpace(scheduleId))
            return TryGetElement(doc, scheduleId) as ViewSchedule;
        if (string.IsNullOrWhiteSpace(scheduleName)) return null;
        return new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
            .FirstOrDefault(s => s.Name.Equals(scheduleName!.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Resolves a level from an element id or a level name; blank picks the lowest level.</summary>
    private static Level? ResolveLevelToken(Document doc, string? idOrName)
    {
        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>();
        if (string.IsNullOrWhiteSpace(idOrName)) return levels.OrderBy(l => l.Elevation).FirstOrDefault();
        var token = idOrName!.Trim();
        if (TryGetElement(doc, token) is Level byId) return byId;
        return levels.FirstOrDefault(l => l.Name.Equals(token, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Resolves a floor plan from a view id or name; blank uses the active view.</summary>
    private static ViewPlan? ResolveViewPlan(Document doc, string? idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName)) return doc.ActiveView as ViewPlan;
        var token = idOrName!.Trim();
        if (TryGetElement(doc, token) is ViewPlan byId) return byId;
        return new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
            .FirstOrDefault(v => v.Name.Equals(token, StringComparison.OrdinalIgnoreCase));
    }

    private static Element? TryGetElement(Document doc, string? idStr)
    {
        if (string.IsNullOrWhiteSpace(idStr)) return null;
        try { return doc.GetElement(AllTools.ParseEid(idStr)); }
        catch { return null; }
    }

    private static ElementId? FindViewFamily(Document doc, ViewFamily family)
        => new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
            .FirstOrDefault(t => t.ViewFamily == family)?.Id;

    /// <summary>
    /// Reads the category out of a create_schedule token: the whole token first, then its
    /// leading words, so "Door List" still resolves to the Door category.
    /// </summary>
    private static BuiltInCategory? ResolveScheduleCategory(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var words = token!.Trim().Split(' ');
        for (var n = words.Length; n >= 1; n--)
        {
            var candidate = AllTools.ParseCategory(string.Join(" ", words.Take(n)));
            if (candidate != null) return candidate;
        }
        return null;
    }

    /// <summary>Axis token (x / -y / z) for a section view direction; null when absent or unrecognised.</summary>
    private static XYZ? ParseViewDirection(string? direction)
    {
        if (string.IsNullOrWhiteSpace(direction)) return null;
        var token = direction!.Trim().ToLowerInvariant();
        var negative = token.StartsWith("-", StringComparison.Ordinal);
        if (negative) token = token.Substring(1).Trim();
        if (token.StartsWith("+", StringComparison.Ordinal)) token = token.Substring(1).Trim();
        XYZ axis;
        switch (token)
        {
            case "x": case "east": case "right": axis = XYZ.BasisX; break;
            case "y": case "north": case "up": axis = XYZ.BasisY; break;
            case "z": axis = XYZ.BasisZ; break;
            default: return null;
        }
        return negative ? axis.Negate() : axis;
    }

    private static BoundingBoxXYZ SectionBox(XYZ a, XYZ b, double height, double depth, string? viewDirection)
    {
        var along = b - a;
        if (along.GetLength() < 1e-6) throw new InvalidOperationException("Section line is too short.");
        var viewDir = ParseViewDirection(viewDirection) ?? along.Normalize();
        var up = XYZ.BasisZ;
        var right = up.CrossProduct(viewDir);
        if (right.GetLength() < 1e-6)
        {
            up = XYZ.BasisY;
            right = up.CrossProduct(viewDir);
        }
        right = right.Normalize();
        up = viewDir.CrossProduct(right).Normalize();
        var t = Transform.Identity;
        t.Origin = (a + b) / 2;
        t.set_Basis(0, right);
        t.set_Basis(1, up);
        t.set_Basis(2, viewDir);
        var halfW = Math.Max(along.GetLength() / 2, 1);
        var halfH = Math.Max(height / 2, 1);
        var halfD = Math.Max(depth / 2, 0.5);
        return new BoundingBoxXYZ
        {
            Enabled = true,
            Min = new XYZ(-halfW, -halfH, -halfD),
            Max = new XYZ(halfW, halfH, halfD),
            Transform = t
        };
    }

    private static List<ElementId> ResolvePrintable(Document doc, IReadOnlyList<string>? ids, bool preferSheets)
    {
        var result = new List<ElementId>();
        if (ids != null && ids.Count > 0)
        {
            foreach (var s in ids)
            {
                try
                {
                    if (doc.GetElement(AllTools.ParseEid(s)) is View view && IsPrintable(view))
                        result.Add(view.Id);
                }
                catch { }
            }
            return result;
        }

        if (preferSheets)
        {
            result.AddRange(new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(s => !s.IsPlaceholder && IsPrintable(s))
                .Select(s => s.Id));
            if (result.Count > 0) return result;
        }

        var active = doc.ActiveView;
        if (active != null && IsPrintable(active)) result.Add(active.Id);
        return result;
    }

    private static bool IsPrintable(View view) => !view.IsTemplate && view.CanBePrinted;

    private static Level? ResolveLevel(Document doc, string? levelId, string? levelName)
    {
        if (!string.IsNullOrWhiteSpace(levelId))
            return doc.GetElement(AllTools.ParseEid(levelId)) as Level;
        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>();
        if (!string.IsNullOrWhiteSpace(levelName))
            return levels.FirstOrDefault(l => l.Name.Equals(levelName, StringComparison.OrdinalIgnoreCase));
        return levels.OrderBy(l => l.Elevation).FirstOrDefault();
    }

    private static Color ParseColor(string s)
    {
        s = s.Trim().ToLowerInvariant();
        return s switch
        {
            "red" => new Color(255, 0, 0),
            "green" => new Color(0, 200, 0),
            "blue" => new Color(0, 0, 255),
            "yellow" => new Color(255, 255, 0),
            "orange" => new Color(255, 165, 0),
            "purple" => new Color(128, 0, 128),
            _ when s.StartsWith("#") && s.Length == 7
                   && int.TryParse(s.Substring(1, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)
                   && int.TryParse(s.Substring(3, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
                   && int.TryParse(s.Substring(5, 2), System.Globalization.NumberStyles.HexNumber, null, out var b)
                => new Color((byte)r, (byte)g, (byte)b),
            _ => new Color(255, 0, 0)
        };
    }

    private static string ReadParam(Element el, string paramName)
    {
        var p = el.LookupParameter(paramName);
        if (p == null)
        {
            foreach (Parameter q in el.Parameters)
            {
                if (q?.Definition?.Name != null && q.Definition.Name.Equals(paramName, StringComparison.OrdinalIgnoreCase))
                { p = q; break; }
            }
        }
        if (p == null) return "(none)";
        var vs = p.AsValueString();
        if (!string.IsNullOrWhiteSpace(vs)) return vs.Trim();
        if (p.StorageType == StorageType.String) return string.IsNullOrWhiteSpace(p.AsString()) ? "(empty)" : p.AsString()!.Trim();
        if (p.StorageType == StorageType.Integer) return p.AsInteger().ToString();
        if (p.StorageType == StorageType.Double) return p.AsDouble().ToString("G4");
        if (p.StorageType == StorageType.ElementId)
        {
            var id = p.AsElementId();
            if (id == ElementId.InvalidElementId) return "(none)";
            return el.Document.GetElement(id)?.Name ?? id.ToString();
        }
        return "(none)";
    }

    private static List<Color> GeneratePalette(int n, string palette)
    {
        palette = palette?.ToLowerInvariant() ?? "rainbow";
        string[] hexRainbow =
        {
            "#1565C0", "#1E88E5", "#039BE5", "#00ACC1", "#00897B", "#43A047", "#7CB342", "#FBC02D", "#FFA000", "#FB8C00",
            "#F4511E", "#E53935", "#D32F2F", "#C2185B", "#7B1FA2", "#512DA8", "#303F9F", "#455A64", "#FF6F00", "#FF4081"
        };
        var list = new List<Color>();
        for (int i = 0; i < n; i++)
        {
            string hex = palette == "heat"
                ? InterpolateHeat((double)i / Math.Max(1, n - 1))
                : hexRainbow[i % hexRainbow.Length];
            list.Add(HexToColor(hex));
        }
        return list;
    }

    private static string InterpolateHeat(double t)
    {
        if (t < 0.5)
        {
            int r = (int)(255 * t * 2);
            int g = (int)(255 * t * 2);
            int b = 255;
            return $"#{r:X2}{g:X2}{b:X2}";
        }
        int rr = 255;
        int gg = (int)(255 * (1 - t) * 2);
        int bb = (int)(255 * (1 - t) * 2);
        return $"#{rr:X2}{gg:X2}{bb:X2}";
    }

    private static Color HexToColor(string hex)
    {
        hex = hex.Trim().TrimStart('#');
        if (hex.Length == 6
            && int.TryParse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)
            && int.TryParse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
            && int.TryParse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
            return new Color((byte)r, (byte)g, (byte)b);
        return new Color(255, 0, 0);
    }
}
