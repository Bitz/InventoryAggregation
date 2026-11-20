# Tasks: Give the last refresh's start in whole seconds

- [x] Truncate `lastRefresh.startedAt` to the second in the response mapping.
- [x] Test it for a `200` and a `503`, with a clock that is not on a whole second.
- [x] `dotnet build` with 0 warnings, `dotnet test`, `npm run spec:check`.
- [x] Archive the change (`openspec archive whole-second-last-refresh`).
