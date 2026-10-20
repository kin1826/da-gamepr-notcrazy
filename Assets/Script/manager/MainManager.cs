using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using TMPro;
using Unity.Netcode;
using Unity.Services.Authentication;
using Unity.Services.Multiplayer;

/// <summary>
/// Main manager for UI flow and scene management.
/// Handles single-player vs multiplayer routing and scene loading.
/// Ensures network-synchronized gameplay for multiplayer.
/// </summary>
public class MainManager : MonoBehaviour
{
    private const float NetworkJoinTimeoutSeconds = 30f;

    public static MainManager Instance { get; private set; }

    [Header("User")]
    public TMP_Text playerNameText;
    public TMP_Text goldText;

    [Header("Panels")]
    public GameObject mainPanel;
    public GameObject multiplayerPanel;
    public GameObject dinoSelectPanel;
    public GameObject levelSelectPanel;
    public GameObject weaponPanel;
    public GameObject giftPanel;
    public GameObject rankingPanel;
    public GameObject notiPanel;

    [Header("Scene")]
    public string gameplaySceneName = "Level_01";

    [Header("Dino Sprites")]
    public Sprite[] dinoSprites;

    [Header("Room Images")]
    public Image leftDinoImage;
    public Image rightDinoImage;

    [Header("Buttons")]
    public Button findRoomButton;
    public Button joinRoomButton;
    public Button playButton;

    [Tooltip("Queue name configured in the Unity Matchmaker dashboard.")]
    public string matchmakerQueueName = "QuickMatch-2P";

    [Header("Dino Buttons")]
    public Button[] dinoButtons;

    [Header("Join")]
    public TMP_InputField roomCodeInput;
    public TMP_InputField codeDisplayInput;

    [Header("Audio")]
    public int lobbyMusicIndex = 0;
    public AudioClip clickSFX;

    private TMP_Text findRoomButtonLabel;
    private TMP_Text playButtonLabel;
    private string defaultFindRoomButtonText;
    private string defaultPlayButtonText;
    private CancellationTokenSource quickMatchCancellation;
    private Task quickMatchTask;
    private bool quickMatchSearchPending;
    private bool quickMatchCancelRequested;
    private string quickMatchCancellationReason;
    private bool isClosingMultiplayerPanel;
    private bool waitingForManualRoom;
    private bool manualJoinPending;
    private float quickMatchRetryNotBefore;

    private void Awake()
    {
        // Singleton pattern
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("Duplicate MainManager found! Destroying this instance.");
            Destroy(gameObject);
            return;
        }
        Instance = this;

