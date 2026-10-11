// PATCH ADDITION: replaces the supplied original source's global song selection and
// per-emitter musician coroutine. One group decides when a song changes, so a
// shorter part or late arrival cannot independently switch the whole performance.
// The expected result is shared song identity and scheduled start/cursor values.
// This is timing coordination, not audio editing: matching names and duration
// cannot guarantee matching tempo, leading silence, or musical arrangement.
// The earlier tests showed one approximately 63 ms Tavern offset; scheduling
// logs alone do not prove that issue is resolved. Keep actual playback evidence.
// Provenance: compared with the user-supplied original files; see SOURCE-CHANGES.md.

// One conductor per nearby band, with optional diagnostic logging.
// The conductor chooses the song, waits for every part, and supplies a shared
// audio-clock start. Emitters still own their individual volume and 3D placement.
// It never changes DFU music, the listener, or another mod's AudioSource.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace SpriteSoundFramework
{
    internal sealed class SpriteSoundEnsembles
    {
        const double Lead = 0.15;       // Give the audio engine time to queue each part.
        const double Settle = 0.75;     // Let a discovery scan finish finding the stage.
        const double LengthTolerance = 0.10; // Reject unequal arrangements, not tiny WAV padding.
        readonly SpriteSoundManager manager;
        readonly Dictionary<string, Band> bands = new Dictionary<string, Band>();
        double nextDiscovery;

        // A Band is one live group's notebook, not a scene object or save-game record.
        // Keys remembers each member's identity when the group formed. Started remembers
        // only sources we scheduled, so cleanup never stops an unrelated replacement.
        // Clips is keyed by base key: two copies of one musician share the same decoded
        // part. Rejected lasts until regroup/reset, preventing endless bad-file retries.
        // Blocked groups remain silent; they do not choose incompatible solo fallbacks.
        sealed class Band
        {
            internal string Id, Song, PreviousSong;
            internal List<SpriteSoundEmitter> Members;
            internal Dictionary<SpriteSoundEmitter, string> Keys;
            internal List<string> Songs;
            internal readonly HashSet<string> Rejected = new HashSet<string>();
            internal readonly Dictionary<string, AudioClip> Clips = new Dictionary<string, AudioClip>();
            internal readonly Dictionary<SpriteSoundEmitter, Start> Started = new Dictionary<SpriteSoundEmitter, Start>();
            internal UnityWebRequest Request;
            internal string LoadingKey;
            internal double ReadyAfter, LoadBegan, Duration, StartDsp, Offset, NextTrace;
            internal bool Timeline, Paused, Blocked, Invalid;
        }

        // A start ticket is the emitter's generation after scheduling. If range or
        // lifecycle handling stops that emitter, its changed ticket tells us to rejoin.
        // Dsp also prevents a queued-but-not-yet-started source from looking like a fault.
        sealed class Start
        {
            internal int Ticket;
            internal double Dsp;
        }

        internal SpriteSoundEnsembles(SpriteSoundManager manager) { this.manager = manager; }

        // This runs every frame, even while the game is paused. DSP time measures
        // sound playback, independent of fast travel's accelerated game clock.
        internal void Tick(IEnumerable<SpriteSoundEmitter> emitters, bool paused)
        {
            double now = AudioSettings.dspTime;
            if (Time.realtimeSinceStartup >= nextDiscovery)
            {
                nextDiscovery = Time.realtimeSinceStartup + 0.5;
                Discover(emitters, now);
            }
            foreach (Band band in bands.Values)
            {
                // Do not let a destroyed or re-purposed sprite finish an old request.
                if (band.Invalid) continue;
                if (band.Members.Any(m => m == null || !m.EnsembleMember || m.Key != band.Keys[m]))
                {
                    Retire(band, "member-invalid");
                    band.Invalid = true;
                    nextDiscovery = 0;
                    continue;
                }
                TickBand(band, now, paused);
            }
        }

        // Deterministic, complete-link grouping: every pair must be close enough.
        // This avoids a chain of musicians joining stages across an entire town.
        // Distance cannot recognize walls; authors can use labels or smaller radii.
        // ORIGINAL COMPARISON: The original treated all musicians as one global band and trusted matching
        // suffixes without checking every member's files. Groups now form locally and
        // intersect available suffixes. A missing part can silence a group instead of
        // letting it improvise an unrelated song; use blocked/rejected logs to explain why.
        void Discover(IEnumerable<SpriteSoundEmitter> emitters, double now)
        {
            var clusters = new List<List<SpriteSoundEmitter>>();
            foreach (var member in emitters.Where(e => e != null && e.EnsembleMember).OrderBy(e => e.GetInstanceID()))
            {
                var cluster = clusters.FirstOrDefault(c => c.All(other =>
                    other.EnsembleScene == member.EnsembleScene && other.EnsembleLabel == member.EnsembleLabel &&
                    Vector3.Distance(other.transform.position, member.transform.position) <= Math.Min(other.EnsembleRadius, member.EnsembleRadius)));
                if (cluster == null) { cluster = new List<SpriteSoundEmitter>(); clusters.Add(cluster); }
                cluster.Add(member);
            }
            // Stable membership keeps the same Band and timeline. Adding, removing,
            // or changing a member creates a fresh band and settling delay. Merely
            // walking out of earshot does not change this list. This greedy grouping
            // follows sorted instance IDs; it is not a designer-authored stage map.
            var desired = new Dictionary<string, List<SpriteSoundEmitter>>();
            foreach (var cluster in clusters)
            {
                string signature = string.Join(";", cluster.Select(m => m.GetInstanceID() + ":" + m.Key).ToArray());
                desired.Add(signature, cluster);
            }
            foreach (string id in bands.Keys.ToArray())
                if (!desired.ContainsKey(id) || bands[id].Invalid)
                {
                    Retire(bands[id], "regroup");
                    bands.Remove(id);
                }
            foreach (var pair in desired)
            {
                if (bands.ContainsKey(pair.Key)) continue;
                var common = new HashSet<string>(manager.MusicianSongs(pair.Value[0].Key), StringComparer.Ordinal);
                foreach (var member in pair.Value.Skip(1)) common.IntersectWith(manager.MusicianSongs(member.Key));
                var band = new Band { Id = pair.Key, Members = pair.Value,
                    Keys = pair.Value.ToDictionary(m => m, m => m.Key),
                    Songs = common.OrderBy(s => s, StringComparer.Ordinal).ToList(), ReadyAfter = now + Settle };
                bands.Add(pair.Key, band);
                if (SpriteSoundLog.Enabled) Log(band, "formed", "commonSongs=" + string.Join(",", band.Songs.ToArray()));
                foreach (var member in band.Members)
                    if (SpriteSoundLog.Enabled) Log(band, "member", "id=" + member.GetInstanceID() + " key=" + member.Key + " label=" + member.EnsembleLabel + " radius=" + member.EnsembleRadius);
            }
        }

        // Order matters: freeze on pause, finish pending file work, select/load all
        // parts, then establish a timeline and schedule eligible members. Diagnostics
        // come last and cannot control those decisions. Disabling logs must preserve
        // these same state transitions. Group cursor = saved offset + elapsed audio time.
        // Pause saves that cursor and removes scheduled/playing sources. Resume gives
        // all audible parts a fresh shared start while retaining their song position.
        void TickBand(Band band, double now, bool paused)
        {
            if (paused != band.Paused)
            {
                if (paused)
                {
                    if (band.Timeline) band.Offset = Position(band, now);
                    StopSources(band);
                    band.Timeline = false;
                }
                band.Paused = paused;
                if (SpriteSoundLog.Enabled) Log(band, paused ? "pause" : "resume", "song=" + band.Song + " cursor=" + band.Offset.ToString("F3"));
            }
            // Complete a request even during pause, but do not schedule any sound.
            if (band.Request != null && !FinishLoad(band)) return;
            if (paused || band.Blocked || now < band.ReadyAfter) return;
            bool audible = band.Members.Any(m => m.EnsembleAudible);

            if (band.Timeline && Position(band, now) >= band.Duration)
            {
                if (SpriteSoundLog.Enabled) Log(band, "song-end", "song=" + band.Song);
                ClearSong(band);
            }
            // A band that cannot be heard does not load its next song. Its current
            // timeline still advances, allowing a returning listener to join mid-song.
            if (!audible) { StopSources(band); return; }
            if (band.Song == null)
            {
                var choices = band.Songs.Where(s => !band.Rejected.Contains(s)).ToList();
                if (choices.Count == 0)
                {
                    band.Blocked = true;
                    if (SpriteSoundLog.Enabled) Log(band, "blocked", "No playable shared song. Check suffixes, WAV loads and equal durations; no independent fallback.");
                    return;
                }
                if (choices.Count > 1) choices.Remove(band.PreviousSong);
                band.Song = choices[UnityEngine.Random.Range(0, choices.Count)];
                if (SpriteSoundLog.Enabled) Log(band, "selected", "song=" + band.Song + " choices=" + choices.Count);
            }
            foreach (string key in band.Keys.Values.Distinct())
            {
                if (band.Clips.ContainsKey(key)) continue;
                string track = key + "-" + band.Song;
                AudioClip clip;
                if (manager.TryGetCachedClip(track, out clip) && clip != null)
                {
                    band.Clips.Add(key, clip);
                    continue;
                }
                try
                {
                    band.Request = UnityWebRequestMultimedia.GetAudioClip(new Uri(manager.BuildFullPathForKey(track)).AbsoluteUri, AudioType.WAV);
                    band.LoadingKey = key;
                    band.LoadBegan = Time.realtimeSinceStartup;
                    band.Request.SendWebRequest();
                    if (SpriteSoundLog.Enabled) Log(band, "load", "track=" + track);
                }
                catch (Exception ex) { RejectSong(band, "load setup: " + ex.Message); }
                return;
            }
            // Every required part is now cached. Near-equal lengths may only differ
            // in export padding, but length alone cannot verify their musical content.
            // The shortest part sets the transition boundary; longer tails can be cut.
            // This first-start barrier is not gapless preloading of the following song.
            if (!band.Timeline)
            {
                double shortest = band.Clips.Values.Min(c => (double)c.samples / c.frequency);
                double longest = band.Clips.Values.Max(c => (double)c.samples / c.frequency);
                if (shortest <= Lead || longest - shortest > LengthTolerance)
                {
                    RejectSong(band, "duration mismatch min=" + shortest.ToString("F4") + " max=" + longest.ToString("F4"));
                    return;
                }
                band.Duration = shortest;
                if (band.Offset >= shortest) { ClearSong(band); return; }
                band.StartDsp = now + Lead;
                band.Timeline = true;
                if (SpriteSoundLog.Enabled) Log(band, "timeline", "song=" + band.Song + " dsp=" + band.StartDsp.ToString("F6") + " cursor=" + band.Offset.ToString("F3") + " duration=" + shortest.ToString("F3"));
            }
            foreach (var member in band.Members)
            {
                Start start;
                if (!member.EnsembleAudible)
                {
                    if (band.Started.Remove(member)) member.StopEnsemble();
                    continue;
                }
                bool assigned = band.Started.TryGetValue(member, out start);
                if (assigned && start.Ticket == member.EnsembleTicket && (now < start.Dsp + 0.1 || member.EnsembleSource.isPlaying)) continue;
                // New arrivals and unexpectedly stopped voices use the same song
                // cursor; they cannot choose a song or advance the band's playlist.
                double at = Math.Max(now + Lead, band.StartDsp);
                double cursor = Position(band, at);
                if (cursor >= band.Duration) continue;
                member.ScheduleEnsemble(band.Clips[member.Key], member.Key + "-" + band.Song, at, cursor);
                band.Started[member] = new Start { Ticket = member.EnsembleTicket, Dsp = at };
                if (SpriteSoundLog.Enabled) Log(band, assigned ? "recover" : "scheduled", "id=" + member.GetInstanceID() + " song=" + band.Song + " dsp=" + at.ToString("F6") + " cursor=" + cursor.ToString("F3"));
            }
            // Read cursor differences only for diagnostics. Do not repeatedly seek
            // playing clips to chase small sample-reporting differences: that could
            // introduce audible skips. A persistent offset needs investigation.
            // 0.0 ms is rounded logged state, not proof of sample-perfect speaker output.
            if (SpriteSoundLog.Enabled && now >= band.NextTrace)
            {
                band.NextTrace = now + 10;
                foreach (var pair in band.Started)
                {
                    if (now < pair.Value.Dsp) continue;
                    var source = pair.Key.EnsembleSource;
                    double cursor = source.clip != null ? (double)source.timeSamples / source.clip.frequency : 0;
                    if (SpriteSoundLog.Enabled) Log(band, "sample", "id=" + pair.Key.GetInstanceID() + " song=" + band.Song + " playing=" + source.isPlaying + " virtual=" + source.isVirtual + " driftMs=" + ((cursor - Position(band, now)) * 1000).ToString("F1"));
                }
            }
        }

        // Before the scheduled start, elapsed time is zero. This prevents a negative
        // seek when pause occurs during the 150 ms lead-in. Offset is kept through a
        // pause, but a new song resets it. Do not substitute accelerated game time here.
        static double Position(Band band, double now) { return band.Offset + Math.Max(0, now - band.StartDsp); }

        // One request per band loads missing parts in sequence. A completed request
        // may be cached during pause, but scheduling waits for TickBand's pause gate.
        // Timeout uses real elapsed time so pausing cannot leave a broken request stuck.
        // The cache is shared; disposing the request does not unload a successful clip.
        bool FinishLoad(Band band)
        {
            if (!band.Request.isDone)
            {
                if (Time.realtimeSinceStartup - band.LoadBegan > 15) RejectSong(band, "load timeout");
                return false;
            }
            try
            {
                if (band.Request.isNetworkError || band.Request.isHttpError)
                { RejectSong(band, "load failed: " + band.Request.error); return false; }
                AudioClip clip = DownloadHandlerAudioClip.GetContent(band.Request);
                if (clip == null || clip.samples <= 0 || clip.frequency <= 0)
                { RejectSong(band, "empty or invalid WAV"); return false; }
                manager.CacheClip(band.LoadingKey + "-" + band.Song, clip);
                band.Clips[band.LoadingKey] = clip;
                DisposeLoad(band);
                return true;
            }
            catch (Exception ex) { RejectSong(band, "decode failed: " + ex.Message); return false; }
        }

        // A load or duration failure rejects the song for the entire band. Delay one
        // second before considering another choice, then eventually report blocked if
        // all choices fail. The delay and rejection set prevent tight error/retry loops.
        // Fixing files on disk requires a restart to refresh the manager's file index.
        void RejectSong(Band band, string reason)
        {
            if (SpriteSoundLog.Enabled) Log(band, "rejected", "song=" + band.Song + " reason=" + reason);
            band.Rejected.Add(band.Song);
            ClearSong(band);
            band.ReadyAfter = AudioSettings.dspTime + 1;
        }

        // Cancel unfinished work before releasing its request. This is also called
        // on regroup and transitions, so an old request cannot schedule new-world audio.
        // A null request is normal for cache hits and already completed loads.
        static void DisposeLoad(Band band)
        {
            if (band.Request == null) return;
            if (!band.Request.isDone) band.Request.Abort();
            band.Request.Dispose();
            band.Request = null;
        }

        // Stop only still-owned musician sources. A sprite that has changed to an
        // animal may already have new playback, and its lifecycle handled the old stop.
        // Clear the tickets even for destroyed members; Unity may report them as null.
        static void StopSources(Band band)
        {
            foreach (var member in band.Started.Keys)
                // A new identity may already have its own non-musician playback.
                if (member != null && member.EnsembleMember && member.Key == band.Keys[member]) member.StopEnsemble();
            band.Started.Clear();
        }

        // Clear this song's source tickets and local clip references, not the shared
        // manager cache. Remember the previous suffix to avoid immediate repeats when
        // another compatible choice exists. A one-song band necessarily repeats itself.
        static void ClearSong(Band band)
        {
            StopSources(band);
            DisposeLoad(band);
            band.PreviousSong = band.Song;
            band.Song = null;
            band.Clips.Clear();
            band.Timeline = false;
            band.Offset = 0;
        }

        // The guard here protects callers that do not need formatted details; the
        // call-site guards avoid building diagnostic strings for regular hot paths.
        // Band IDs are run-local component IDs plus keys, not persistent NPC identities.
        static void Log(Band band, string action, string details)
        {
            if (!SpriteSoundLog.Enabled) return;
            SpriteSoundLog.Info("[SpriteSound][Ensemble] event=" + action + " band=" + band.Id + " " + details);
        }

        // Retirement means the group no longer owns playback, not necessarily an
        // error. Streaming, member disable, regroup and world transitions all reach here.
        // member-invalid followed by regroup can log retirement twice; cleanup is safe.
        static void Retire(Band band, string reason) { Log(band, "retired", reason); ClearSong(band); }

        // Transitions retire every band's work before the next discovery pass. The
        // manager retains its reusable clip cache. Logger on/off changes never call this;
        // resetting bands to change diagnostics would interrupt the player's audio.
        internal void Reset(string reason)
        {
            foreach (Band band in bands.Values) Retire(band, reason);
            bands.Clear();
            nextDiscovery = 0;
        }
    }
}
