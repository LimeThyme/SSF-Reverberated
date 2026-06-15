using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game; 

namespace SpriteSoundFramework
{
    [RequireComponent(typeof(AudioSource))]
    [RequireComponent(typeof(AudioLowPassFilter))] 
    public class SpriteSoundEmitter : MonoBehaviour
    {
        private AudioSource audioSource;
        private AudioLowPassFilter lowPassFilter;
        private Transform playerTransform;
        
        private Billboard billboard; 
        private int lastFrame = -1;

        private string baseKey;
        private string currentKey;
        private bool initialized = false;
        
        private bool loop = false;
        private bool isMusician = false;    
        private bool isRandomized = false;  
        private float originalVolume = 1.0f;
        private List<FrameSoundData> frameTriggers = null; 

        private float targetCutoff = 22000f; 
        private const float cutoffClear = 22000f;
        private const float cutoffMuffled = 1000f; 
        private int wallLayerMask = 1 << 0; 

        public string Key => baseKey;
        public bool IsInitialized => initialized;

        void Awake()
        {
            audioSource = GetComponent<AudioSource>();
            if (!audioSource) audioSource = gameObject.AddComponent<AudioSource>();

            lowPassFilter = GetComponent<AudioLowPassFilter>();
            if (lowPassFilter == null) lowPassFilter = gameObject.AddComponent<AudioLowPassFilter>();
            
            lowPassFilter.cutoffFrequency = cutoffClear;
            audioSource.spatialBlend = 1.0f;
            audioSource.playOnAwake = false;
            audioSource.loop = false;

            billboard = GetComponent<Billboard>();

            if (SpriteSoundManager.Instance != null)
                SpriteSoundManager.Instance.RegisterEmitter(this);
        }

        void OnEnable()
        {
            if (initialized && loop && !string.IsNullOrEmpty(currentKey))
            {
                StopAllCoroutines();
                StartCoroutine(PlaylistCoroutine(currentKey));
            }
        }

        void Start()
        {
            if (GameManager.Instance != null && GameManager.Instance.PlayerObject != null)
                playerTransform = GameManager.Instance.PlayerObject.transform;

            InvokeRepeating(nameof(SlowSensorUpdate), Random.Range(0f, 0.2f), 0.2f);
        }

        void OnDestroy() {
            if (SpriteSoundManager.Instance != null) SpriteSoundManager.Instance.UnregisterEmitter(this);
        }

        public void Initialize(string key, bool loopDefault)
        {
            StopAllCoroutines();
            if (audioSource.isPlaying) audioSource.Stop();

            baseKey = key;
            currentKey = key;
            if (string.IsNullOrEmpty(baseKey)) return;

            SpriteSoundConfigData cfg = SpriteSoundConfig.GetConfig(baseKey);
            
            if (cfg.frameSounds != null && cfg.frameSounds.Count > 0)
                frameTriggers = cfg.frameSounds;
            else
                frameTriggers = null;

            originalVolume = cfg.volume;
            audioSource.volume = originalVolume;
            
            audioSource.minDistance = Mathf.Max(0.01f, cfg.minDistance);
            audioSource.maxDistance = Mathf.Max(audioSource.minDistance + 0.1f, cfg.maxDistance);

            switch (cfg.rolloff.ToLowerInvariant())
            {
                case "linear": audioSource.rolloffMode = AudioRolloffMode.Linear; break;
                default: audioSource.rolloffMode = AudioRolloffMode.Logarithmic; break;
            }

            loop = cfg.loop;
            isMusician = cfg.isMusician;
            isRandomized = cfg.randomize;

            if (SpriteSoundManager.Instance != null)
            {
                if (isMusician)
                    currentKey = SpriteSoundManager.Instance.PickRandomMusicianTrack(baseKey);
                else if (isRandomized)
                    currentKey = SpriteSoundManager.Instance.PickRandomVariant(baseKey);
            }

            initialized = true;

            if (loop) StartCoroutine(PlaylistCoroutine(currentKey));
        }