        SaveManager.Load();
    }

    private void Start()
    {
        NetworkConfig.EnsureInstance();
        ValidateReferences();

        findRoomButtonLabel = findRoomButton != null ? findRoomButton.GetComponentInChildren<TMP_Text>(true) : null;
        playButtonLabel = playButton != null ? playButton.GetComponentInChildren<TMP_Text>(true) : null;
        defaultFindRoomButtonText = "Find Room";
        defaultPlayButtonText = "Play";
        if (findRoomButtonLabel != null) findRoomButtonLabel.text = defaultFindRoomButtonText;
        if (playButtonLabel != null) playButtonLabel.text = defaultPlayButtonText;

        if (joinRoomButton != null) joinRoomButton.interactable = true;
        if (findRoomButton != null) findRoomButton.interactable = true;
        if (playButton != null) playButton.interactable = false;

        if (leftDinoImage != null) leftDinoImage.gameObject.SetActive(false);
        if (rightDinoImage != null) rightDinoImage.gameObject.SetActive(false);

        AudioManager.Instance?.PlayMusic(lobbyMusicIndex);
        RefreshPlayerName();

        if (GameData.ShowTeammateDisconnectedNotice)
        {
            GameData.ShowTeammateDisconnectedNotice = false;
            if (notiPanel != null)
            {
                notiPanel.SetActive(true);
            }
            else
            {
                Debug.LogWarning("MainManager: Noti_Panel is not assigned.");
            }
        }
    }

    public void RefreshPlayerName()
    {
        if (playerNameText == null) return;
        playerNameText.text = UserSession.Current != null ? UserSession.Current.name : "";
    }

    public void RefreshGold()
    {
        if (goldText == null) return;
        goldText.text = UserSession.Current != null ? $"{UserSession.Current.gold}" : "0";
    }

    /// <summary>
    /// Validates all required UI references are assigned.
    /// </summary>
    private void ValidateReferences()
    {
        // if (mainPanel == null) Debug.LogError("MainManager: mainPanel not assigned!");
        // if (multiplayerPanel == null) Debug.LogError("MainManager: multiplayerPanel not assigned!");
        // if (dinoSelectPanel == null) Debug.LogError("MainManager: dinoSelectPanel not assigned!");
        // if (dinoSprites == null || dinoSprites.Length == 0) Debug.LogError("MainManager: dinoSprites not assigned!");
        // if (leftDinoImage == null) Debug.LogError("MainManager: leftDinoImage not assigned!");
        // if (rightDinoImage == null) Debug.LogError("MainManager: rightDinoImage not assigned!");
        // if (createCodeButton == null) Debug.LogError("MainManager: createCodeButton not assigned!");
        // if (joinRoomButton == null) Debug.LogError("MainManager: joinRoomButton not assigned!");
        // if (playButton == null) Debug.LogError("MainManager: playButton not assigned!");
        // if (roomCodeInput == null) Debug.LogError("MainManager: roomCodeInput not assigned!");
        // if (codeDisplayInput == null) Debug.LogError("MainManager: codeDisplayInput not assigned!");
    }

    private void Update()
    {
        // Only update UI if multiplayer and LobbyManager is available
        if (GameData.IsMultiplayer && LobbyManager.Instance != null)
        {
            UpdateRoomUI();
        }
    }

    public void StartGame()
    {
        gameplaySceneName = $"Level_{GameData.SelectedLevel:00}";
        Debug.Log($"MainManager: StartGame with scene {gameplaySceneName}");

        if (string.IsNullOrEmpty(gameplaySceneName))
        {
            Debug.LogError("gameplaySceneName not set!");
            return;
        }

        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsConnectedClient)
        {
            if (!NetworkManager.Singleton.IsServer)
            {
                Debug.LogWarning("Only the host can start the multiplayer game.");
                return;
            }

            // Multiplayer - use network-synchronized loading
            Debug.Log("Starting multiplayer game via network...");
            NetworkManager.Singleton.SceneManager.LoadScene(
                gameplaySceneName,
                LoadSceneMode.Single
            );
        }
        else
        {
            // Single player
            Debug.Log("Starting single player game...");
            SceneManager.LoadScene(gameplaySceneName);
        }
    }

    /// <summary>
    /// Load any scene by name using the same logic as StartGame (multiplayer host uses Netcode).
    /// </summary>
    public void LoadLevel(string sceneName)
    {
        if (string.IsNullOrEmpty(sceneName))
        {
            Debug.LogError("LoadLevel: sceneName is null or empty");
            return;
        }

        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsConnectedClient)
        {
            if (!NetworkManager.Singleton.IsServer)
            {
                Debug.LogWarning("Only the host can start the multiplayer game.");
                return;
            }

            Debug.Log($"MainManager: Loading multiplayer scene via network... {sceneName}");
            NetworkManager.Singleton.SceneManager.LoadScene(sceneName, LoadSceneMode.Single);
        }
        else
        {
            Debug.Log($"MainManager: Loading local scene... {sceneName}");
            SceneManager.LoadScene(sceneName);
        }
    }

    // =========================
    // AUDIO
    // =========================
    public void PlayClick()
    {
        if (clickSFX != null)
            AudioManager.Instance?.PlaySFX(clickSFX);
    }

    // =========================
    // SINGLE PLAYER
    // =========================
    public void OpenSinglePlayer()
    {
        GameData.IsMultiplayer = false;

        // mainPanel.SetActive(false);
        dinoSelectPanel.SetActive(true);
    }

    // =========================
    // MULTIPLAYER
    // =========================
    public void OpenMultiplayer()
    {
        GameData.IsMultiplayer = true;

        // mainPanel.SetActive(false);
        multiplayerPanel.SetActive(true);
    }

    /// <summary>
    /// Starts a two-player Matchmaker search. The MPS queue creates the session
    /// when no compatible open session is waiting to be joined.
    /// </summary>
    public void StartQuickMatchmaking()
    {
        if (quickMatchSearchPending || manualJoinPending || isClosingMultiplayerPanel)
        {
            return;
        }

        if (Time.unscaledTime < quickMatchRetryNotBefore)
        {
            if (findRoomButtonLabel != null) findRoomButtonLabel.text = "Wait Before Retry";
            Debug.LogWarning("Matchmaking is cooling down after a rate-limit response. Please wait before retrying.");
            return;
        }

        NetworkConfig networkConfig = NetworkConfig.EnsureInstance();
        if (networkConfig == null)
        {
            Debug.LogError("Cannot search for a match: NetworkConfig is missing.");
            return;
        }

        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
        {
            Debug.LogWarning("Cannot search for a match while another network session is active.");
            return;
        }

        ResetMultiplayerLobbyUI();
        GameData.IsMultiplayer = true;
        GameData.IsHost = false;
        waitingForManualRoom = false;
        networkConfig.isHost = false;
        networkConfig.relayCode = string.Empty;
        networkConfig.joinCode = string.Empty;
        quickMatchCancelRequested = false;
        quickMatchCancellationReason = null;
        quickMatchSearchPending = true;
        quickMatchCancellation = new CancellationTokenSource();

        if (findRoomButton != null) findRoomButton.interactable = true;
        if (joinRoomButton != null) joinRoomButton.interactable = false;
        if (playButton != null) playButton.interactable = false;
        if (findRoomButtonLabel != null) findRoomButtonLabel.text = "Cancel Search";
        if (playButtonLabel != null) playButtonLabel.text = defaultPlayButtonText;
        if (dinoButtons != null)
        {
            foreach (Button button in dinoButtons)
            {
                if (button != null) button.interactable = false;
            }
        }

        quickMatchTask = RunQuickMatchmakingAsync(networkConfig, quickMatchCancellation.Token);
    }

    /// <summary>
    /// Find Room toggles between starting a search and canceling its ticket.
    /// Room creation cancellation remains on the host's Play button.
    /// </summary>
    public async void OnFindRoomButtonPressed()
    {
        if (!quickMatchSearchPending)
        {
            StartQuickMatchmaking();
            return;
        }

        quickMatchCancelRequested = true;
        quickMatchCancellationReason = "Find Room button";
        Debug.LogWarning("[Matchmaking] Search cancellation requested from the Find Room button.");
        if (findRoomButton != null) findRoomButton.interactable = false;
        if (findRoomButtonLabel != null) findRoomButtonLabel.text = "Cancelling...";
        quickMatchCancellation?.Cancel();

        Task pendingTask = quickMatchTask;
        if (pendingTask != null)
        {
            await pendingTask;
        }

        if (!isClosingMultiplayerPanel)
        {
            ResetMultiplayerLobbyUI();
        }
    }

    public async void OnMultiplayerPlayButtonPressed()
    {
        if (waitingForManualRoom)
        {
            await CancelCreatedRoomAsync();
            return;
        }

        OpenLevelSelect();
    }

    private async Task CancelCreatedRoomAsync()
    {
        if (!waitingForManualRoom)
        {
            return;
        }

        waitingForManualRoom = false;
        if (playButton != null) playButton.interactable = false;
        if (playButtonLabel != null) playButtonLabel.text = "Cancelling...";

        if (LobbyManager.Instance != null && LobbyManager.Instance.IsSpawned &&
            NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
        {
            LobbyManager.Instance.ResetDinoSelections();
        }

        NetworkConfig networkConfig = NetworkConfig.Instance;
        if (networkConfig != null)
        {
            await networkConfig.LeaveActiveSessionAsync();
        }

        ResetMultiplayerLobbyUI();
        GameData.IsMultiplayer = true;
        GameData.IsHost = false;
        Debug.Log("Created room canceled and lobby state reset.");
    }

    private async Task RunQuickMatchmakingAsync(NetworkConfig networkConfig, CancellationToken cancellationToken)
    {
        bool matched = false;
        string playerId = "<unavailable>";
        string failureButtonText = null;
        try
        {
            await networkConfig.EnsureServicesInitializedAsync();
            cancellationToken.ThrowIfCancellationRequested();
            playerId = AuthenticationService.Instance.PlayerId;

            if (string.IsNullOrWhiteSpace(matchmakerQueueName))
            {
                throw new InvalidOperationException("Matchmaker queue name is empty.");
            }

            Debug.Log($"[Matchmaking] Requesting queue '{matchmakerQueueName.Trim()}' with profile " +
                $"'{networkConfig.AuthenticationProfileName}' and PlayerId '{playerId}'. " +
                "Both clients must use the same Unity project/environment and different Player IDs.");

            MatchmakerOptions matchOptions = new MatchmakerOptions
            {
                QueueName = matchmakerQueueName.Trim()
            };
            SessionOptions sessionOptions = new SessionOptions { MaxPlayers = 2 }.WithRelayNetwork();
            ISession session;
            try
            {
                Debug.Log("[Matchmaking] Checking for an existing open room before creating a Matchmaker ticket.");
                session = await MultiplayerService.Instance.MatchmakeSessionAsync(
                    new QuickJoinOptions
                    {
                        Timeout = TimeSpan.FromSeconds(2),
                        CreateSession = false
                    },
                    sessionOptions);
                Debug.Log($"[Matchmaking] Quick Join found existing session '{session.Id}'.");
            }
            catch (Exception quickJoinException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (quickMatchCancelRequested || isClosingMultiplayerPanel)
                {
                    throw new OperationCanceledException("Room search canceled before Matchmaker fallback.", quickJoinException,
                        cancellationToken);
                }

                Debug.Log($"[Matchmaking] No open room found through Quick Join ({quickJoinException.Message}); " +
                    "falling back to the two-player Matchmaker queue.");
                session = await MultiplayerService.Instance.MatchmakeSessionAsync(
                    matchOptions,
                    sessionOptions,
                    cancellationToken);
            }

            if (session == null)
            {
                throw new InvalidOperationException("Matchmaker returned an empty session.");
            }

            networkConfig.SetActiveSession(session);

            if (cancellationToken.IsCancellationRequested || quickMatchCancelRequested || isClosingMultiplayerPanel)
            {
                await networkConfig.LeaveActiveSessionAsync();
                return;
            }

            await WaitForNetworkAndLobbyAsync(session.IsHost, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            networkConfig.isHost = session.IsHost;
            EnterMultiplayerDinoSelection(session.IsHost, "quick match");

            matched = true;
            Debug.Log($"[Matchmaking] Match '{session.Id}' connected. This player is " +
                $"{(session.IsHost ? "host" : "client")}; NGO client connected={NetworkManager.Singleton.IsConnectedClient}, " +
                $"LobbyManager spawned={LobbyManager.Instance != null && LobbyManager.Instance.IsSpawned}.");
        }
        catch (OperationCanceledException)
        {
            if (quickMatchCancelRequested || cancellationToken.IsCancellationRequested)
            {
                Debug.Log($"[Matchmaking] Search canceled (reason: {quickMatchCancellationReason ?? "cancellation token"}).");
            }
            else
            {
                failureButtonText = "Try Again";
                Debug.LogWarning("[Matchmaking] Search was canceled before a match was assigned. No session was joined.");
            }
        }
        catch (Exception exception)
        {
            if (quickMatchCancelRequested || cancellationToken.IsCancellationRequested)
            {
                Debug.Log($"[Matchmaking] Search canceled (reason: {quickMatchCancellationReason ?? "cancellation token"}); " +
                    $"exception type={exception.GetType().Name}, message={exception.Message}");
            }
            else if (exception is SessionException sessionException &&
                sessionException.Error == SessionError.MatchmakerCancelled)
            {
                failureButtonText = "Try Again";
                Debug.LogWarning("[Matchmaking] The Matchmaker canceled this ticket before assigning a session. " +
                    "The character-selection screen was not opened.");
            }
            else
            {
                bool rateLimited = IsMatchmakerRateLimited(exception);
                failureButtonText = rateLimited ? "Wait Before Retry" : "Try Again";
                if (rateLimited)
                {
                    quickMatchRetryNotBefore = Time.unscaledTime + 5f;
                }

                Debug.LogError($"[Matchmaking] Failed for queue '{matchmakerQueueName}' and PlayerId '{playerId}': {exception}");
            }
        }
        finally
        {
            // MatchmakeSessionAsync owns a live ticket/polling loop. Dispose it on
            // failure too, otherwise retrying can leave old polls consuming quota.
            if (!matched && quickMatchCancellation != null && !quickMatchCancellation.IsCancellationRequested)
            {
                quickMatchCancellation.Cancel();
            }

            if (!matched && networkConfig.ActiveSession != null)
            {
                await networkConfig.LeaveActiveSessionAsync();
            }

            quickMatchSearchPending = false;

            if (!isClosingMultiplayerPanel)
            {
                if (matched)
                {
                    if (findRoomButtonLabel != null) findRoomButtonLabel.text = defaultFindRoomButtonText;
                }
                else
                {
                    ResetMultiplayerLobbyUI();
                    if (!quickMatchCancelRequested && failureButtonText != null && findRoomButtonLabel != null)
                    {
                        findRoomButtonLabel.text = failureButtonText;
                    }
                }

                if (findRoomButton != null) findRoomButton.interactable = !matched;
                if (joinRoomButton != null) joinRoomButton.interactable = !matched;
            }

            quickMatchCancellation?.Dispose();
            quickMatchCancellation = null;
            quickMatchTask = null;
        }
    }

    private static bool IsMatchmakerRateLimited(Exception exception)
    {
        for (Exception current = exception; current != null; current = current.InnerException)
        {
            if (current.Message.IndexOf("429", StringComparison.OrdinalIgnoreCase) >= 0 ||
                current.Message.IndexOf("Too Many Requests", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private void ResetMultiplayerLobbyUI()
    {
        GameData.SelectedDino = 0;
        RoomData.HostDino = -1;
        RoomData.ClientDino = -1;
        RoomData.RoomCode = string.Empty;
        manualJoinPending = false;

        if (LobbyManager.Instance != null)
        {
            LobbyManager.Instance.ResetDinoSelections();
        }

        waitingForManualRoom = false;

        if (leftDinoImage != null)
        {
            leftDinoImage.sprite = null;
            leftDinoImage.gameObject.SetActive(false);
        }
        if (rightDinoImage != null)
        {
            rightDinoImage.sprite = null;
            rightDinoImage.gameObject.SetActive(false);
        }
        if (roomCodeInput != null) roomCodeInput.text = string.Empty;
        if (codeDisplayInput != null) codeDisplayInput.text = string.Empty;
        if (dinoSelectPanel != null) dinoSelectPanel.SetActive(false);
        if (levelSelectPanel != null) levelSelectPanel.SetActive(false);
        if (joinRoomButton != null) joinRoomButton.interactable = true;
        if (findRoomButton != null) findRoomButton.interactable = true;
        if (findRoomButtonLabel != null) findRoomButtonLabel.text = defaultFindRoomButtonText;
        if (playButton != null) playButton.interactable = false;
        if (playButtonLabel != null) playButtonLabel.text = defaultPlayButtonText;

        if (dinoButtons != null)
        {
            foreach (Button button in dinoButtons)
            {
                if (button != null) button.interactable = true;
            }
        }
    }

    public void OpenLevelSelect()
    {
        levelSelectPanel.SetActive(true);

        if (LevelSelectManager.Instance != null)
        {
            LevelSelectManager.Instance.GenerateAgain();
        }
        else
        {
            Debug.LogWarning("MainManager: LevelSelectManager is not available.");
        }
    }

    // =========================
    // CREATE ROOM
    // =========================
    public async void CreateRoom()
    {
        if (quickMatchSearchPending || manualJoinPending ||
            (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening))
        {
            Debug.LogWarning("Cannot create a manual room while another matchmaking or network session is active.");
            return;
        }

        NetworkConfig networkConfig = NetworkConfig.EnsureInstance();
        if (networkConfig == null)
        {
            Debug.LogError("NetworkConfig not found!");
            return;
        }

        GameData.IsHost = true;

        try
        {
            ISession session = await networkConfig.CreateHostedSessionAsync();
            await WaitForNetworkAndLobbyAsync(true, CancellationToken.None);

            waitingForManualRoom = true;
            RoomData.RoomCode = session.Code;
            if (findRoomButton != null) findRoomButton.interactable = false;
            if (joinRoomButton != null) joinRoomButton.interactable = false;
            if (playButton != null) playButton.interactable = true;
            if (playButtonLabel != null) playButtonLabel.text = "Cancel Room";
            if (codeDisplayInput != null) codeDisplayInput.text = session.Code;
            dinoSelectPanel?.SetActive(true);
            Debug.Log($"[Room] Waiting for a player. Session '{session.Id}' is open to Quick Join; code='{session.Code}'.");
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"Failed to create room: {ex.Message}");
            if (networkConfig.ActiveSession != null)
            {
                await networkConfig.LeaveActiveSessionAsync();
            }
        }

    }

    // =========================
    // SELECT DINO
    // =========================
    public void SelectDino(int index)
    {
        // Validate dino index
        if (index < 0 || index >= dinoSprites.Length)
        {
            Debug.LogError($"Invalid dino index: {index}");
            return;
        }

        // SINGLE PLAYER
        if (!GameData.IsMultiplayer)
        {
            GameData.SelectedDino = index;

            //Save
            SaveManager.Data.selectedDino =index;
            SaveManager.Save();

            PlayerSkinManager.LocalPlayer?.SetSkin(index);
            Debug.Log($"Single player selected dino: {index}");
            OpenLevelSelect();
            // StartGame();
            return;
        }

        // MULTIPLAYER HOST
        if (GameData.IsHost)
        {
            GameData.SelectedDino = index;
            PlayerSkinManager.LocalPlayer?.SetSkin(index);

            if (LobbyManager.Instance == null)
            {
                Debug.LogError("LobbyManager not found!");
                return;
            }

            LobbyManager.Instance.SetHostDinoRpc(index);
            leftDinoImage.sprite = dinoSprites[index];
            leftDinoImage.gameObject.SetActive(true);
            dinoSelectPanel.SetActive(false);
            playButton.interactable = false;
            Debug.Log($"Host selected dino: {index}");
        }
        else
        {
            // MULTIPLAYER CLIENT
            GameData.SelectedDino = index;
            PlayerSkinManager.LocalPlayer?.SetSkin(index);

            if (LobbyManager.Instance == null)
            {
                Debug.LogError("LobbyManager not found!");
                return;
            }

            LobbyManager.Instance.SetClientDinoRpc(index);          
            rightDinoImage.sprite = dinoSprites[index];
            rightDinoImage.gameObject.SetActive(true);
            dinoSelectPanel.SetActive(false);
            playButton.interactable = false;
            Debug.Log($"Client selected dino: {index}");
            UpdateRoomUI();
        }
    }
    // =========================
    // BACK BUTTON
    // =========================
    public void BackToMainMenu()
    {
        multiplayerPanel.SetActive(false);
        dinoSelectPanel.SetActive(false);

        mainPanel.SetActive(true);
    }

    // =========================
    // CLOSE CURRENT PANEL
    // =========================
    public void CloseAllPanels()
    {
        multiplayerPanel.SetActive(false);
        dinoSelectPanel.SetActive(false);

        mainPanel.SetActive(true);
    }

    // =========================
    // CLOSE DINO PANEL
    // =========================
    public void CloseDinoPanel()
    {
        dinoSelectPanel.SetActive(false);

        if (GameData.IsMultiplayer)
        {
            multiplayerPanel.SetActive(true);
        }
        else
        {
            mainPanel.SetActive(true);
        }
    }

    // =========================
    // CLOSE MULTIPLAYER PANEL
    // =========================
    public async void CloseMultiplayerPanel()
    {
        if (isClosingMultiplayerPanel)
        {
            return;
        }

        isClosingMultiplayerPanel = true;
        quickMatchCancelRequested = true;
        quickMatchCancellationReason = "closing Multiplayer panel";
        Debug.LogWarning("[Matchmaking] Search cancellation requested because the Multiplayer panel is closing.");
        quickMatchCancellation?.Cancel();

        Task pendingMatchTask = quickMatchTask;
        if (pendingMatchTask != null)
        {
            await pendingMatchTask;
        }

        if (LobbyManager.Instance != null && LobbyManager.Instance.IsSpawned &&
            NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
        {
            LobbyManager.Instance.ResetDinoSelections();
        }

        NetworkConfig networkConfig = NetworkConfig.Instance;
        if (networkConfig != null)
        {
            await networkConfig.LeaveActiveSessionAsync();
        }

        ResetMultiplayerLobbyUI();
        GameData.IsMultiplayer = false;
        GameData.IsHost = false;

        if (multiplayerPanel != null) multiplayerPanel.SetActive(false);
        if (mainPanel != null) mainPanel.SetActive(true);

        isClosingMultiplayerPanel = false;
        quickMatchCancelRequested = false;
        Debug.Log("Multiplayer lobby closed and connection state reset.");
    }
    public void CloseLevelSelectPanel()
    {
        levelSelectPanel.SetActive(false);
    }

    public void CloseNotiPanel()
    {
        notiPanel?.SetActive(false);
    }

    // =========================
    // QUIT GAME
    // =========================
    public void QuitGame()
    {
        Debug.Log("QUIT GAME");

        #if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
        #else
        Application.Quit();
        #endif
    }

    /// <summary>
    /// Creates room code via Unity Relay service.
    /// Called automatically when creating room - no manual code generation needed.
    /// </summary>
    public void CreateRoomCode()
    {
        string currentRelayCode = NetworkConfig.Instance != null ? NetworkConfig.Instance.relayCode : RoomData.RoomCode;

        if (string.IsNullOrWhiteSpace(currentRelayCode))
        {
            Debug.LogWarning("No Relay room exists yet. Press Create Room first.");
            return;
        }

        if (codeDisplayInput != null)
        {
            codeDisplayInput.text = currentRelayCode;
        }

        Debug.Log($"Current Relay join code: {currentRelayCode}");
    }

    /// <summary>
    /// Join room using relay join code.
    /// Validates code with Unity Relay service.
    /// </summary>
    public async void JoinRoom()
    {
        if (quickMatchSearchPending || manualJoinPending ||
            (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening))
        {
            Debug.LogWarning("Cannot join a code room while another matchmaking or network session is active.");
            return;
        }

        if (roomCodeInput == null)
        {
            Debug.LogError("roomCodeInput not assigned!");
            return;
        }

        NetworkConfig networkConfig = NetworkConfig.EnsureInstance();
        if (networkConfig == null)
        {
            Debug.LogError("NetworkConfig not found!");
            return;
        }

        string inputCode = NetworkConfig.NormalizeJoinCode(roomCodeInput.text);

        if (string.IsNullOrEmpty(inputCode))
        {
            Debug.LogWarning("Please enter a room code");
            return;
        }

        roomCodeInput.text = inputCode;
        manualJoinPending = true;
        if (joinRoomButton != null) joinRoomButton.interactable = false;
        if (findRoomButton != null) findRoomButton.interactable = false;
        bool joined = false;

        try
        {
            ISession session = await networkConfig.JoinHostedSessionByCodeAsync(inputCode);
            await WaitForNetworkAndLobbyAsync(false, CancellationToken.None);
            EnterMultiplayerDinoSelection(false, "room code join");
            joined = true;
            Debug.Log($"[Room Join] Connected to session '{session.Id}'; entering shared Dino selection flow.");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to join room: {ex.Message}");
        }
        finally
        {
            manualJoinPending = false;
            if (!joined)
            {
                await networkConfig.LeaveActiveSessionAsync();
                ResetMultiplayerLobbyUI();
                GameData.IsMultiplayer = false;
                GameData.IsHost = false;
            }
        }
    }

    private async Task WaitForNetworkAndLobbyAsync(bool expectHost, CancellationToken cancellationToken)
    {
        float deadline = Time.realtimeSinceStartup + NetworkJoinTimeoutSeconds;
        while (Time.realtimeSinceStartup < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            NetworkManager networkManager = NetworkManager.Singleton;
            bool roleReady = networkManager != null && networkManager.IsConnectedClient &&
                (expectHost ? networkManager.IsServer : networkManager.IsClient && !networkManager.IsServer);
            bool lobbyReady = LobbyManager.Instance != null && LobbyManager.Instance.IsSpawned;

            if (roleReady && lobbyReady)
            {
                return;
            }

            await Task.Yield();
        }

        NetworkManager currentNetwork = NetworkManager.Singleton;
        bool listening = currentNetwork != null && currentNetwork.IsListening;
        bool connected = currentNetwork != null && currentNetwork.IsConnectedClient;
        bool lobbySpawned = LobbyManager.Instance != null && LobbyManager.Instance.IsSpawned;
        throw new TimeoutException(
            $"Timed out waiting for {(expectHost ? "host" : "client")} network/lobby readiness " +
            $"(listening={listening}, connected={connected}, LobbyManager spawned={lobbySpawned}).");
    }

    /// <summary>
    /// Shared post-connect flow for manual Relay joins and Matchmaker clients:
    /// select a Dino only after NGO and the networked lobby object are ready.
    /// </summary>
    private void EnterMultiplayerDinoSelection(bool isHost, string source)
    {
        NetworkConfig networkConfig = NetworkConfig.Instance;
        if (networkConfig != null) networkConfig.isHost = isHost;

        GameData.IsMultiplayer = true;
        GameData.IsHost = isHost;
        GameData.SelectedDino = 0;
        waitingForManualRoom = false;
        RoomData.HostDino = -1;
        RoomData.ClientDino = -1;
        RoomData.RoomCode = string.Empty;

        if (codeDisplayInput != null) codeDisplayInput.text = string.Empty;
        if (roomCodeInput != null) roomCodeInput.text = string.Empty;
        if (leftDinoImage != null)
        {
            leftDinoImage.sprite = null;
            leftDinoImage.gameObject.SetActive(false);
        }
        if (rightDinoImage != null)
        {
            rightDinoImage.sprite = null;
            rightDinoImage.gameObject.SetActive(false);
        }

        if (findRoomButton != null) findRoomButton.interactable = false;
        if (joinRoomButton != null) joinRoomButton.interactable = false;
        if (playButton != null) playButton.interactable = false;
        if (playButtonLabel != null) playButtonLabel.text = defaultPlayButtonText;
        if (dinoButtons != null)
        {
            foreach (Button button in dinoButtons)
            {
                if (button != null) button.interactable = true;
            }
        }

        if (levelSelectPanel != null) levelSelectPanel.SetActive(false);
        if (dinoSelectPanel != null) dinoSelectPanel.SetActive(true);

        if (LobbyManager.Instance != null)
        {
            UpdateDinoButtonAvailability(LobbyManager.Instance.HostDino.Value);
        }

        UpdateRoomUI();
        Debug.Log($"[Multiplayer Lobby] {source}: {(isHost ? "host" : "joiner")} entered Dino selection.");
    }

    private void UpdateDinoButtonAvailability(int hostDino)
    {
        if (dinoButtons == null) return;

        for (int i = 0; i < dinoButtons.Length; i++)
        {
            if (dinoButtons[i] != null)
            {
                dinoButtons[i].interactable = GameData.IsHost || i != hostDino;
            }
        }
    }

    /// <summary>
    /// Updates multiplayer room UI with host/client dino selections.
    /// Called every frame while in multiplayer dino select screen.
    /// </summary>
    public void UpdateRoomUI()
    {
        if (quickMatchSearchPending || isClosingMultiplayerPanel || LobbyManager.Instance == null ||
            NetworkManager.Singleton == null || !NetworkManager.Singleton.IsListening)
            return;

        int hostDino = LobbyManager.Instance.HostDino.Value;
        int clientDino = LobbyManager.Instance.ClientDino.Value;
        UpdateDinoButtonAvailability(hostDino);

        if (hostDino >= 0 && hostDino < dinoSprites.Length && leftDinoImage != null)
        {
            leftDinoImage.sprite = dinoSprites[hostDino];
            leftDinoImage.gameObject.SetActive(true);
        }

        if (clientDino >= 0 && clientDino < dinoSprites.Length && rightDinoImage != null)
        {
            rightDinoImage.sprite = dinoSprites[clientDino];
            rightDinoImage.gameObject.SetActive(true);
        }

        if (playButton != null)
        {
            bool isHost = NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;
            bool bothPlayersSelected = hostDino >= 0 && hostDino < dinoSprites.Length &&
                clientDino >= 0 && clientDino < dinoSprites.Length;

            if (waitingForManualRoom && isHost && bothPlayersSelected)
            {
                waitingForManualRoom = false;
            }

            bool canCancelRoom = waitingForManualRoom && isHost;
            playButton.interactable = canCancelRoom || (bothPlayersSelected && isHost);
            if (playButtonLabel != null)
            {
                playButtonLabel.text = canCancelRoom ? "Cancel Room" : defaultPlayButtonText;
            }
        }
    }

    public void OpenWeaponPanel()
    {
        weaponPanel.SetActive(true);
    }
    public void CloseWeaponPanel()
    {
        weaponPanel.SetActive(false);
    }

    public void OpenGiftPanel()
    {
        giftPanel.SetActive(true);
    }
    public void CloseGiftPanel()
    {
        giftPanel.SetActive(false);
    }
    
    public void OpenRankingPanel()
    {
        rankingPanel.SetActive(true);
    }
    public void CloseRankingPanel()
    {
        rankingPanel.SetActive(false);
    }
}
