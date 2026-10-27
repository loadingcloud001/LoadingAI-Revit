# LoadingAI for Revit

Chat with your Revit model. LoadingAI adds a chat pane to Autodesk Revit 2024–2027. You ask in plain
language; a language model reads the model, answers, and — only when you approve — edits it.

> "How many walls are on Level 2, and which ones have no fire rating?"
> "Colour the walls by Fire Rating."
> "Split these beams where they meet the columns. Dry run first."
> "Export the active sheet set to PDF."

It is not a generic coding agent. Every action is a typed Revit tool (93 of them: 91 in seven plugins, and two for notes and playbooks)
that runs inside a Revit transaction on Revit's own thread, so a bad answer from the model cannot do
more than the tools allow.

MIT licensed. Free to use with the built-in provider, or bring your own key.

> **Status: early release (v0.6.0).** Revit 2027 is the only version it has been run in so far. See
> [Known limitations](docs/KNOWN_LIMITATIONS.md) before you point it at a model you care about, and
> work on a copy.

## What it can do

| Area | Examples |
|---|---|
| **Read the model** | Summaries, element details, selection, levels and grids, rooms, quantities, worksets, phases, warnings, links |
| **Views and presentation** | Isolate, hide, colour by parameter or schedule, floor plans, 3D and section views, schedules, tags and dimensions |
| **Edit** | Set parameters (with unit handling in mm, m², m³), rename, move, duplicate, delete, create walls and family instances, change types, load families |
| **Structure** | Split beams by columns, floors by beams, anything by levels or grids; cut openings from clashes |
| **Export** | PDF, DWG, IFC, purge unused |
| **Quantity takeoff** | Formwork setup and panels |
| **Look and name** | Take a picture of a view to check a result (`capture_view`, for models that can read images); map your words to the model's exact names (`resolve_names`) |

Tools are grouped into plugins under [`Plugins/`](Plugins/README.md). You can switch individual tools
on and off in the pane, and [write your own plugin](CONTRIBUTING.md).

## How it keeps your model safe

Tools come in three tiers, and the tier decides what you are asked:

- **L1 — read only.** Runs without a prompt.
- **L2 — light writes** (view overrides, parameter edits). A review card in the chat says what will
  change before it runs.
- **L3 — delete, move, create.** **Off by default.** When switched on, each use shows the same
  review card in the chat. There is one confirm and no second Revit dialog.

The review card names its effect on the Allow button ("Split 8 beams"). You can allow once, for this
session, or always (never for tools that delete or replace elements), deny with a reason the agent
reads, or edit the inputs first.

A **permission level** next to the Send button sets how much asks. Only you can change it; the model
cannot.

| Level | Reads | L2 writes | L3 writes |
|---|---|---|---|
| Read only | run | blocked | blocked |
| Plan | run | blocked | blocked |
| Ask before changes (default) | run | ask | ask |
| Auto-edit | run | run | ask |
| Bypass | run | run | run |

Bypass also dismisses Revit warning dialogs during the turn. An error Revit can only fix by deleting
elements rolls that step back. The whole turn is still one undo. A dry run never asks.

Also:

- Writes made during one chat turn fold into **a single undo step**. If anything fails, the turn
  rolls back.
- Many tools have a **dry run** that reports what would change and touches nothing.
- If Revit raises an error a tool cannot resolve, the whole batch is rolled back rather than left
  half-written.
- Plugins load only if the DLL's SHA-256 is on the allowlist, and you are asked before a new one is
  trusted.

The model can still be wrong. Review what it proposes, and keep your own backups.

## Install (no Visual Studio needed)

1. Install the [WebView2 Evergreen Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)
   if it is not already there. Windows 11 includes it.
