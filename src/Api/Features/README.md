# App feature slices

One folder per vertical slice: `<Feature>Endpoints.cs` (a `MapTenantFeatureGroup` group),
`<Feature>Handler.cs`, `<Feature>Models.cs`, `<Feature>DataContributor.cs` — entity in
`src/Core/Entities`, registration + `app.Map<Feature>()` in `Program.cs`. Conventions and the
add-a-slice checklist: `docs/WAYS_OF_WORKING.md` (ADR-004); the port order: ADR-V001.

This file also keeps the folder present when no slice exists yet — the architecture tests locate
the repo root by this directory.
