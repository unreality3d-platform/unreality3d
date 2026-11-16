using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using TMPro;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Events;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace U3D
{
    public enum U3DAudioChannel { Music, Voice, Ambient, Effects }

    /// <summary>
    /// Plays a list of audio tracks, with optional play/pause, progress and title/time controls.
    /// Each track is an AudioClip in the project or a URL. In web builds a URL streams through
    /// the browser's own audio element, so long files never load into memory; its volume follows
    /// the Settings UI's Main slider times the chosen channel's slider.
    ///
    /// Public methods (Play, Pause, Stop, TogglePlayPause) are designed for UnityEvent
    /// wiring from triggers like U3DInteractTrigger or U3DEnterTrigger.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public class U3DAudioPlayer : MonoBehaviour
    {
        [Serializable]
        public class Track
        {
            [Tooltip("Shown on the controls while this track plays. Leave empty to show the clip or file name.")]
            public string title = "";

            [Tooltip("An audio file in your project. It is built into your experience, so keep it short. Used instead of the URL when both are set.")]
            public AudioClip clip;

            [Tooltip("Direct link to an .mp3, .ogg or .wav file. It streams at runtime and is not included in your build, which suits long audio.")]
            public string url = "";
        }

        [Header("Audio Source")]
        [Tooltip("Tracks play in order. Each track uses its Clip if one is set, otherwise its URL.")]
        public List<Track> tracks = new List<Track>();

        [Header("Playback Settings")]
        [Tooltip("Start playing as soon as the scene loads. Browsers hold the sound until the visitor's first click, tap or key press.")]
        public bool playOnAwake = false;

        [Tooltip("Move on to the next track when one ends")]
        public bool advanceToNextTrack = true;

        [Tooltip("With Advance To Next Track on, start the list again after the last track. With it off, repeat the current track.")]
        public bool loop = false;

        [Tooltip("Remember where each visitor stopped listening and continue from there on their next visit")]
        public bool resumePosition = false;

        [Header("Volume")]
        [Tooltip("The Settings UI volume slider that controls this player, together with the Main slider")]
        public U3DAudioChannel mixerChannel = U3DAudioChannel.Voice;

        [SerializeField, HideInInspector] private AudioMixer mixer;

        [Header("Controls UI (Optional)")]
        [Tooltip("Button that toggles play/pause. Wired automatically by the editor tool.")]
        public Button playPauseButton;

        [Tooltip("Slider for scrubbing through the track. Wired automatically by the editor tool.")]
        public Slider progressSlider;

        [Tooltip("Text showing the track title and current time / total duration. Wired automatically by the editor tool.")]
        public TextMeshProUGUI timeDisplay;

        [Tooltip("Text on the play/pause button. Updated automatically to show Play or Pause.")]
        public TextMeshProUGUI playPauseButtonText;

        [Header("Events")]
        public UnityEvent OnAudioStarted;
        public UnityEvent OnAudioFinished;
        public UnityEvent OnAudioError;

        private const string MainVolumeParameter = "MainVolume";
        private const float VolumeReadInterval = 0.25f;
        private const float SaveInterval = 3f;
        private const string PlayText = "Play";
        private const string PauseText = "Pause";
        private const string LoadingText = "Loading";
        private const string ErrorText = "Audio unavailable";
        private const string IdleTimeText = "0:00 / 0:00";
        private static readonly char[] UrlCutChars = { '?', '#' };

        private AudioSource audioSource;
        private AudioClip downloadedClip;
        private int currentIndex = -1;
        private bool wantsPlay;
        private bool streaming;
        private bool sourceStarted;
        private bool sourcePaused;
        private bool announcedStart;
        private bool errorRaised;
        private int loadToken;
        private float nextSaveTime;
        private float nextVolumeReadTime;
        private float streamVolume = 1f;
        private string lastButtonText;
        private string lastTimeText;

        public int CurrentTrackIndex => currentIndex;
        public bool IsPlaying { get; private set; }
        public bool IsLoading { get; private set; }
        public bool HasError { get; private set; }
        public float CurrentTime { get; private set; }
        public float Duration { get; private set; }

#if UNITY_WEBGL && !UNITY_EDITOR
        private int streamId;

        [DllImport("__Internal")] private static extern int U3DAudioStream_Create();
        [DllImport("__Internal")] private static extern void U3DAudioStream_Destroy(int id);
        [DllImport("__Internal")] private static extern void U3DAudioStream_Play(int id, string url, double startSeconds, double volume);
        [DllImport("__Internal")] private static extern void U3DAudioStream_Pause(int id);
        [DllImport("__Internal")] private static extern void U3DAudioStream_Resume(int id);
        [DllImport("__Internal")] private static extern void U3DAudioStream_Stop(int id);
        [DllImport("__Internal")] private static extern void U3DAudioStream_Seek(int id, double seconds);
        [DllImport("__Internal")] private static extern void U3DAudioStream_SetVolume(int id, double volume);
        [DllImport("__Internal")] private static extern double U3DAudioStream_GetTime(int id);
        [DllImport("__Internal")] private static extern double U3DAudioStream_GetDuration(int id);
        [DllImport("__Internal")] private static extern int U3DAudioStream_IsPlaying(int id);
        [DllImport("__Internal")] private static extern int U3DAudioStream_IsBuffering(int id);
        [DllImport("__Internal")] private static extern int U3DAudioStream_ConsumeEnded(int id);
        [DllImport("__Internal")] private static extern int U3DAudioStream_HasError(int id);
#endif

        void Awake()
        {
            audioSource = GetComponent<AudioSource>();
            audioSource.playOnAwake = false;
            audioSource.loop = false;
            audioSource.hideFlags = HideFlags.HideInInspector;
            RouteToMixerChannel();

#if UNITY_WEBGL && !UNITY_EDITOR
            streamId = U3DAudioStream_Create();
#endif

            WireControls();
        }

        void Start()
        {
            if (playOnAwake)
                Play();
        }

        void Update()
        {
            if (currentIndex >= 0)
            {
                bool ended = streaming ? UpdateStreamState() : UpdateSourceState();

                if (HasError && !errorRaised)
                {
                    errorRaised = true;
                    wantsPlay = false;
                    IsLoading = false;
                    OnAudioError?.Invoke();
                }

                if (IsPlaying && !announcedStart)
                {
                    announcedStart = true;
                    OnAudioStarted?.Invoke();
                }

                if (resumePosition && IsPlaying && Time.unscaledTime >= nextSaveTime)
                {
                    nextSaveTime = Time.unscaledTime + SaveInterval;
                    SavePosition();
                }

                if (ended)
                    HandleTrackEnded();
            }

            UpdateControlsUI();
        }

        void OnDisable()
        {
            Pause();
        }

        void OnApplicationPause(bool paused)
        {
            if (paused)
                SavePosition();
        }

        void OnDestroy()
        {
            if (playPauseButton != null)
                playPauseButton.onClick.RemoveListener(TogglePlayPause);
            if (progressSlider != null)
                progressSlider.onValueChanged.RemoveListener(OnSliderChanged);

            if (audioSource != null)
                StopBackend();

#if UNITY_WEBGL && !UNITY_EDITOR
            U3DAudioStream_Destroy(streamId);
#endif
        }

        // -----------------------------──────────────────
        // Configuration
        // -----------------------------──────────────────

        private void RouteToMixerChannel()
        {
            AudioMixer target = GetMixer();
            if (target == null) return;

            AudioMixerGroup[] groups = target.FindMatchingGroups(mixerChannel.ToString());
            if (groups != null && groups.Length > 0)
                audioSource.outputAudioMixerGroup = groups[0];
        }

        private AudioMixer GetMixer()
        {
            if (mixer != null) return mixer;
            if (audioSource != null && audioSource.outputAudioMixerGroup != null)
                return audioSource.outputAudioMixerGroup.audioMixer;
            return null;
        }

        private void WireControls()
        {
            if (playPauseButton != null)
                playPauseButton.onClick.AddListener(TogglePlayPause);

            if (progressSlider != null)
            {
                progressSlider.minValue = 0f;
                progressSlider.maxValue = 1f;
                progressSlider.onValueChanged.AddListener(OnSliderChanged);
            }
        }

        // -----------------------------──────────────────
        // Public Methods (for UnityEvent wiring)
        // -----------------------------──────────────────

        public void Play()
        {
            if (tracks == null || tracks.Count == 0) return;

            if (currentIndex >= 0 && !HasError)
            {
                Resume();
                return;
            }

            if (currentIndex >= 0)
            {
                PlayTrack(currentIndex, CurrentTime);
                return;
            }

            int index = 0;
            float start = 0f;
            if (resumePosition)
                LoadPosition(out index, out start);
            PlayTrack(index, start);
        }

        public void Pause()
        {
            if (currentIndex < 0 || !wantsPlay) return;
            wantsPlay = false;

            if (streaming)
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                U3DAudioStream_Pause(streamId);
#endif
            }
            else if (audioSource.isPlaying)
            {
                audioSource.Pause();
                sourcePaused = true;
            }

            SavePosition();
        }

        public void Stop()
        {
            StopBackend();
            wantsPlay = false;
            IsPlaying = false;
            IsLoading = false;
            HasError = false;
            currentIndex = -1;
            CurrentTime = 0f;
            Duration = 0f;
            ClearPosition();
        }

        public void TogglePlayPause()
        {
            if (wantsPlay)
                Pause();
            else
                Play();
        }

        // -----------------------------──────────────────
        // Playback
        // -----------------------------──────────────────

        private void PlayTrack(int index, float startSeconds)
        {
            if (tracks == null || index < 0 || index >= tracks.Count) return;

            StopBackend();
            currentIndex = index;
            CurrentTime = Mathf.Max(0f, startSeconds);
            Duration = 0f;
            HasError = false;
            IsPlaying = false;
            wantsPlay = true;

            Track track = tracks[index];
            if (track != null && track.clip != null)
            {
                IsLoading = false;
                audioSource.clip = track.clip;
                audioSource.time = Mathf.Clamp(CurrentTime, 0f, Mathf.Max(0f, track.clip.length - 0.5f));
                audioSource.Play();
                return;
            }

            string url = track != null ? NormalizeUrl(track.url) : "";
            if (string.IsNullOrEmpty(url))
            {
                IsLoading = false;
                HasError = true;
                return;
            }

            IsLoading = true;
#if UNITY_WEBGL && !UNITY_EDITOR
            streaming = true;
            ReadStreamVolume(true);
            U3DAudioStream_Play(streamId, url, CurrentTime, streamVolume);
#else
            StartCoroutine(LoadClip(url, CurrentTime, loadToken));
#endif
        }

        private void Resume()
        {
            if (wantsPlay) return;
            wantsPlay = true;

            if (streaming)
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                U3DAudioStream_Resume(streamId);
#endif
            }
            else if (!IsLoading && audioSource.clip != null)
            {
                if (sourcePaused)
                    audioSource.UnPause();
                else
                    audioSource.Play();
                sourcePaused = false;
            }
        }

        private void StopBackend()
        {
            loadToken++;

#if UNITY_WEBGL && !UNITY_EDITOR
            if (streaming)
                U3DAudioStream_Stop(streamId);
#endif
            streaming = false;

            audioSource.Stop();
            audioSource.clip = null;
            if (downloadedClip != null)
            {
                Destroy(downloadedClip);
                downloadedClip = null;
            }

            sourceStarted = false;
            sourcePaused = false;
            announcedStart = false;
            errorRaised = false;
        }

        private void HandleTrackEnded()
        {
            OnAudioFinished?.Invoke();

            int next = currentIndex + 1;
            if (advanceToNextTrack && next < tracks.Count)
                PlayTrack(next, 0f);
            else if (loop)
                PlayTrack(advanceToNextTrack ? 0 : currentIndex, 0f);
            else
                Stop();
        }

        private bool UpdateStreamState()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            ReadStreamVolume(false);
            CurrentTime = (float)U3DAudioStream_GetTime(streamId);
            Duration = (float)U3DAudioStream_GetDuration(streamId);
            IsPlaying = U3DAudioStream_IsPlaying(streamId) == 1;
            IsLoading = wantsPlay && (U3DAudioStream_IsBuffering(streamId) == 1 || (!IsPlaying && Duration <= 0f));
            HasError = U3DAudioStream_HasError(streamId) == 1;
            return U3DAudioStream_ConsumeEnded(streamId) == 1;
