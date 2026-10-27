# Current source changes: 1.4.1 to 1.4.2 candidate

The earlier original-source comparison is preserved in SOURCE-CHANGES-1.4.1.md.
Its descriptions of loose DLL loading and reflective Harmony calls are historical.
Read ../HARMONY-MIGRATION.md for the current implementation and troubleshooting.

- SpriteSound_DFUIntegration.cs starts a bootstrap after loading the logging option;
  duplicate entry-point calls do not open a second session. It marks SSF unready
  until the dependency checks, patching and retention check have completed.
- SpriteSoundHarmonyBootstrap.cs is new. It checks the required enabled dependency,
  order, version and active provider, yields for startup cleanup, installs hooks,
  verifies retention on the following frame, and then starts normal discovery.
- SpriteSound_NPCFRPatch.cs uses typed Harmony calls, verifies the shared library,
  reports each target, checks patch ownership and removes only SSF's owner on exit.
  The two identity postfix bodies are preserved. NPCFR remains optional.
- SpriteSoundFramework.dfmod.json declares the required dependency and version 1.4.2.
  The GUID, display title and maintainer-authored description are preserved.
- The other seven C# files are byte-identical to the packaged 1.4.1. Source hashes
  and validate_package.py allow a later maintainer to confirm that boundary.

Error and lifecycle comments are located beside the changed code. Do not move
patch installation into the entry point or start discovery before the final check:
that could recreate the startup cleanup race described in the migration guide.
