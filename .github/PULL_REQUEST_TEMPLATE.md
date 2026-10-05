## What and why

<!-- What does this change, and what problem does it solve? Link the issue if there is one. -->

## How it was checked

<!-- Unit tests, an in-Revit run (say which Revit version), or both. -->

## Checklist

- [ ] No secrets, API keys, or private model data in the diff
- [ ] Plugins reference `LoadingAI.Core` only, never the host
- [ ] The tool's tier is right (L1 read / L2 light write / L3 destructive)
- [ ] Tests added or updated; `dotnet test src/LoadingAI.Tests` passes
- [ ] Docs updated if behaviour changed
