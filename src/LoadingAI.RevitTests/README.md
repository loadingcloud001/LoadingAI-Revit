# LoadingAI.RevitTests

NUnit tests that run **inside Revit**. They prove Adapter + `RevitBridge.AttachCurrentThread` (same-thread `RunAsync`, no ExternalEvent deadlock) against a live document.

GitHub-hosted `windows-latest` cannot start `Revit.exe`. CI builds this project but does not run it. Hosted tests stay on `src/LoadingAI.Tests` (xUnit + Fake `IRevitBridge`).

## Local run (Revit 2027 installed)

`revit.runsettings` pins `NUnit.Version=2027`, `Open=true`, `Close=true`. ricaun locates `Revit.exe` from Autodesk registry (default `C:\Program Files\...` or a custom volume such as `F:\Program Files\Autodesk\Revit 2027`). Do **not** put a machine-specific `Revit.exe` path in the committed runsettings — `NUnit.Application` is ricaun's console, not Revit.

```powershell
# Refresh the installed host + shipped plugin hash set first.
dotnet build src/LoadingAI/LoadingAI.csproj -c Release `
  -p:RevitVersion=2027 -p:SkipTrustAddin=true -m:1
$env:DOTNET_SYSTEM_GLOBALIZATION_INVARIANT = "1"
$env:DOTNET_SYSTEM_GLOBALIZATION_PREDEFINED_CULTURES_ONLY = "0"
$env:RICAUN_REVITTEST_TESTADAPTER_NUNIT_LANGUAGE = "ENU"
dotnet test src/LoadingAI.RevitTests/LoadingAI.RevitTests.csproj -c Release `
  -p:ForceRevitTests=true -p:RevitVersion=2027 `
  -p:SkipHostPluginBuild=true -p:SkipTrustAddin=true
