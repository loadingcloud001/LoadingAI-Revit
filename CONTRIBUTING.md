# Contributing — MIT Opensource Plugin Shelf

All code is MIT. No billing, no closed-source plugins.

## How to add a plugin

1. Fork the repo.
2. Create a folder `Plugins/<your-id>/` (plugin.json + csproj + C#). Do not put plugins under `src/LoadingAI.Plugins.*`.
3. Implement `IRevitToolPlugin` (see `src/LoadingAI.Core/Plugins/IRevitToolPlugin.cs`). Prefer `context.Bridge` (typed DTOs, no RevitAPI). Implement the empty `IRevitFullAccess` marker only if you need raw `UIApplication` (geometry / export / family).
   ```csharp
   public class HelloPlugin : IRevitToolPlugin {
     public string Id => "hello";
     public string Name => "Hello";
     public string Version => "0.1.0";
     public IEnumerable<ToolEntry> CreateTools(IPluginContext context)
       => new[] { new ToolEntry{ Definition = ..., Handler = async json => "hi", Tier = ToolTier.L1 } };
   }
   ```
   ProjectReference `src/LoadingAI.Core` — never `src/LoadingAI` (the host). FullAccess plugins also reference `src/LoadingAI.RevitUtil` and Nice3point RevitAPI packages (`Private=false`, `ExcludeAssets=runtime`).
   Optional: `context.Progress.Report("Exporting", 40)` for determinate percent. The host already shows a progress bar around every tool, so this is optional.
   Optional: implement `IPluginLifecycle` (`IsBusy`) so the host can wait before ALC unload (Revit 2025+).
   Optional: set `ToolEntry.Presets` so the host shows a chat form (string / number / boolean / select / pick-elements / pick-box) before execute. Leave it empty (the default) to keep today’s path. Confirm merges form values over the LLM args; L3 still shows the native `Confirm L3:` TaskDialog. Do not call `PickObjects` yourself — declare `PresetFieldKind.PickElements` or `PickBox` and the host owns the pick.
   ```csharp
   new ToolEntry {
     Definition = ..., Handler = ..., Tier = ToolTier.L2,
     Presets = new[] {
       new PresetField { Id = "offsetMm", Label = "Offset (mm)", Kind = PresetFieldKind.Number, Required = true, Default = 20 },
       new PresetField { Id = "elementIds", Label = "Elements", Kind = PresetFieldKind.PickElements, Required = true, Category = "Walls" }
     }
   }
   ```
4. Add `plugin.json` next to your DLL when shipping externally:
   ```json
   {"id":"hello","name":"Hello","version":"0.1.0","license":"MIT","author":"You","entryAssembly":"LoadingAI.Plugins.Hello.dll","tools":[{"name":"hello","tier":"L1","description":"...","enabledByDefault":true}],"verified":false}
   ```
5. PR to `main` with `Plugins/README.md` entry. Maintainer reviews for Revit API correctness (no wrong-thread API, Transaction required, no hard crash). Merged plugins become `verified:true`.

## Local dev without PR

Drop `<PluginId>/plugin.json + *.dll` into `%AppData%\LoadingAI\Plugins\` (or `src/LoadingAI/Plugins/dev/` for dev build). Restart Revit. **Settings** shows it as **Unverified** — works locally, not distributed.

## Rules

- L3 (delete/move/create/apply_construction_dates) must be `enabledByDefault:false`.
- All `RevitBridge.RunAsync` calls must stay on ExternalEvent; no direct `Document` access off UI thread.
- Temporary view ops (`Isolate/Hide/Override`) must be inside `Transaction` (Revit 2027 requirement).
- Keep `Tool` description concise and English.
- Bridge-first: L1 query and L2 view/param tools use `context.Bridge`. Do not call `FullAccess` unless the op is on the exception list (geometry / export / family).

## Test pyramid

Hosted GitHub Actions **cannot start Revit.exe**. Split coverage:

| Layer | Where | What it proves | Runner |
|---|---|---|---|
| L0 | `src/LoadingAI.Tests` (xUnit) | DTO roundtrips, catalog/source contracts | GitHub-hosted `windows-latest` |
| L1 | `src/LoadingAI.Tests` + `Fakes/FakeRevitBridge` | Tool handlers vs `IRevitBridge` (no-doc, empty selection, success text) | Same hosted CI |
| L2–L3 | `src/LoadingAI.RevitTests` (NUnit) | Live document, Adapter + `AttachCurrentThread`, Discover core tools, one rollback | **Local** Revit 2027 / future self-hosted runner |

CI (`.github/workflows/ci.yml`) already runs `dotnet test src/LoadingAI.Tests/...` only. `LoadingAI.RevitTests` has `IsTestProject=false` unless `-p:ForceRevitTests=true`, so `dotnet test LoadingAI.sln` will not launch Revit.

Do **not** call `PluginLoader.Discover` from Fake tests — `ToolGuards.ApplyConfirm` shows a TaskDialog. Call `QueryTools` / `ViewTools` / `ParamTools` `Create*` (or `AllTools.CreateAll`) with `FakePluginContext`.

In-Revit tests must call `RevitBridge.AttachCurrentThread(uiApp)` before awaiting Adapter/`RunAsync`. ricaun occupies the UI thread; the ExternalEvent path deadlocks there.

`src/LoadingAI.RevitTests/revit.runsettings` pins `NUnit.Version=2027` + `Open`/`Close`. ricaun finds `Revit.exe` from Autodesk registry — a non-default Program Files volume is fine. Do **not** commit a machine-specific `Revit.exe` path; `NUnit.Application` is ricaun's console, not Revit. Override with `RICAUN_REVITTEST_TESTADAPTER_NUNIT_*` env vars. Do not commit `.rvt` files.

Local CopyAddin (no `SkipCopyAddin`) also runs `scripts/trust-addin.ps1` so Revit does not ask you to Accept / Always Load after every rebuild. It writes HKCU `SOFTWARE\Autodesk\Revit\Autodesk Revit <year>\CodeSigning\<AddInId>=1` for **this** add-in only and Authenticode-signs the AppData `LoadingAI.dll` with a CurrentUser cert imported into Trusted Publishers. That is not a global security disable. Opt out with `-p:SkipTrustAddin=true`. CI already skips it via `SkipCopyAddin`.

```powershell
dotnet test src/LoadingAI.Tests/LoadingAI.Tests.csproj -c Release -p:SkipCopyAddin=true -p:SkipHostPluginBuild=true
dotnet test src/LoadingAI.RevitTests/LoadingAI.RevitTests.csproj -c Release -p:ForceRevitTests=true -p:RevitVersion=2027 -p:SkipHostPluginBuild=true --settings src/LoadingAI.RevitTests/revit.runsettings
```

## Review Checklist

- [ ] MIT header, no secret keys
- [ ] csproj does not ProjectReference the host
- [ ] Tier correct (L1 read / L2 light write / L3 destructive)
- [ ] Handles `Please open a model first.` and `Please select elements first.`
- [ ] Transaction with RollBack on failure
- [ ] Fake-bridge or in-Revit coverage for new Bridge tools (hosted xUnit first)
