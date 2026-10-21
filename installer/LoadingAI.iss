; LoadingAI for Revit — per-user setup. No administrator rights.
; Compile with: ISCC.exe /DMyAppVersion=0.3.0 installer/LoadingAI.iss
; The four year folders under payload\ are produced by scripts/stage-setup.ps1.

#ifndef MyAppVersion
  #define MyAppVersion "0.3.0"
#endif

#define AddInId "{{A7C31E4B-6D2F-4A18-9C55-2E8B0F1D6A40}"

[Setup]
AppId={#AddInId}
AppName=LoadingAI for Revit
AppVersion={#MyAppVersion}
AppPublisher=LoadingAI
AppPublisherURL=https://github.com/loadingcloud001/LoadingAI-Revit
AppSupportURL=https://github.com/loadingcloud001/LoadingAI-Revit/issues
DefaultDirName={userappdata}\LoadingAI
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
CreateAppDir=no
UninstallFilesDir={userappdata}\LoadingAI\uninstall
OutputDir=..\artifacts
OutputBaseFilename=LoadingAI-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
CloseApplications=no
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Types]
Name: "full"; Description: "Revit years on this PC"

[Components]
Name: "y2024"; Description: "Revit 2024"; Types: full
Name: "y2025"; Description: "Revit 2025"; Types: full
Name: "y2026"; Description: "Revit 2026"; Types: full
Name: "y2027"; Description: "Revit 2027"; Types: full

[InstallDelete]
Type: filesandordirs; Name: "{userappdata}\Autodesk\Revit\Addins\2024\LoadingAI"; Components: y2024
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2024\RevitAiPoc.addin"; Components: y2024
Type: filesandordirs; Name: "{userappdata}\Autodesk\Revit\Addins\2024\RevitAiPoc"; Components: y2024
Type: filesandordirs; Name: "{userappdata}\Autodesk\Revit\Addins\2025\LoadingAI"; Components: y2025
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2025\RevitAiPoc.addin"; Components: y2025
Type: filesandordirs; Name: "{userappdata}\Autodesk\Revit\Addins\2025\RevitAiPoc"; Components: y2025
Type: filesandordirs; Name: "{userappdata}\Autodesk\Revit\Addins\2026\LoadingAI"; Components: y2026
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2026\RevitAiPoc.addin"; Components: y2026
Type: filesandordirs; Name: "{userappdata}\Autodesk\Revit\Addins\2026\RevitAiPoc"; Components: y2026
Type: filesandordirs; Name: "{userappdata}\Autodesk\Revit\Addins\2027\LoadingAI"; Components: y2027
Type: files; Name: "{userappdata}\Autodesk\Revit\Addins\2027\RevitAiPoc.addin"; Components: y2027
Type: filesandordirs; Name: "{userappdata}\Autodesk\Revit\Addins\2027\RevitAiPoc"; Components: y2027

[Files]
Source: "payload\2024\LoadingAI.addin"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2024"; Components: y2024; Flags: ignoreversion
Source: "payload\2024\LoadingAI\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2024\LoadingAI"; Components: y2024; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "payload\2024\Plugins\*"; DestDir: "{userappdata}\LoadingAI\Plugins"; Components: y2024; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "payload\2025\LoadingAI.addin"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2025"; Components: y2025; Flags: ignoreversion
Source: "payload\2025\LoadingAI\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2025\LoadingAI"; Components: y2025; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "payload\2025\Plugins\*"; DestDir: "{userappdata}\LoadingAI\Plugins"; Components: y2025; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "payload\2026\LoadingAI.addin"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2026"; Components: y2026; Flags: ignoreversion
Source: "payload\2026\LoadingAI\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2026\LoadingAI"; Components: y2026; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "payload\2026\Plugins\*"; DestDir: "{userappdata}\LoadingAI\Plugins"; Components: y2026; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "payload\2027\LoadingAI.addin"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2027"; Components: y2027; Flags: ignoreversion
Source: "payload\2027\LoadingAI\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2027\LoadingAI"; Components: y2027; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "payload\2027\Plugins\*"; DestDir: "{userappdata}\LoadingAI\Plugins"; Components: y2027; Flags: ignoreversion recursesubdirs createallsubdirs

[Messages]
WelcomeLabel2=This copies LoadingAI into your Revit Add-Ins folder. Close Revit first. Administrator rights are not required.%n%nOn the next page, leave checked the Revit year installed on this PC. The chat plugins are stored once, for the newest year you leave checked. If you also use an older Revit, run this setup again with only that year checked.
FinishedLabel=Start that Revit, open a model, then choose Add-Ins, LoadingAI, Open Chat.%n%nThe chat pane needs the Microsoft WebView2 Evergreen Runtime. Windows 11 already includes it.

[Code]
var
  ComponentsReady: Boolean;

function RevitInstalled(Year: String): Boolean;
begin
  Result :=
    RegKeyExists(HKLM64, 'SOFTWARE\Autodesk\Revit\Autodesk Revit ' + Year) or
    RegKeyExists(HKCU, 'SOFTWARE\Autodesk\Revit\Autodesk Revit ' + Year) or
    DirExists(ExpandConstant('{userappdata}\Autodesk\Revit\Addins\' + Year));
end;

function YearSelectedByDefault(Year: String): Boolean;
var
  Any: Boolean;
begin
  Any := RevitInstalled('2024') or RevitInstalled('2025') or RevitInstalled('2026') or RevitInstalled('2027');
  if Any then
    Result := RevitInstalled(Year)
  else
    Result := (Year = '2027');
end;

function WebView2Installed: Boolean;
begin
  Result :=
    RegKeyExists(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}') or
    RegKeyExists(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}') or
    RegKeyExists(HKCU, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}');
end;

procedure InitializeWizard;
begin
  if not WebView2Installed then
    MsgBox('The WebView2 Evergreen Runtime is not installed. The chat pane needs it.' + #13#10 +
      'Install it from https://developer.microsoft.com/microsoft-edge/webview2/ before you open the chat.' + #13#10 +
      'You can still finish this setup.', mbInformation, MB_OK);
end;

procedure CurPageChanged(CurPageID: Integer);
var
  i: Integer;
  caption: String;
begin
  if (CurPageID = wpSelectComponents) and (not ComponentsReady) then
  begin
    ComponentsReady := True;
    for i := 0 to WizardForm.ComponentsList.Items.Count - 1 do
    begin
      caption := WizardForm.ComponentsList.ItemCaption[i];
      if Pos('2024', caption) > 0 then WizardForm.ComponentsList.Checked[i] := YearSelectedByDefault('2024');
      if Pos('2025', caption) > 0 then WizardForm.ComponentsList.Checked[i] := YearSelectedByDefault('2025');
      if Pos('2026', caption) > 0 then WizardForm.ComponentsList.Checked[i] := YearSelectedByDefault('2026');
      if Pos('2027', caption) > 0 then WizardForm.ComponentsList.Checked[i] := YearSelectedByDefault('2027');
    end;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = wpSelectComponents then
  begin
    if not (WizardIsComponentSelected('y2024') or WizardIsComponentSelected('y2025') or
            WizardIsComponentSelected('y2026') or WizardIsComponentSelected('y2027')) then
    begin
      MsgBox('Select at least one Revit year.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;
