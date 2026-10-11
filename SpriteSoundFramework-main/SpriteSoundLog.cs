// PATCH ADDITION: SSF messages previously went through Unity's log directly.
// This writer adds mod-local diagnostics and a live Mod Options switch. It never
// captures other mods' messages or controls any audio. Ordinary warnings/errors
// still reach Unity when diagnostics are off, matching the former release policy.
// A game session and a logging interval are different: one game session may
// contain several on/off intervals but must rotate its files at most once.
// Provenance: compared with the user-supplied original files; see SOURCE-CHANGES.md.

// Optional diagnostic destination controlled by DFU Mod Options.
// Keep diagnostics separate from playback: a disk error must not interrupt a sound.
using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace SpriteSoundFramework
{
    public static class SpriteSoundLog
    {
        // Keep one complete current run and one complete previous run.
        // Rotate at the first enable in a new session; that may be during gameplay.
        // Further enables in that session append and keep the previous-run file intact.
        private static readonly object Gate = new object();
        private static StreamWriter writer;
        private static string modDirectory;
        // initialized means an open attempt happened during this enabled interval;
        // failed prevents repeated attempts on every message after an I/O error.
        // ended blocks late callbacks after shutdown. begun prevents duplicate startup.
        // rotated survives checkbox toggles and resets only for a new game session.
        private static bool initialized;
        private static bool failed;
        private static bool ended;
        private static bool rotated;
        private static bool begun;
        // Callers check this before collecting diagnostic samples or formatting text.
        public static bool Enabled { get; private set; }

        // Call once at the mod entry point, before other SSF components write messages.
        // Unity's normal log receives a location notice on each successful open.
        // With diagnostics enabled, SSF messages go to
        // the dedicated files; this does not intercept messages from other mods.
        public static void BeginSession(string loadedModDirectory, bool enable = false)
        {
            lock (Gate)
            {
                // A repeated initialization in the same run must not rotate twice.
                if (begun && !ended) return;
                begun = true;
                CloseWriter();
                modDirectory = loadedModDirectory;
                initialized = false;
                failed = false;
                ended = false;
                rotated = false;
                Enabled = false;
                Application.quitting -= EndSession;
                Application.quitting += EndSession;
            }
            SetEnabled(enable);
        }

        // Applying the checkbox changes only diagnostics, never the audio session.
        // Re-enabling within a run appends to that run; it does not rotate away evidence.
        public static void SetEnabled(bool enable)
        {
            lock (Gate)
            {
                if (ended || enable == Enabled) return;
                if (!enable)
                {
                    Info("[SpriteSound][Session] mod logging disabled");
                    Enabled = false;
                    CloseWriter();
                    return;
                }
                Enabled = true;
                initialized = false;
                failed = false;
                EnsureOpen();
                Info("[SpriteSound][Session] mod logging enabled");
            }
        }

        public static void Info(string message) { Write("INFO", message); }
        public static void Warning(string message) { Write("WARN", message); }
        public static void Error(string message) { Write("ERROR", message); }

        // Closing also runs when the discovery hook is destroyed, covering the end
        // of Editor Play Mode as well as quitting a standalone game. Late teardown
        // messages are ignored until BeginSession starts another run.
        public static void EndSession()
        {
            lock (Gate)
            {
                if (ended) return;
                Write("INFO", "[SpriteSound][Session] debug logging ended");
                ended = true;
                Enabled = false;
                CloseWriter();
                Application.quitting -= EndSession;
            }
        }

        // Each message line has a UTC timestamp, severity, and the original SSF
        // message; comment-style open/resume headers use a shorter form. Runtime/DSP
        // times inside playback messages still
        // describe game/audio clocks. Those clocks are not recording timestamps.
        private static void Write(string level, string message)
        {
            lock (Gate)
            {
                if (ended) return;
                if (!Enabled)
                {
                    // Preserve actionable failures in Unity's normal log, as the old
                    // release build did. Routine diagnostics remain completely quiet.
                    if (level == "ERROR") Debug.LogError(message);
                    else if (level == "WARN") Debug.LogWarning(message);
                    return;
                }
                EnsureOpen();
                if (!failed)
                {
                    try
                    {
                        string line = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                            + " [" + level + "] " + message;
                        writer.WriteLine(line);
                        return;
                    }
                    catch (Exception ex) { Fail(ex); }
                }
                // Unity chooses Editor.log in the editor and Player.log in a normal
                // standalone run. Respect that routing instead of guessing paths.
                if (level == "ERROR") Debug.LogError(message);
                else if (level == "WARN") Debug.LogWarning(message);
                else Debug.Log(message);
            }
        }

        // Opening is lazy: an off-only run must not create a directory or touch old logs.
        // Take a write lock before backing up the previous run. If backup fails, preserve
        // the current file and fall back to Unity. On re-enable, append and say "resumed"
        // so the header cannot be mistaken for a game restart or a second rotation.
        // The application gate protects logger state, not the rest of the game's objects.
        private static void EnsureOpen()
        {
            if (initialized) return;
            initialized = true;
            try
            {
                // DFU supplies the directory containing the loaded .dfmod. This
                // also follows custom mod locations instead of the process's working
                // directory or a hard-coded AppData/StreamingAssets location.
                if (string.IsNullOrWhiteSpace(modDirectory))
                    throw new InvalidOperationException("DFU did not provide the mod directory.");
                string folder = Path.Combine(Path.GetFullPath(modDirectory), "SSF", "Logs");
                Directory.CreateDirectory(folder);
                string current = Path.Combine(folder, "SSF.log");
                string previous = Path.Combine(folder, "SSF-prev.log");

                // Acquire the current file before rotating. A second game instance
                // cannot replace the previous log while the first is still writing.
                // Copy the old run before truncating it, so a failed backup leaves
                // the current log intact. No temporary or numbered parts are made.
                FileStream stream = new FileStream(current, FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.Read);
                bool reopening = rotated;
                try
                {
                    if (!rotated && stream.Length > 0)
                    {
                        using (var backup = new FileStream(previous, FileMode.Create,
                            FileAccess.Write, FileShare.Read))
                            stream.CopyTo(backup);
                    }
                    if (rotated) stream.Position = stream.Length;
                    else { stream.Position = 0; stream.SetLength(0); }
                    rotated = true;
                    writer = new StreamWriter(stream, new UTF8Encoding(false));
                    stream = null; // The writer now owns and closes the file.
                    writer.AutoFlush = true;
                    writer.WriteLine((reopening ? "# SSF logging resumed (same session) " : "# SSF log session opened ") +
                        DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                }
                finally { if (stream != null) stream.Dispose(); }
                Debug.Log("[SpriteSound] Debug log: " + current);
            }
            catch (Exception ex) { Fail(ex); }
        }

        // Logging is optional. Warn once, close the unusable file, and let Unity's
        // normal log carry subsequent messages rather than retrying disk writes on
        // every frame. A later off/on toggle deliberately permits a fresh attempt.
        // No playback state or coroutine is changed here.
        private static void Fail(Exception ex)
        {
            if (failed) return;
            failed = true;
            CloseWriter();
            Debug.LogWarning("[SpriteSound] Dedicated logging unavailable; using Unity log. " + ex.Message);
        }

        private static void CloseWriter()
        {
            var previous = writer;
            writer = null;
            if (previous != null)
            {
                try { previous.Dispose(); }
                catch (Exception) { /* Do not let shutdown of a debug file break gameplay. */ }
            }
        }
    }
}
