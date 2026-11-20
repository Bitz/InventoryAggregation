# Tasks: Link every report response to the refresh that made it

- [x] Carry the refresh's span context on `StoredReport`.
- [x] Link every `200` to it, once; no link on `503`.
- [x] Tests for a plain request, a stale report and a forced request with one link.
- [x] README telemetry section.
- [x] `dotnet build` with 0 warnings, `dotnet test`, `npm run spec:check`.
- [x] Archive the change (`openspec archive link-requests-to-refresh-trace`).
