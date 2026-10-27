# Optional logging in 1.4.2

The existing Enable Mod Logging checkbox, SSF/Logs location and two-file rotation
are unchanged. Turn it on before a fresh game launch to capture startup events.
If startup failed, enable diagnostics through Mod Options if available and restart;
toggling the logger is not a retry mechanism for failed dependency installation.

Search for [SpriteSound][Harmony]. Expected order:
1. dependency=daggerfall.harmony modVersion=... stage=waiting-for-Start
2. library=0Harmony, Version=2.4.2.0, ...
3. patched=...SetMaterial for each installed target (or NPCFR=absent).
4. stage=ready hooks-retained=true; discovery started.

An unsupported NPCFR signature is a warning, not a claim of full compatibility.
A startup failed message means discovery did not start and SSF remains unready.
Errors/warnings still reach Unity's log when the checkbox is off. Missing assembly
errors from DFU itself may appear before SSF diagnostics; include Player.log too.

No fallback loads a DLL from disk. Compare installed dependency version, loaded
library identities, enabled state and order before investigating audio behavior.
The retention check happens during startup only; it is not a continuous monitor.