        public void Reactivate() 
        { 
            if (!initialized) return;
            if (billboard == null) billboard = GetComponent<Billboard>();

            string targetKey = currentKey;

            if (SpriteSoundManager.Instance != null)
            {
                if (isMusician)
                    targetKey = SpriteSoundManager.Instance.PickRandomMusicianTrack(baseKey);
                else if (isRandomized && !audioSource.isPlaying)
                    targetKey = SpriteSoundManager.Instance.PickRandomVariant(baseKey);
            }

            // Sync Check: If keys match, do not restart
            bool isCorrectSong = (targetKey == currentKey);
            
            if (!isCorrectSong || (!audioSource.isPlaying && loop))
            {
                currentKey = targetKey;
                StopAllCoroutines();
                if (audioSource.isPlaying) audioSource.Stop();
                
                if (loop) StartCoroutine(PlaylistCoroutine(currentKey));
            }
        }

        IEnumerator PlaylistCoroutine(string startKey) 
        {
            string playKey = startKey;
            var mgr = SpriteSoundManager.Instance;
            currentKey = playKey;

            while (true)
            {
                if (mgr == null) yield break;

                // 1. GET CLIP
                AudioClip clip = null;
                if (!mgr.TryGetCachedClip(playKey, out clip) || clip == null)
                {
                    string path = mgr.BuildFullPathForKey(playKey);
                    if (System.IO.File.Exists(path))
                    {
                        using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip("file://" + path, AudioType.WAV))
                        {
                            yield return www.SendWebRequest();
                            if (!www.isNetworkError && !www.isHttpError)
                            {
                                clip = DownloadHandlerAudioClip.GetContent(www);
                                mgr.CacheClip(playKey, clip);
                            }
                        }
                    }
                }

                if (clip != null) 
                {
                    audioSource.clip = clip;
                    
                    if (!isMusician && !isRandomized)
                    {
                        audioSource.loop = true;
                        audioSource.time = 0f;
                        audioSource.Play();
                        yield break; 
                    }

                    // --- GLOBAL SYNC LOGIC ---
                    if (isMusician)
                    {
                        // Calculate how far into the song the "Global Band" is
                        double startTime = mgr.MusicianStartTime;
                        double now = AudioSettings.dspTime;
                        float seekTime = (float)(now - startTime);

                        // If the global band finished this song while we were loading...
                        if (seekTime >= clip.length)
                        {
                            // If I'm the first to notice, clear the global track
                            string suffix = playKey.Replace(baseKey + "-", "");
                            if (mgr.CurrentSongForMusicians == suffix)
                            {
                                mgr.ResetMusicianTrack();
                            }
                            
                            // Pick the NEW song (this will reset MusicianStartTime to NOW)
                            playKey = mgr.PickRandomMusicianTrack(baseKey);
                            
                            // Loop immediately to load the new clip
                            continue; 
                        }

                        // Perfect Sync: Jump exactly to where the band is
                        audioSource.time = seekTime;
                    }
                    else
                    {
                        audioSource.time = 0f;
                    }

                    audioSource.loop = false; 
                    audioSource.Play();

                    // 2. WAIT FOR END
                    while (audioSource.isPlaying || !audioSource.enabled)
                    {
                        yield return new WaitForSeconds(0.5f);
                    }

                    // 3. PICK NEXT SONG
                    if (isMusician)
                    {
                        string justFinishedSuffix = playKey.Replace(baseKey + "-", "");

                        if (mgr.CurrentSongForMusicians == justFinishedSuffix)
                        {
                            mgr.ResetMusicianTrack();
                        }
                        
                        playKey = mgr.PickRandomMusicianTrack(baseKey);
                    }
                    else if (isRandomized)
                    {
                        playKey = mgr.PickRandomVariant(baseKey);
                    }

                    currentKey = playKey; 
                }
                else 
                {
                    yield return new WaitForSeconds(1.0f);
                }
            }
        }

