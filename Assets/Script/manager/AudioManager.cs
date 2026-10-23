using UnityEngine;
using UnityEngine.SceneManagement;

public class AudioManager : MonoBehaviour
{
    public static AudioManager Instance { get; private set; }

    [Header("Sources")]
    [SerializeField] private AudioSource musicSource;
    [SerializeField] private AudioSource sfxSource;

    [Header("Music Clips")]
    public AudioClip[] musicTracks;

    [Header("SFX Clips")]
    public AudioClip[] sfxClips;

    [Header("Default Volume")]
    [Range(0f, 1f)] public float musicVolume = 0.5f;
    [Range(0f, 1f)] public float sfxVolume = 1f;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }

        // The menu's AudioManager currently shares a GameObject with the
        // scene-owned LobbyManager/NetworkObject. Keep audio alive without
        // moving that network object into DontDestroyOnLoad.
        if (GetComponent<LobbyManager>() != null)
        {
            CreatePersistentAudioRoot();
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);

        SceneManager.sceneLoaded += OnSceneLoaded;

        if (musicSource == null) musicSource = gameObject.AddComponent<AudioSource>();
        if (sfxSource == null) sfxSource = gameObject.AddComponent<AudioSource>();
        musicSource.volume = musicVolume;
        sfxSource.volume = sfxVolume;
        musicSource.loop = true;
    }

    private void OnDestroy()
    {
        if (Instance != this) return;

        SceneManager.sceneLoaded -= OnSceneLoaded;
        Instance = null;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        // This track is menu ambience. The AudioManager persists for SFX and
        // settings, but menu music must not follow players into gameplay.
        if (scene.name != "Menu")
            StopMusic();
    }

    private void CreatePersistentAudioRoot()
    {
        GameObject root = new GameObject("PersistentAudioManager");
        root.SetActive(false);

        AudioSource persistentMusic = root.AddComponent<AudioSource>();
        AudioSource persistentSfx = root.AddComponent<AudioSource>();
        AudioManager persistentManager = root.AddComponent<AudioManager>();
        persistentManager.musicSource = persistentMusic;
        persistentManager.sfxSource = persistentSfx;
        persistentManager.musicTracks = musicTracks;
        persistentManager.sfxClips = sfxClips;
        persistentManager.musicVolume = musicVolume;
        persistentManager.sfxVolume = sfxVolume;

        root.SetActive(true);
        Destroy(this);
    }

    // ── MUSIC ──────────────────────────────────────────────

    public void PlayMusic(int index)
    {
        if (!IsValidMusic(index)) return;

        musicSource.clip = musicTracks[index];
        musicSource.Play();
    }

    public void PlayMusic(AudioClip clip)
    {
        if (clip == null) return;

        musicSource.clip = clip;
        musicSource.Play();
    }

    public void StopMusic()
    {
        if (musicSource != null) musicSource.Stop();
    }

    public void PauseMusic() => musicSource.Pause();

    public void ResumeMusic() => musicSource.UnPause();

    public void SetMusicVolume(float volume)
    {
        musicVolume = Mathf.Clamp01(volume);
        musicSource.volume = musicVolume;
    }

    // ── SFX ───────────────────────────────────────────────

    public void PlaySFX(int index)
    {
        if (!IsValidSFX(index)) return;

        sfxSource.PlayOneShot(sfxClips[index], sfxVolume);
    }

    public void PlaySFX(AudioClip clip)
    {
        if (clip == null) return;

        sfxSource.PlayOneShot(clip, sfxVolume);
    }

    public void SetSFXVolume(float volume)
    {
        sfxVolume = Mathf.Clamp01(volume);
        sfxSource.volume = sfxVolume;
    }

    // ── MUTE ──────────────────────────────────────────────

    public void MuteMusic(bool mute) => musicSource.mute = mute;

    public void MuteSFX(bool mute) => sfxSource.mute = mute;

    public void MuteAll(bool mute)
    {
        musicSource.mute = mute;
        sfxSource.mute = mute;
    }

    public void ToggleMuteAll()
    {
        bool mute = !musicSource.mute;
        musicSource.mute = mute;
        sfxSource.mute = mute;
    }

    public bool IsMuted => musicSource.mute;
    public bool IsMusicMuted => musicSource.mute;
    public bool IsSFXMuted => sfxSource.mute;

    // ── HELPERS ───────────────────────────────────────────

    private bool IsValidMusic(int index)
    {
        if (musicTracks == null || index < 0 || index >= musicTracks.Length)
        {
            Debug.LogWarning($"AudioManager: music index {index} không hợp lệ.");
            return false;
        }
        return true;
    }

    private bool IsValidSFX(int index)
    {
        if (sfxClips == null || index < 0 || index >= sfxClips.Length)
        {
            Debug.LogWarning($"AudioManager: sfx index {index} không hợp lệ.");
            return false;
        }
        return true;
    }
}
