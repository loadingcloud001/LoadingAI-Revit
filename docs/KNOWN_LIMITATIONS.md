# Known limitations

What LoadingAI does not do yet, or does only partly. This is current as of **v0.3.1**. If something
here bites you, [open an issue](https://github.com/loadingcloud001/LoadingAI-Revit/issues) — a
reproduction on a small model is the most useful thing you can send.

## Where it has been tested

- **Revit 2027 is the only version exercised inside Revit.** The 2027.2 build was run against
  Autodesk's Snowdon sample model (13 in-Revit tests, plus hand checks of the install, link reload
  and confirmation prompts). The 2024, 2025 and 2026 builds compile and pass the unit tests in CI,
  but nobody has run them in Revit yet. They should work; treat them as unverified.
- **`qs-formwork` has not been run in Revit.** Its tests compile but have never executed. The panel
  geometry is the least-tested part of the add-in. Use `dryRun` and check the result before you
  trust it.
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
- `split_by_levels` has no level filter: it considers every matching host in the scope you give it.
- A split run is slow on big models: a 10-wall page took about a minute on Snowdon. Revit's window
  is busy while it works. The whole turn undoes in one step.

## Chat and tools

- **English only.** The add-in's own text and tool descriptions are English; the model translates.
- **Some tools cap their output on purpose.** For example `tag_all` tags at most 50 elements per
  call and says so.
- **Destructive tools are off by default.** Anything that deletes, moves or creates elements (the
  `L3` tools) must be switched on in the pane, and asks for confirmation each time.
- **Rolling back a turn is all-or-nothing.** Denying one write confirmation rolls back every write
  made earlier in the same turn.

## The built-in free tier

- Up to 100 requests per day per device. The device id is generated on your machine, so the
  allowance is a courtesy limit rather than licensing — it is not tamper-proof, and the service may
  tighten it.
- It runs on a shared gateway that you do not control. For work you depend on, add your own key for
  any other provider in **Settings**, or [run your own gateway](../gateway/README.md).

## Plugins

- The plugin allowlist (SHA-256 of each DLL) stops accidents and third-party drop-ins. It is **not**
  a defence against software already running as you. Only trust plugins you have read or built.
- **Rescan** picks up a new or changed plugin on Revit 2025 and later. On Revit 2024 a DLL that is
  already loaded stays in memory until Revit restarts.