2. Download **LoadingAI-Setup.exe** from [Releases](https://github.com/loadingcloud001/LoadingAI-Revit/releases).
3. Close Revit. Double-click the setup. It does not need administrator rights. Leave checked the
   Revit year it finds on this PC.
4. Start Revit, open a model, then **Add-Ins → LoadingAI → Open Chat**.

The chat plugins are stored once, for the newest year you leave checked. If you also use an older
Revit, run the setup again with only that year checked.

The zip for one year (`LoadingAI-Revit-2024.zip` … `LoadingAI-Revit-2027.zip`) is the same add-in
if you would rather copy the files yourself. Unblock the zip, unzip it, and run `Install.ps1`, or copy:

- `LoadingAI.addin` → `%AppData%\Autodesk\Revit\Addins\<year>\`
- the `LoadingAI\` folder → `%AppData%\Autodesk\Revit\Addins\<year>\LoadingAI\`
- `Plugins\` → `%AppData%\LoadingAI\Plugins\`

If Revit reports **Duplicated AddInId**, delete a leftover `RevitAiPoc.addin` and `RevitAiPoc\`
folder from `%AppData%\Autodesk\Revit\Addins\<year>\`.

## Using it

- Type a request. `/ask` answers without editing, `/plan` proposes steps, `/build` carries them out.
  The default is `/build`.
- Tool lengths are millimetres, areas m², volumes m³.
- A tool that finds elements saves them as `@r1`, `@r2`, … for the rest of the chat, so "colour
  them red" acts on exactly what the last search found.
- To uninstall, delete the `.addin` file and the `LoadingAI` folder from the Addins directory, and
  optionally `%AppData%\LoadingAI`.

## Memory and playbooks

- **Notes.** When you tell the agent a fact about the job ("beams on this job are UB sections"), it
  may offer to remember it. It always asks first, with the exact text, and a note can never be
  allowed for the session or always. Notes are read at the start of every chat: project notes for the
  open model, and notes for all projects. See and edit them in **Settings → Memory**. They live in
  `%AppData%\LoadingAI\memory`, one file per project.
- **Playbooks** are saved procedures in plain Markdown. Type `/` to see them and `/model-qa` (plus an
  optional note, such as `/model-qa only Level 2`) to run one. Four starters ship with the add-in:
  `model-qa`, `sheet-setup`, `beam-split` and `export-package`.
- **Write your own** in `%AppData%\LoadingAI\Playbooks` (Settings → Memory shows the path). Copy a
  starter and keep its header: `name`, `description`, `tools` (the tools it may use) and `mode`
  (`ask`, `plan` or `build`). A file with your own playbook's name replaces the starter of that name.
  A playbook guides the agent; every change still goes through the review card.

## Models, keys and privacy

**Built-in LoadingAI provider.** No key needed. Up to 100 requests a day per device (a "request" is
one message you send; the tool calls the model makes while answering are not counted). The count
shows in the chat header and Settings and resets at 00:00 UTC. It runs through a small gateway
([`gateway/`](gateway/README.md)) that holds the upstream credential, so no provider key is ever
shipped inside the add-in.

**Your own provider.** In Settings, add a key for any OpenAI-compatible provider (DeepSeek, OpenAI,
OpenRouter and others, or a local server). Keys are stored in `%AppData%\LoadingAI\settings.json`,
encrypted with Windows DPAPI for your user account. There is no daily limit on your own key.

**What leaves your computer.** Whichever provider you choose receives your chat messages and the
results of the tools it calls — for example element names, parameter values, counts and quantities
from the open model. Do not use a provider you would not send that information to. With the
built-in provider the gateway also receives a random device id (`%AppData%\LoadingAI\device.json`)
to count the daily allowance, and sees your network address like any web service; it keeps only a
short hash of that address for two days, to apply a per-network limit. The add-in has no analytics or telemetry. It keeps a size-bounded
diagnostic log on your machine (`%AppData%\LoadingAI\debug.jsonl`) that records tool calls and
errors.

## Build from source

You need Windows, the .NET 10 SDK, Node 22 or newer, and Revit installed for the version you build.

```powershell
cd web; npm install; npm run build; cd ..
dotnet test src/LoadingAI.Tests/LoadingAI.Tests.csproj -c Release -p:SkipCopyAddin=true -p:SkipHostPluginBuild=true
dotnet build src/LoadingAI/LoadingAI.csproj -c Release -p:RevitVersion=2027
```

The last command also copies the add-in into `%AppData%\Autodesk\Revit\Addins\2027\` and runs
`scripts/trust-addin.ps1`, which marks it as always-load for your user so Revit does not ask you to
accept it after every rebuild. That is a local-development convenience; CI never runs it.

Pack a release zip with `pwsh scripts/pack-addin.ps1 -RevitVersion 2027` (also 2024, 2025, 2026).
Pushing a `v*` tag publishes those four zips and **LoadingAI-Setup.exe** through GitHub Actions.
The setup program is `installer/LoadingAI.iss`; the workflow expands the zips and compiles it.

## Repository layout

| Path | What |
|---|---|
| `src/LoadingAI` | The add-in: ribbon, WebView2 pane, Revit adapter |
| `src/LoadingAI.Core` | Revit-free core (netstandard2.0): LLM clients, tool registry, plugin contract |
| `src/LoadingAI.RevitUtil` | Revit-bound unit and parameter helpers |
| `Plugins/` | The shipped tool plugins (they depend on Core only) |
| `web/` | The chat pane (Vite + React) |
| `gateway/` | The free-tier gateway: request handler, quota counters, deploy templates |
| `docs/` | [Known limitations](docs/KNOWN_LIMITATIONS.md) and [architecture decisions](docs/adr/README.md) |

## Contributing

Bug reports, plugins and fixes are welcome. Start with [CONTRIBUTING.md](CONTRIBUTING.md). To report
a vulnerability, see [SECURITY.md](SECURITY.md).

## Disclaimer

LoadingAI is an independent open-source project. It is not affiliated with, endorsed by, or
supported by Autodesk. Autodesk and Revit are trademarks of Autodesk, Inc. The software is provided
as is under the [MIT License](LICENSE); AI output can be wrong, so review changes to your models.
