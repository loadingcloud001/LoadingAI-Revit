# LoadingAI.RevitTests

NUnit tests that run **inside Revit**. They prove Adapter + `RevitBridge.AttachCurrentThread` (same-thread `RunAsync`, no ExternalEvent deadlock) against a live document.

GitHub-hosted `windows-latest` cannot start `Revit.exe`. CI builds this project but does not run it. Hosted tests stay on `src/LoadingAI.Tests` (xUnit + Fake `IRevitBridge`).

## Local run (Revit 2027 installed)

`revit.runsettings` pins `NUnit.Version=2027`, `Open=true`, `Close=true`. ricaun locates `Revit.exe` from Autodesk registry (default `C:\Program Files\...` or a custom volume such as `F:\Program Files\Autodesk\Revit 2027`). Do **not** put a machine-specific `Revit.exe` path in the committed runsettings — `NUnit.Application` is ricaun's console, not Revit.

```powershell
dotnet test src/LoadingAI.RevitTests/LoadingAI.RevitTests.csproj -c Release `
  -p:ForceRevitTests=true -p:RevitVersion=2027 `
  -p:SkipHostPluginBuild=true
```

Do **not** pass `-p:SkipCopyAddin=true` for this run. Revit loads `%AppData%\Autodesk\Revit\Addins\2027\LoadingAI\LoadingAI.dll` at startup. If that copy is older than the test build, you get `MissingMethodException` on `RevitBridge.AttachCurrentThread`. CopyAddin deploys the just-built host so the in-process bind matches.

CopyAddin then runs `scripts/trust-addin.ps1` (unless `-p:SkipTrustAddin=true`). That is **local-dev only**: HKCU `CodeSigning` DWORD `1` for AddInId `8F2C4E2A-9B3D-4E2A-A5F1-7D1A3B9C0E12`, plus Authenticode on the AppData DLL and a CurrentUser **Trusted Publishers** cert (`CN=LoadingAI Local Dev`). Unsigned Always Load is GUID-only; Revit still re-prompts when the DLL hash changes. Do **not** disable add-in security globally. Hosted CI uses `SkipCopyAddin`, so it never signs or writes HKCU. Close Revit before rebuild if the AppData DLL is locked.

`ForceRevitTests=true` flips `IsTestProject` on. Without it, `dotnet test LoadingAI.sln` will not launch Revit.

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

L3 stays dry-run only. The rollback test starts a transaction and rolls it back; it does not persist writes.