#else
            return false;
#endif
        }

        private bool UpdateSourceState()
        {
            if (IsLoading || audioSource.clip == null)
            {
                IsPlaying = false;
                return false;
            }

            CurrentTime = audioSource.time;
            Duration = audioSource.clip.length;
            IsPlaying = audioSource.isPlaying;
            if (IsPlaying)
                sourceStarted = true;

            return wantsPlay && sourceStarted && !IsPlaying;
        }

        private IEnumerator LoadClip(string url, float startSeconds, int token)
        {
            using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(url, GetAudioType(url)))
            {
                ((DownloadHandlerAudioClip)request.downloadHandler).compressed = true;
                yield return request.SendWebRequest();
                if (token != loadToken) yield break;

                AudioClip clip = request.result == UnityWebRequest.Result.Success
                    ? DownloadHandlerAudioClip.GetContent(request)
                    : null;

                IsLoading = false;
                if (clip == null)
                {
                    HasError = true;
                    yield break;
                }

                downloadedClip = clip;
                audioSource.clip = clip;
                audioSource.time = Mathf.Clamp(startSeconds, 0f, Mathf.Max(0f, clip.length - 0.5f));
                if (wantsPlay)
                    audioSource.Play();
            }
        }

        private void ReadStreamVolume(bool force)
        {
            if (!force && Time.unscaledTime < nextVolumeReadTime) return;
            nextVolumeReadTime = Time.unscaledTime + VolumeReadInterval;

            float volume = 1f;
            AudioMixer target = GetMixer();
            if (target != null)
            {
                if (target.GetFloat(MainVolumeParameter, out float mainDb))
                    volume *= DecibelsToLinear(mainDb);
                if (target.GetFloat(mixerChannel + "Volume", out float channelDb))
                    volume *= DecibelsToLinear(channelDb);
            }

            if (!force && Mathf.Abs(volume - streamVolume) < 0.001f) return;
            streamVolume = volume;

#if UNITY_WEBGL && !UNITY_EDITOR
            U3DAudioStream_SetVolume(streamId, streamVolume);
#endif
        }

        private static float DecibelsToLinear(float db)
        {
            return db <= -79f ? 0f : Mathf.Pow(10f, db / 20f);
        }

        // -----------------------------──────────────────
        // Controls UI
        // -----------------------------──────────────────

        private void OnSliderChanged(float value)
        {
            if (currentIndex < 0 || Duration <= 0f) return;

            float seconds = Mathf.Clamp(value * Duration, 0f, Mathf.Max(0f, Duration - 0.5f));
            CurrentTime = seconds;

            if (streaming)
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                U3DAudioStream_Seek(streamId, seconds);
#endif
            }
            else if (audioSource.clip != null)
            {
                audioSource.time = Mathf.Min(seconds, Mathf.Max(0f, audioSource.clip.length - 0.05f));
            }
        }

        private void UpdateControlsUI()
        {
            string buttonText = wantsPlay ? PauseText : PlayText;
            if (playPauseButtonText != null && buttonText != lastButtonText)
            {
                playPauseButtonText.text = buttonText;
                lastButtonText = buttonText;
            }

            if (progressSlider != null)
            {
                float progress = currentIndex >= 0 && Duration > 0f ? CurrentTime / Duration : 0f;
                progressSlider.SetValueWithoutNotify(Mathf.Clamp01(progress));
            }

            if (timeDisplay == null) return;

            string line;
            if (HasError)
                line = ErrorText;
            else if (currentIndex < 0)
                line = IdleTimeText;
            else if (IsLoading && Duration <= 0f)
                line = LoadingText;
            else
                line = FormatTime(CurrentTime) + " / " + FormatTime(Duration);

            string title = GetTrackTitle(currentIndex >= 0 ? currentIndex : 0);
            string text = string.IsNullOrEmpty(title) ? line : title + "\n" + line;

            if (text != lastTimeText)
            {
                timeDisplay.text = text;
                lastTimeText = text;
            }
        }

        private string GetTrackTitle(int index)
        {
            if (tracks == null || index < 0 || index >= tracks.Count || tracks[index] == null) return "";

            Track track = tracks[index];
            if (!string.IsNullOrWhiteSpace(track.title)) return track.title;
            if (track.clip != null) return track.clip.name;
            return FileNameFromUrl(track.url);
        }

        private static string FormatTime(float seconds)
        {
            int total = Mathf.Max(0, Mathf.FloorToInt(seconds));
            int hours = total / 3600;
            int minutes = (total / 60) % 60;
            int secs = total % 60;
            return hours > 0 ? $"{hours}:{minutes:00}:{secs:00}" : $"{minutes}:{secs:00}";
        }

        // -----------------------------──────────────────
        // URLs
        // -----------------------------──────────────────

        private static string NormalizeUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";
            url = url.Trim();

            const string wwwArchive = "://www.archive.org/";
            int i = url.IndexOf(wwwArchive, StringComparison.OrdinalIgnoreCase);
            if (i >= 0)
                url = url.Substring(0, i) + "://archive.org/" + url.Substring(i + wwwArchive.Length);

            return url;
        }

        private static string StripQuery(string url)
        {
            int cut = url.IndexOfAny(UrlCutChars);
            return cut >= 0 ? url.Substring(0, cut) : url;
        }

        private static string FileNameFromUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";
            string name = System.IO.Path.GetFileNameWithoutExtension(StripQuery(url.Trim()));
            return Uri.UnescapeDataString(name);
        }

        private static AudioType GetAudioType(string url)
        {
            string extension = System.IO.Path.GetExtension(StripQuery(url)).ToLowerInvariant();
            switch (extension)
            {
                case ".mp3": return AudioType.MPEG;
                case ".ogg":
                case ".oga": return AudioType.OGGVORBIS;
                case ".wav": return AudioType.WAV;
                default: return AudioType.UNKNOWN;
            }
        }

        // -----------------------------──────────────────
        // Resume Position
        // -----------------------------──────────────────

        private string PositionKey => "U3DAudioPlayer." + gameObject.scene.name + "." + GetHierarchyPath(transform);

        private void SavePosition()
        {
            if (!resumePosition || currentIndex < 0) return;

            string key = PositionKey;
            PlayerPrefs.SetInt(key + ".Track", currentIndex);
            PlayerPrefs.SetFloat(key + ".Time", CurrentTime);
            PlayerPrefs.Save();
        }

        private void ClearPosition()
        {
            if (!resumePosition) return;

            string key = PositionKey;
            PlayerPrefs.DeleteKey(key + ".Track");
            PlayerPrefs.DeleteKey(key + ".Time");
            PlayerPrefs.Save();
        }

        private void LoadPosition(out int index, out float seconds)
        {
            string key = PositionKey;
            index = Mathf.Clamp(PlayerPrefs.GetInt(key + ".Track", 0), 0, Mathf.Max(0, tracks.Count - 1));
            seconds = Mathf.Max(0f, PlayerPrefs.GetFloat(key + ".Time", 0f));
        }

        private static string GetHierarchyPath(Transform t)
        {
            string path = t.name;
            while (t.parent != null)
            {
                t = t.parent;
                path = t.name + "/" + path;
            }
            return path;
        }
    }
}