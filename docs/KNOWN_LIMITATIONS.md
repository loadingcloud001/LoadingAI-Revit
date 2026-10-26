# Known limitations

What LoadingAI does not do yet, or does only partly. This is current as of **v0.6.0**. If something
here bites you, [open an issue](https://github.com/loadingcloud001/LoadingAI-Revit/issues) — a
reproduction on a small model is the most useful thing you can send.

## Where it has been tested

- **Revit 2027 is the only version exercised inside Revit.** The 2027.2 build was run against
  Autodesk's Snowdon sample model (13 in-Revit tests, plus hand checks of the install, link reload
  and confirmation prompts). The 2024, 2025 and 2026 builds compile and pass the unit tests in CI,
  but nobody has run them in Revit yet. They should work; treat them as unverified.
- **`qs-formwork` has not been run in Revit.** Its tests compile but have never executed. The panel
  geometry is the least-tested part of the add-in. A dry-run names the hosts and face types and
  says the panel count is not known until a real run. Check that result before you trust it.
- **The v0.6.0 review flow has not been run in Revit yet.** The single in-chat review, the
  permission levels, Select / Zoom to / Isolate, "Follow Revit" theme and the change counts are
  covered by unit tests and a browser preview, but nobody has run them inside Revit. There is no
  Revit dialog behind the review card any more, so work on a copy until you have checked it. The
  steps are in [pane-redesign-in-revit-checklist.md](pane-redesign-in-revit-checklist.md).
- Windows only. Needs the WebView2 Evergreen runtime.

## Structure-cut tools (`split_*`)

- **A large batch can fail as one unit.** The split tools run in a single transaction. If Revit
  raises an error the tool cannot resolve, the whole batch rolls back and the reply says
  *"Revit rolled back the whole batch … Nothing was changed."* Nothing is half-written. Work in
  pages of about 10 using `limit` and `continueFrom`.
- **Re-created members lose what is hosted on them.** When a wall or floor cannot be split natively
  it is rebuilt, and doors, windows or equipment hosted on it are deleted with the old one. A
  `dryRun` forecasts how many dependents would go; read it first.
- **Joins may be dropped.** If Revit cannot keep a join on a rebuilt piece, the tool detaches it and
  says so (*"Unjoined N join(s)"*). Re-join them if your model needs it.
- `split_by_levels` can limit hosts to one storey with `hostLevelName`. Leaving that empty still considers every matching host in the scope. The levels you pick are the planes to cut at, which is a separate choice.
- A split run is slow on big models: a 10-wall page took about a minute on Snowdon. Revit's window
  is busy while it works. The whole turn undoes in one step.

## Chat and tools

- **English only.** The add-in's own text and tool descriptions are English; the model translates.
- **Some tools cap their output on purpose.** For example `tag_all` tags at most 50 elements per
  call and says so.
- **Destructive tools are off by default.** Anything that deletes, moves or creates elements (the
  `L3` tools) must be switched on in the pane, and asks for a review in the chat each time. The
  **Bypass** permission level, off by default, skips those reviews and Revit warning dialogs for
  tools that are already on. It does not switch a tool on, and it does not delete elements to clear
  a Revit error. Tools that delete or replace elements can never be set to "Always allow".
- **One confirm, no Revit dialog.** The review is a card in the pane. If the pane is hidden the
  add-in shows it; a review left unanswered is cancelled after 3 minutes (15 for a Run form), never
  approved.
- **Select, Zoom to and Isolate only change what you see.** They are unavailable while the agent is
  still changing the model, and Isolate needs a view that supports temporary isolation. There is no
  Undo button: use Ctrl+Z in Revit, which undoes the whole turn at once.
- **The change count is Revit's own.** It counts what Revit reported inside the turn, including
  dependents deleted with an element. Element ids are listed for the first 2000 per category.
- **Rolling back a turn is all-or-nothing.** Denying one write confirmation rolls back every write
  made earlier in the same turn.

## The built-in free tier

- Up to 100 requests per day per device. The device id is generated on your machine, so the
  allowance is a courtesy limit rather than licensing — it is not tamper-proof. The service also has
  a daily ceiling for all users together and one per network address, so on a busy day it can answer
  "at capacity" until 00:00 UTC. Use your own key if you need it every day.
- It runs on a shared gateway that you do not control. For work you depend on, add your own key for
  any other provider in **Settings**, or [run your own gateway](../gateway/README.md).

## Plugins

- The plugin allowlist (SHA-256 of each DLL) stops accidents and third-party drop-ins. It is **not**
  a defence against software already running as you. Only trust plugins you have read or built.
- **Rescan** picks up a new or changed plugin on Revit 2025 and later. On Revit 2024 a DLL that is
  already loaded stays in memory until Revit restarts.