        void SlowSensorUpdate()
        {
            if (playerTransform == null || audioSource == null || !initialized) return;

            float distance = Vector3.Distance(transform.position, playerTransform.position);
            float maxDist = audioSource.maxDistance;

            float tailMultiplier = 5.0f; 
            float hardSilenceLimit = (audioSource.rolloffMode == AudioRolloffMode.Linear) 
                                     ? maxDist 
                                     : maxDist * tailMultiplier;

            bool isSmallInterior = GameManager.Instance.IsPlayerInside 
                                   && !GameManager.Instance.IsPlayerInsideDungeon 
                                   && !GameManager.Instance.IsPlayerInsideCastle;

            if (!isSmallInterior && distance > hardSilenceLimit + 2.0f)
            {
                if (audioSource.enabled) audioSource.enabled = false;
                return;
            }

            if (!audioSource.enabled) audioSource.enabled = true;

            float targetVolume = originalVolume;
            if (distance > maxDist && audioSource.rolloffMode == AudioRolloffMode.Logarithmic)
            {
                float tailProgress = Mathf.Clamp01((distance - maxDist) / (hardSilenceLimit - maxDist));
                float fadeFactor = 1.0f - (tailProgress * tailProgress);
                targetVolume = originalVolume * fadeFactor;
            }
            
            if (Mathf.Abs(audioSource.volume - targetVolume) > 0.001f)
                audioSource.volume = targetVolume;

            Vector3 src = transform.position + Vector3.up * 0.5f; 
            Vector3 pPos = playerTransform.position;
            bool isVisible = false;

            if (!Physics.Linecast(src, pPos + Vector3.up * 0.5f, wallLayerMask)) isVisible = true;
            else if (!Physics.Linecast(src, pPos + Vector3.up * 1.6f, wallLayerMask)) isVisible = true;
            else if (!Physics.Linecast(src, pPos + Vector3.up * 1.0f - playerTransform.right * 0.4f, wallLayerMask)) isVisible = true;
            else if (!Physics.Linecast(src, pPos + Vector3.up * 1.0f + playerTransform.right * 0.4f, wallLayerMask)) isVisible = true;

            targetCutoff = isVisible ? cutoffClear : cutoffMuffled;
        }

        void Update()
        {
            if (!initialized || !audioSource.enabled) return;

            if (billboard == null) {
                billboard = GetComponent<Billboard>();
                if (billboard == null) return;
            }

            if (Mathf.Abs(lowPassFilter.cutoffFrequency - targetCutoff) > 10f)
            {
                lowPassFilter.cutoffFrequency = Mathf.Lerp(lowPassFilter.cutoffFrequency, targetCutoff, Time.deltaTime * 3f);
            }

            int currentFrame = billboard.Summary.CurrentFrame;
            if (currentFrame != lastFrame)
            {
                CheckFrameTriggers(currentFrame);
                lastFrame = currentFrame;
            }
        }

        void CheckFrameTriggers(int frame)
        {
            if (frameTriggers == null) return;

            for (int i = 0; i < frameTriggers.Count; i++)
            {
                if (frameTriggers[i].frame == frame)
                {
                    string triggerKey = frameTriggers[i].soundKey;
                    if (SpriteSoundManager.Instance != null)
                        triggerKey = SpriteSoundManager.Instance.PickRandomVariant(triggerKey);
                    StartCoroutine(PlayOneShot(triggerKey));
                }
            }
        }

        IEnumerator PlayOneShot(string key)
        {
            var mgr = SpriteSoundManager.Instance;
            if (mgr == null) yield break;

            AudioClip clip = null;
            if (!mgr.TryGetCachedClip(key, out clip) || clip == null)
            {
                string path = mgr.BuildFullPathForKey(key);
                if (!System.IO.File.Exists(path)) yield break;

                using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip("file://" + path, AudioType.WAV))
                {
                    yield return www.SendWebRequest();
                    if (!www.isNetworkError && !www.isHttpError)
                    {
                        clip = DownloadHandlerAudioClip.GetContent(www);
                        mgr.CacheClip(key, clip);
                    }
                }
            }
            if (clip != null) audioSource.PlayOneShot(clip);
        }

        public void SetPaused(bool p) { if(audioSource) { if(p) audioSource.Pause(); else audioSource.UnPause(); } }
    }
}