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
- After changing the plugin's source, run `npm run plugin:prebuild` and commit `prebuilt/` before tagging a release: the
  Release workflow stops if the prebuilt plugin doesn't match the source.
- Job paths are Windows paths, on purpose (`path.win32`), so `npm test` needs Windows; CI runs there too.
- What's planned and what needs working on is in [ROADMAP.md](ROADMAP.md); keep its review date and lists true.