```

Do **not** pass `-p:SkipCopyAddin=true` for this run. Revit loads `%AppData%\Autodesk\Revit\Addins\2027\LoadingAI\LoadingAI.dll` at startup. If that copy is older than the test build, you get `MissingMethodException` on `RevitBridge.AttachCurrentThread`. CopyAddin deploys the just-built host so the in-process bind matches.

CopyAddin then runs `scripts/trust-addin.ps1` (unless `-p:SkipTrustAddin=true`). That is **local-dev only**: HKCU `CodeSigning` DWORD `1` for AddInId `8F2C4E2A-9B3D-4E2A-A5F1-7D1A3B9C0E12`, plus Authenticode on the AppData DLL and a CurrentUser **Trusted Publishers** cert (`CN=LoadingAI Local Dev`). Unsigned Always Load is GUID-only; Revit still re-prompts when the DLL hash changes. Do **not** disable add-in security globally. Hosted CI uses `SkipCopyAddin`, so it never signs or writes HKCU. Close Revit before rebuild if the AppData DLL is locked.

`ForceRevitTests=true` flips `IsTestProject` on. Without it, `dotnet test LoadingAI.sln` will not launch Revit.

For this machine's Revit 2027/ricalun 1.11.1 combination, keep `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` together with `DOTNET_SYSTEM_GLOBALIZATION_PREDEFINED_CULTURES_ONLY=0` (see the 2027.2 section); otherwise the adapter's assembly resolver can recurse through satellite resources and report `Stack overflow` before discovering tests. To attach to a model that is already open, set `RICAUN_REVITTEST_TESTADAPTER_NUNIT_OPEN=false` and `...NUNIT_CLOSE=false` and run the same command. The Opening Tools slice is `OpeningToolsInRevitTests`: discovery/hash gate, dry-run, live place+punch, and repair-cut, all write probes rolled back.

Optional env overrides (prefix `RICAUN_REVITTEST_TESTADAPTER_`):

```powershell
$env:RICAUN_REVITTEST_TESTADAPTER_NUNIT_VERSION = "2027"
$env:RICAUN_REVITTEST_TESTADAPTER_NUNIT_OPEN = "true"
$env:RICAUN_REVITTEST_TESTADAPTER_NUNIT_CLOSE = "true"
$env:RICAUN_REVITTEST_TESTADAPTER_NUNIT_VERBOSITY = "2"
```

If no document is already open, `[OneTimeSetUp]` creates a throwaway Metric project under `%TEMP%` (not committed). Tests still `Ignore` if that create/activate step is blocked (Revit forbids some document ops inside event handlers).

Construct `RevitBridge` in `[OneTimeSetUp]`, not inside an `async` NUnit test: `ExternalEvent.Create` is only legal in a standard API execution. Do not commit `.rvt` files.

## ricaun vs TestRunner

[`ricaun.RevitTest.TestAdapter`](https://github.com/ricaun-io/ricaun.RevitTest) **1.11.1** is documented for Revit **2019–2025**. On this product it **does launch Revit 2027** when `NUnit.Version=2027` (registry `InstallLocation`, not a committed `Revit.exe` path).

If `dotnet test -p:ForceRevitTests=true` cannot start Revit:

1. The assembly still compiles: `dotnet build src/LoadingAI.RevitTests -p:RevitVersion=2027 -p:SkipCopyAddin=true -p:SkipHostPluginBuild=true`
2. Load that DLL with [Revit.TestRunner](https://github.com/geberit/Revit.TestRunner) inside Revit 2027.
3. First slice avoids App.Bridge, ribbon, HostPage, WebView2, and TaskDialog.

### Verified on Revit 2027.2: the adapter runs but Revit cannot finish startup

Recorded because it costs about half an hour to rediscover. On Revit **2027.2** (build `20260716_1515`) the two known failure modes are:

**1. `Revit.exe` is not discoverable.** A Revit install that never wrote `InstallLocation` under `HKLM\SOFTWARE\Autodesk\Revit\Autodesk Revit 2027` cannot be found by `ricaun`, and every test fails with `RevitTest: Timeout 10 minutes` — including tests that have nothing to do with the code under test. Check the value before reading anything into a timeout. Fix it machine-locally:

```powershell
reg add "HKLM\SOFTWARE\Autodesk\Revit\Autodesk Revit 2027" /v InstallLocation /t REG_SZ /d "<install dir>" /f   # admin
```

**2. With Revit discoverable, Revit 2027.2 dies in UI initialisation.** It starts, completes licensing, then fails with `ExceptionCode=0xe0434352` and `Unconverted MessageBox ""` in the journal, at the point where it would set WPF rendering mode — before `Journal Init`. A successful run's journal contains `Setting WPF rendering mode to SoftwareOnly` and `Journal Init`; a failing one contains neither and is ~6 KB instead of ~480 KB. It reproduces to the second, and `dotnet test` reports `No test is available` because discovery dies with it.

**Fixed by a second variable (verified 2026-10-04, Revit 2027.2, 12 tests ran in about 57 s).** The invariant-globalization switch above is inherited by the Revit process the test host launches, and Revit then dies in WPF initialisation. Set both, so the tooling stays invariant while Revit may create the OS culture:

```powershell
$env:DOTNET_SYSTEM_GLOBALIZATION_INVARIANT = "1"
$env:DOTNET_SYSTEM_GLOBALIZATION_PREDEFINED_CULTURES_ONLY = "0"
$env:DOTNET_SYSTEM_GLOBALIZATION_PREDEFINED_CULTURES_ONLY = "0"
```

Without the first, discovery stack-overflows; with only the first, every test times out after 10 minutes.

This is **not the add-in**. It reproduces identically with this project's add-in moved out of `%AppData%\Autodesk\Revit\Addins\2027`, and the same binary launched by hand reaches a full ribbon with the add-in installed. `ricaun.RevitTest.TestAdapter` 1.11.1 is documented for Revit 2019–2025.

**Step 2 above does not reach 2027.** Revit.TestRunner's latest release (v1.5.0, July 2024) ships `InstallAddin v2022…v2025.cmd` only, is a prerelease flagged "problems detected", and its `net8.0-windows` add-in bundles its own Revit 2025 `RevitAPI.dll`. With the two variables above the ricaun adapter does run on 2027.2 (`PluginFixesInRevitTests` executed there). `FormworkInRevitTests` has not been run on it; see [Known limitations](../../docs/KNOWN_LIMITATIONS.md).

Kind B bridge-only plugins (structure-cuts, qs-formwork) use `CreateTools` with a plain `PluginContext(raw, pluginDir)` — not `PluginLoader.Discover`. Dry-run first (empty selection / scope all skip reasons). One write is wrapped in `TransactionGroup.RollBack` so it does not persist. The host smoke rollback test starts a transaction and rolls it back; it does not persist writes.
