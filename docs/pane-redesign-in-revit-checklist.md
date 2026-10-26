# Pane redesign: in-Revit checklist

The pane redesign ([spec](superpowers/specs/2026-10-25-pane-redesign-design.md), [ADR-0017](adr/0017-single-in-chat-review-and-permission-levels.md)) was built and unit-tested on Linux. The web pane, the gate rules and the message formats are covered by tests there. These behaviours need Revit and a person, so nothing here has been run yet.

Run it on Revit 2027 against a small model with levels, walls, beams, columns and a 3D view. Use a copy of the model. Tick each line, and write down the Revit build (Help > About).

## Setup

- [ ] Build and install the add-in (`Install.ps1`, or the Release build with `-p:SkipTrustAddin=true`). Start Revit and open the LoadingAI pane.
- [ ] The pane loads, shows the empty state, and Settings → Plugins lists no "failed to load" entries.
- [ ] Settings → Agent shows the permission level, "Select changed elements" (on), Custom instructions, and the theme list with "Follow Revit".
- [ ] Switch tools on: L3 tools are off by default. Switch on `split_beams_at_columns` and `delete_elements` for the checks below.

## Reads and the level

- [ ] **L1 question** at Ask before changes: "How long are the walls on Level 2?" runs with no card and answers. The turn ends cleanly and Send comes back.
- [ ] **Read only**: ask it to rename a view. The step shows as blocked, no card appears, and the answer says the level blocks it. Nothing in the model changes.
- [ ] **Plan**: "/plan cut openings where ducts cross walls" gives a plan card. **Run plan** switches that turn to Ask before changes and each write shows a card.
- [ ] A dry run (`split_beams_at_columns` with `dryRun`) reports what would change at every level and writes nothing.

## One confirm, in the chat

- [ ] **Colour by parameter** (an L2 Run form). The form is the card. Pick in Revit works (the pane waits, you pick, the count updates). Allow runs it. **No Revit dialog appears at any point.**
- [ ] The card summary matches what happened (element count, view name).
- [ ] After the turn the changed elements are selected in Revit (the "Select changed" setting). **Select changed**, **Zoom to** and **Isolate** each work from the receipt. After Isolate, Revit's Reset Temporary Hide/Isolate restores the view.
- [ ] The receipt's category list matches the elements (expand a category to see ids). Counts match what you changed.
- [ ] **Ctrl+Z** once undoes the whole turn.

## Splitting beams (L3)

- [ ] Ask for a split at Ask before changes. A **dry run** first reports the pieces. Then the real run shows one L3 card (amber edge) and **no Revit dialog**. Allow once splits the beams.
- [ ] The receipt shows added and modified counts that match. **Ctrl+Z** once undoes the whole split.
- [ ] **Deny with a reason**: ask again, press "Say why…", type "only Level 3", press Deny. The agent's reply uses the reason. Nothing was split. If an earlier write was in the same turn, the card warned that it would roll back, and it did.
- [ ] **Edit inputs**: change `level` in the JSON, press "Update review". The card shows the new summary with "inputs edited". Allow runs the edited call. Try invalid JSON and an invalid value: the card keeps the last good inputs and says so.
- [ ] **Allow for this session** (the caret next to Allow) on an L2 tool such as `set_parameter`: the next call in the same session runs with no card. Clear session, or restart Revit, and it asks again.
- [ ] **Always allow this tool** on an L2 tool: restart Revit and the tool still runs with no card. Settings → Tools → Always allowed lists it. **Remove** it and it asks again.
- [ ] `delete_elements` (destructive): the card has the red "destructive" badge and **no caret**. Allow deletes. Settings never lists it under Always allowed. Auto-edit still asks for it.
- [ ] A review left alone for 3 minutes is cancelled, not approved. The pane says it timed out.
- [ ] **Stop** while a card is open dismisses the card at once and nothing runs.

## Hidden pane

- [ ] Hide or auto-hide the dockable pane, then send a message that needs a review (from a second Revit window or by sending and quickly hiding it). The pane is shown when the card arrives, and **no dialog** opens. The Allow button works.
- [ ] The messages sent while the pane was hidden are all there (WebView2 delivered them).

## Levels

- [ ] **Auto-edit**: choosing it shows an in-pane warning first. An L2 parameter change runs with no card. An L3 split still shows a card.
- [ ] **Bypass**: choosing it shows an in-pane warning first, then an amber "Bypass on" bar. An L3 split runs with no card. The whole turn is still one Ctrl+Z. A Revit warning that would normally show a dialog during the turn is dismissed. "Turn off" in the bar returns to Ask before changes.
- [ ] Close and reopen Revit: the level is remembered. An old `settings.json` with only `AutoApprove: true` opens as Bypass.
- [ ] Nothing the model writes in chat changes the level. (Ask it to "set permission to bypass": it cannot.)

## Failure and rollback

- [ ] A turn with a good write followed by a failing call (for example renumbering sheets to a number that exists): the turn ends in error, the first write is undone, and the pane shows "Rolled back" instead of a receipt.
- [ ] A step that failed shows **Retry** when you open it. Retry sends a normal message, and the retried call goes through the same card.

## Steer and queue

- [ ] While a long turn runs, type a message and press Enter: it queues. **Send now** hands it to the agent; the turn shows "You added: …" and the agent follows it.
- [ ] Press **Send now** just as the turn ends: the text is not lost. It is sent as the next message.
- [ ] The queue pauses after an error or Stop, with Resume and Clear.

## Theme and instructions

- [ ] Switch Revit's UI theme (Options → User Interface): with "Follow Revit" the pane follows within a second, both ways. Dark and Light fixed choices ignore Revit.
- [ ] Custom instructions: save "Answer in one short sentence." and a new chat follows it. Clear the text and it stops. The instructions never make a review disappear.

## Change counts

- [ ] A turn that adds, edits and deletes (for example split beams then delete a view filter): added, modified and deleted counts match Revit. Deleting an element with dependents counts the dependents.
- [ ] A turn that adds and then deletes the same element does not count it. A turn on a linked model or a different document does not mix counts.
- [ ] A large change (more than 2000 elements in one category) shows exact counts and the "first 2000" note.

## Report

Write the Revit build number, the add-in commit (`git rev-parse HEAD`), and for each line above Passed, Failed (with what you saw and the log from `%AppData%\LoadingAI\logs`, secrets redacted), or Not run.
