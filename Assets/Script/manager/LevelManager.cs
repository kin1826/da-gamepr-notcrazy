using System.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

public class LevelManager : MonoBehaviour
{
    public static LevelManager Instance { get; private set; }

    [Header("Transition Panel")]
    public GameObject transitionPanel;
    public RectTransform topImage;
    public RectTransform bottomImage;
    private float transitionDuration = 0.3f;
    private float delayBeforeOpen = 0.2f;

    [Header("Respawn Panel")]
    public GameObject respawnPanel;
    public GameObject tapToContinueText;
    public GameObject waitingText;
    public GameObject teammateDeadText;
    public GameObject skipButton;

    [Header("Skip / Ads Panels")]
    public GameObject skipChoicePanel;
    public GameObject adsPanel;

    public static int DeathCount { get; private set; }

    private float halfHeight;
    private bool handledMatchDisconnect;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    private void Start()
    {
        Time.timeScale = 1f;
        halfHeight = Screen.height / 2f;
        skipButton?.SetActive(DeathCount >= 3);

        // Đặt 2 ảnh ra ngoài màn hình (trạng thái mở sẵn)
        topImage.anchoredPosition = new Vector2(0, halfHeight);
        bottomImage.anchoredPosition = new Vector2(0, -halfHeight);
        transitionPanel.SetActive(false);

        if (GameData.IsMultiplayer && NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;
        }

        // Mở màn hình sau khi scene load xong
        StartCoroutine(OpenTransition());
    }

