# Optional logging

See README.md for the Mod Options checkbox, rotation, and diagnostic event table.
Use SpriteSoundLog.Enabled before collecting diagnostic-only state or formatting
frequent messages. Info is suppressed when off; operational Warning/Error calls
continue to Unity's normal log. Do not put playback actions inside logging guards.
Toggling logging must never reset the conductor or an emitter's playback session.
