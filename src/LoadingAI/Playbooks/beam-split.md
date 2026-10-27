---
name: beam-split
description: Split beams at columns safely: dry run, review, split, check the receipt.
tools: [resolve_names, find_elements_by_filter, split_beams_by_columns, capture_view]
mode: build
---
1. Use resolve_names for the level the user names. If no level is named, ask.
2. Run split_beams_by_columns with dryRun true and report how many beams would split into how many pieces.
3. Ask the user to confirm the real run. Then run split_beams_by_columns without dryRun; the user reviews it on the card.
4. Call capture_view on the new pieces (@rN from the result if given) and say whether the result looks right.
5. Remind the user that one Ctrl+Z undoes the whole split.
