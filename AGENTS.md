# Guide for AI coding assistants

Read [CLAUDE.md](CLAUDE.md): it is the guide for any AI assistant or developer changing this repository (where
things are, how to check a change, the rules). How it works inside is in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).
The short version:

- No job data in the repository: examples are made up ("Elm Street Drainage", "RFI 12"); job facts live in the user's
  project database, and `src/tools/project-db.sql` stays a copy of Project Analyzer's schema.
- Run `npm run build`, `npm test` and `npm run docs:check` before committing; regenerate `docs/tools.generated.md`
  with `npm run docs:generate` after changing tools.
- A tool is a domain action in `src/tools/domains/` plus a plugin method in `CommandDispatcher.cs`; drawings change
  only inside the plugin, through `CivilExecution`.
- Never start Civil 3D, AutoCAD or `accoreconsole` from an automated session; `dotnet build` of the plugin only
  compiles.
