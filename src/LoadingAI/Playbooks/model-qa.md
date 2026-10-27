---
name: model-qa
description: Check warnings, model health and room problems, then show the worst area.
tools: [get_warnings, get_model_health_score, audit_issue_set, get_rooms, capture_view]
mode: build
---
1. Run get_model_health_score and get_warnings. Group warnings by description and count them.
2. Run audit_issue_set and list each issue kind with its count.
3. Run get_rooms and note rooms that are unplaced, unbounded or redundant.
4. Take the warning group with the most elements. Call capture_view with its saved result set (@rN) so the user can see it.
5. Answer with a short table: issue, count, what to do. Change nothing in the model.