    private void OnDestroy()
    {
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
        }
    }

    private void OnClientDisconnected(ulong clientId)
    {
        NetworkManager networkManager = NetworkManager.Singleton;
        if (!GameData.IsMultiplayer || networkManager == null || handledMatchDisconnect)
        {
            return;
        }

        if (SceneManager.GetActiveScene().name == "Menu")
        {
            return;
        }

        // The exit button owns this transition already; ignore its shutdown callbacks
        // so the menu is only loaded once.
        if (GameData.IsLeavingMatchIntentionally)
        {
            return;
        }

        handledMatchDisconnect = true;

        bool disconnectedClientIsLocal = clientId == networkManager.LocalClientId;
        bool aTeammateDisconnected = !disconnectedClientIsLocal || !networkManager.IsServer;

        if (aTeammateDisconnected)
        {
            GameData.ShowTeammateDisconnectedNotice = true;
        }

        SceneManager.LoadScene("Menu");
    }

    // ── RESPAWN PANEL ─────────────────────────────────────

    // Single player overload
    public void ShowRespawnPanel() => ShowRespawnPanel(true);

    // Multiplayer: iAmDead = true nếu chính mình chết, false nếu đồng đội chết
    public void ShowRespawnPanel(bool iAmDead)
    {
        if (respawnPanel == null) return;
        Time.timeScale = 0f;
        respawnPanel.SetActive(true);
        tapToContinueText?.SetActive(true);
        waitingText?.SetActive(false);
        teammateDeadText?.SetActive(!iAmDead);

        if (!GameData.IsMultiplayer)
        {
            DeathCount++;
            skipButton?.SetActive(DeathCount >= 3);
        }
    }

    public void ShowRespawnPanelAfterDeathShake()
    {
        if (respawnPanel == null)
        {
            return;
        }

        CameraShake cameraShake = CameraShake.Instance;
        if (cameraShake == null)
        {
            ShowRespawnPanel(true);
            return;
        }

        cameraShake.Shake(
            cameraShake.DefaultDuration,
            cameraShake.DefaultMagnitude,
            () => ShowRespawnPanel(true)
        );
    }

    public void OnSkipClicked()
    {
        Time.timeScale = 1f;
        respawnPanel?.SetActive(false);
        skipChoicePanel?.SetActive(true);
    }

    public void OnAdsFinished()
    {
        adsPanel?.SetActive(false);
        ResetDeathCount();
        string next = GetNextLevelName();
        LoadLevel(string.IsNullOrEmpty(next) ? SceneManager.GetActiveScene().name : next);
    }

    public static void ResetDeathCount() => DeathCount = 0;

    private string GetNextLevelName()
    {
        int nextIndex = SceneManager.GetActiveScene().buildIndex + 1;
        if (nextIndex >= SceneManager.sceneCountInBuildSettings) return "";
        return System.IO.Path.GetFileNameWithoutExtension(
            SceneUtility.GetScenePathByBuildIndex(nextIndex));
    }

    public void ShowWaiting()
    {
        tapToContinueText?.SetActive(false);
        waitingText?.SetActive(true);
    }

    // Called by the respawn panel button's onClick.
    public void OnTapContinue()
    {
        if (GameData.IsMultiplayer
            && NetworkManager.Singleton != null
            && NetworkManager.Singleton.IsConnectedClient)
        {
            // Show waiting state — RespawnSync will reload when both players are ready.
            ShowWaiting();
            RespawnSync.Instance?.PlayerReadyServerRpc();
        }
        else
        {
            // Single player — unpause then reload with transition.
            Time.timeScale = 1f;
            respawnPanel?.SetActive(false);
            string sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            LoadLevel(sceneName);
        }
    }

    // Called by RespawnSync on clients just before the server reloads the scene.
    public void PlayCloseTransitionOnly()
    {
        Time.timeScale = 1f;
        respawnPanel?.SetActive(false);
        if (transitionPanel != null && topImage != null && bottomImage != null)
            StartCoroutine(CloseTransition());
    }

    // ── LOAD LEVEL ────────────────────────────────────────

    public void LoadLevel(string sceneName)
    {
        if (string.IsNullOrEmpty(sceneName))
        {
            Debug.LogError("LevelManager: sceneName is null or empty");
            return;
        }

        StartCoroutine(TransitionAndLoad(sceneName));
    }

    // ── TRANSITION ────────────────────────────────────────

    private IEnumerator TransitionAndLoad(string sceneName)
    {
        if (transitionPanel != null && topImage != null && bottomImage != null)
            yield return StartCoroutine(CloseTransition());

        Debug.Log($"LevelManager: Loading scene '{sceneName}'");

        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsConnectedClient)
        {
            if (!NetworkManager.Singleton.IsServer)
            {
                Debug.LogWarning("LevelManager: Only the host can load the next level.");
                yield break;
            }
            NetworkManager.Singleton.SceneManager.LoadScene(sceneName, LoadSceneMode.Single);
        }
        else
        {
            SceneManager.LoadScene(sceneName);
        }
    }

    private IEnumerator CloseTransition()
    {
        transitionPanel.SetActive(true);

        Vector2 topStart = new Vector2(0, halfHeight);
        Vector2 topEnd = new Vector2(0, 0);
        Vector2 bottomStart = new Vector2(0, -halfHeight);
        Vector2 bottomEnd = new Vector2(0, 0);

        float elapsed = 0f;
        while (elapsed < transitionDuration)
        {
            float t = elapsed / transitionDuration;
            topImage.anchoredPosition = Vector2.Lerp(topStart, topEnd, t);
            bottomImage.anchoredPosition = Vector2.Lerp(bottomStart, bottomEnd, t);
            elapsed += Time.unscaledDeltaTime;
            yield return null;
        }

        topImage.anchoredPosition = topEnd;
        bottomImage.anchoredPosition = bottomEnd;
    }

    private IEnumerator OpenTransition()
    {
        transitionPanel.SetActive(true);

        // Đặt ở giữa trước khi mở
        topImage.anchoredPosition = Vector2.zero;
        bottomImage.anchoredPosition = Vector2.zero;

        yield return new WaitForSecondsRealtime(delayBeforeOpen);

        Vector2 topEnd = new Vector2(0, halfHeight);
        Vector2 bottomEnd = new Vector2(0, -halfHeight);

        float elapsed = 0f;
        while (elapsed < transitionDuration)
        {
            float t = elapsed / transitionDuration;
            topImage.anchoredPosition = Vector2.Lerp(Vector2.zero, topEnd, t);
            bottomImage.anchoredPosition = Vector2.Lerp(Vector2.zero, bottomEnd, t);
            elapsed += Time.unscaledDeltaTime;
            yield return null;
        }

        topImage.anchoredPosition = topEnd;
        bottomImage.anchoredPosition = bottomEnd;
        transitionPanel.SetActive(false);
    }
}
