# Reverberated 1.4.2 candidate source

Read the package-level README.md and HARMONY-MIGRATION.md before building.
This version requires daggerfall.harmony.dfmod and an explicit build-time Harmony
reference. Use DeveloperTests/Build-Candidate.ps1 to generate the staging manifest;
do not copy a loose Harmony DLL into the release or reuse the old build instructions.

SOURCE-CHANGES.md describes the current delta. SOURCE-CHANGES-1.4.1.md preserves
the earlier comparison against original SSF. The audio and ensemble files retain
their 1.4.1 executable behavior; the migration affects startup and identity hooks.
