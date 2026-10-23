using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using TMPro;
using Unity.Netcode;
using Unity.Services.Authentication;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using Unity.Services.Matchmaker;
using Unity.Services.Matchmaker.Models;
using Unity.Services.Multiplayer;
using Unity.Services.Qos;
using Unity.Services.Qos.V2.Models;

/// <summary>
/// Main manager for UI flow and scene management.
/// Handles single-player vs multiplayer routing and scene loading.
/// Ensures network-synchronized gameplay for multiplayer.
/// </summary>
public class MainManager : MonoBehaviour
{
    private const float NetworkJoinTimeoutSeconds = 30f;
    private const float OpenRoomSearchTimeoutSeconds = 2f;
    private const int MatchCancelAssignmentGraceMilliseconds = 1100;
    private const int OpenRoomPollMilliseconds = 500;

    private enum QuickMatchPhase
    {
        Idle,
        CheckingOpenRooms,
        WaitingForMatchmaker,
        Connecting,
        InLobby,
        Canceling
    }

    private sealed class SessionNetworkStartException : Exception
    {
        public SessionNetworkStartException(SessionError error)
            : base($"The session network failed to start ({error}).")
        {
        }
    }

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
    private QuickMatchPhase quickMatchPhase = QuickMatchPhase.Idle;
    private bool quickMatchCancelRequested;
    private string quickMatchCancellationReason;
    private string quickMatchTicketId;
    private bool isClosingMultiplayerPanel;
    private bool closePanelRequestPending;
    private bool handledLobbyDisconnect;
    private bool waitingForManualRoom;
    private bool manualRoomPlayerFound;
    private bool manualRoomCreatePending;
    private bool manualRoomCancelPending;
    private bool manualJoinPending;
    private float quickMatchRetryNotBefore;
    private ISession manualRoomSession;
    private Action<string> manualRoomPlayerJoinedHandler;

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
        GameData.IsRespawning = false;
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

        RegisterLobbyDisconnectCallback();
    }

    private void OnDestroy()
    {
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnLobbyClientDisconnected;
        }
    }

    private void RegisterLobbyDisconnectCallback()
    {
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnLobbyClientDisconnected;
            NetworkManager.Singleton.OnClientDisconnectCallback += OnLobbyClientDisconnected;
        }
    }

    private void OnLobbyClientDisconnected(ulong clientId)
    {
        NetworkManager networkManager = NetworkManager.Singleton;
        if (!GameData.IsMultiplayer || GameData.IsLeavingMatchIntentionally ||
            networkManager == null || isClosingMultiplayerPanel ||
            quickMatchPhase == QuickMatchPhase.Canceling || handledLobbyDisconnect)
        {
            return;
        }

        // MainManager only owns the menu/lobby flow. Gameplay disconnects are
        // handled by LevelManager.
        if (SceneManager.GetActiveScene().name != "Menu" ||
            (!networkManager.IsServer && clientId != networkManager.LocalClientId))
        {
            return;
        }

        Debug.LogWarning($"[Multiplayer Lobby] Teammate disconnected (clientId={clientId}); returning to menu.");
        handledLobbyDisconnect = true;
        GameData.ShowTeammateDisconnectedNotice = true;
        GameData.IsLeavingMatchIntentionally = true;
        _ = HandleLobbyDisconnectAsync();
    }

    private async Task HandleLobbyDisconnectAsync()
    {
        NetworkConfig networkConfig = NetworkConfig.Instance;
        if (networkConfig != null)
        {
            GameData.IsLeavingMatchIntentionally = true;
            await networkConfig.LeaveActiveSessionAsync();
        }

        GameData.IsMultiplayer = false;
        GameData.IsHost = false;
        ResetMultiplayerLobbyUI();
        multiplayerPanel?.SetActive(false);
        dinoSelectPanel?.SetActive(false);
        mainPanel?.SetActive(true);
        GameData.IsLeavingMatchIntentionally = false;
        GameData.ShowTeammateDisconnectedNotice = false;
        notiPanel?.SetActive(true);
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
    /// Searches existing public two-player rooms first, then uses the two-player
    /// Matchmaker queue which creates a rendezvous session when a pair is found.
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
        GameData.IsLeavingMatchIntentionally = false;
        waitingForManualRoom = false;
        networkConfig.isHost = false;
        networkConfig.relayCode = string.Empty;
        networkConfig.joinCode = string.Empty;
        quickMatchCancelRequested = false;
        quickMatchCancellationReason = null;
        quickMatchTicketId = null;
        quickMatchSearchPending = true;
        quickMatchPhase = QuickMatchPhase.CheckingOpenRooms;
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

        // Once a session has been assigned, Find Room is no longer a ticket
        // cancellation control. Use the explicit lobby/exit flow instead.
        if (quickMatchPhase == QuickMatchPhase.Connecting ||
            quickMatchPhase == QuickMatchPhase.InLobby ||
            quickMatchPhase == QuickMatchPhase.Canceling)
        {
            return;
        }

        // Matchmaker assignment can arrive on the same frame as a cancel click.
        // Briefly wait for the next poll before asking the ticket flow to resolve
        // cancellation against the server's final assignment status.
        if (quickMatchPhase == QuickMatchPhase.CheckingOpenRooms ||
            quickMatchPhase == QuickMatchPhase.WaitingForMatchmaker)
        {
            if (findRoomButton != null) findRoomButton.interactable = false;
            if (findRoomButtonLabel != null) findRoomButtonLabel.text = "Checking Match...";
            await Task.Delay(MatchCancelAssignmentGraceMilliseconds);

            if (!quickMatchSearchPending || quickMatchPhase == QuickMatchPhase.Connecting ||
                quickMatchPhase == QuickMatchPhase.InLobby)
            {
                return;
            }
        }

        quickMatchCancelRequested = true;
        quickMatchCancellationReason = "Find Room button";
        bool safeToResolveCancellation = quickMatchPhase == QuickMatchPhase.CheckingOpenRooms ||
            quickMatchPhase == QuickMatchPhase.WaitingForMatchmaker;
        if (!safeToResolveCancellation)
        {
            quickMatchPhase = QuickMatchPhase.Canceling;
            quickMatchCancellation?.Cancel();
        }
        Debug.LogWarning("[Matchmaking] Search cancellation requested from the Find Room button.");
        if (findRoomButton != null) findRoomButton.interactable = false;
        if (findRoomButtonLabel != null)
            findRoomButtonLabel.text = safeToResolveCancellation ? "Checking Match..." : "Cancelling...";

        Task pendingTask = quickMatchTask;
        if (pendingTask != null)
        {
            await pendingTask;
        }

        if (!isClosingMultiplayerPanel && quickMatchPhase != QuickMatchPhase.InLobby &&
            quickMatchPhase != QuickMatchPhase.Connecting)
        {
            ResetMultiplayerLobbyUI();
        }
    }

    public async void OnMultiplayerPlayButtonPressed()
    {
        if (quickMatchPhase == QuickMatchPhase.Connecting)
        {
            return;
        }

        if (waitingForManualRoom)
        {
            await CancelCreatedRoomAsync();
            return;
        }

        UnsubscribeManualRoomEvents();
        OpenLevelSelect();
    }

    private async Task CancelCreatedRoomAsync()
    {
        if (!waitingForManualRoom || manualRoomCancelPending)
        {
            return;
        }

        manualRoomCancelPending = true;
        if (playButton != null) playButton.interactable = false;
        if (playButtonLabel != null) playButtonLabel.text = "Checking Room...";

        ISession session = manualRoomSession ?? NetworkConfig.Instance?.ActiveSession;
        if (session != null)
        {
            try
            {
                await session.RefreshAsync();
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Could not verify room membership before cancellation: {exception.Message}");
                if (waitingForManualRoom && !manualRoomPlayerFound)
                {
                    if (playButton != null) playButton.interactable = true;
                    if (playButtonLabel != null) playButtonLabel.text = "Cancel Room";
                }
                manualRoomCancelPending = false;
                return;
            }

            if (manualRoomPlayerFound || session.Players.Count > 1)
            {
                OnManualRoomPlayerJoined(null);
                Debug.Log("[Room] A second player joined before room cancellation; keeping the room and disabling Cancel Room.");
                manualRoomCancelPending = false;
                return;
            }
        }

        waitingForManualRoom = false;
        UnsubscribeManualRoomEvents();
        if (playButtonLabel != null) playButtonLabel.text = "Cancelling...";

        if (LobbyManager.Instance != null && LobbyManager.Instance.IsSpawned &&
            NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
        {
            LobbyManager.Instance.ResetDinoSelections();
        }

        NetworkConfig networkConfig = NetworkConfig.Instance;
        if (networkConfig != null)
        {
            GameData.IsLeavingMatchIntentionally = true;
            await networkConfig.LeaveActiveSessionAsync();
        }

        ResetMultiplayerLobbyUI();
        GameData.IsMultiplayer = true;
        GameData.IsHost = false;
        GameData.IsLeavingMatchIntentionally = false;
        manualRoomCancelPending = false;
        Debug.Log("Created room canceled and lobby state reset.");
    }

    private async Task RunQuickMatchmakingAsync(NetworkConfig networkConfig, CancellationToken cancellationToken)
    {
        bool matched = false;
        CancellationTokenSource searchCancellation = quickMatchCancellation;
        IMultiplayerService observedMultiplayerService = null;
        string playerId = "<unavailable>";
        string failureButtonText = null;
        try
        {
            await networkConfig.EnsureServicesInitializedAsync();
            cancellationToken.ThrowIfCancellationRequested();
            observedMultiplayerService = MultiplayerService.Instance;
            if (observedMultiplayerService == null)
            {
                throw new InvalidOperationException("Multiplayer Services is not available after initialization.");
            }

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
            quickMatchPhase = QuickMatchPhase.CheckingOpenRooms;
            Debug.Log("[Matchmaking] Checking for an existing open room before creating a Matchmaker ticket.");
            ISession session = await TryJoinOpenRoomAsync(observedMultiplayerService, sessionOptions);
            if (session == null)
            {
                if (quickMatchCancelRequested || cancellationToken.IsCancellationRequested || isClosingMultiplayerPanel)
                {
                    throw new OperationCanceledException("Search canceled before finding an open room or Matchmaker assignment.",
                        cancellationToken);
                }

                Debug.Log("[Matchmaking] No joinable open room found; falling back to the two-player Matchmaker queue.");
                quickMatchPhase = QuickMatchPhase.WaitingForMatchmaker;
                if (findRoomButton != null) findRoomButton.interactable = true;
                if (findRoomButtonLabel != null) findRoomButtonLabel.text = "Cancel Search";
                session = await RunMatchmakerTicketAsync(playerId, matchOptions, sessionOptions);
            }

            if (session == null)
            {
                throw new InvalidOperationException("Matchmaker returned an empty session.");
            }

            SetQuickMatchConnectingUI();
            networkConfig.SetActiveSession(session);

            if (cancellationToken.IsCancellationRequested || quickMatchCancelRequested || isClosingMultiplayerPanel)
            {
                GameData.IsLeavingMatchIntentionally = true;
                await networkConfig.LeaveActiveSessionAsync();
                GameData.IsLeavingMatchIntentionally = false;
                return;
            }

            // Once the service has assigned a session, allow its Relay setup to
            // finish or fail before leaving. Canceling here can race Relay's
            // in-flight JoinAllocation request and produce an unhandled SDK error.
            await WaitForNetworkAndLobbyAsync(session, session.IsHost, CancellationToken.None);
            if (quickMatchCancelRequested || isClosingMultiplayerPanel)
            {
                GameData.IsLeavingMatchIntentionally = true;
                await networkConfig.LeaveActiveSessionAsync();
                GameData.IsLeavingMatchIntentionally = false;
                return;
            }

            networkConfig.isHost = session.IsHost;
            EnterMultiplayerDinoSelection(session.IsHost, "quick match");

            matched = true;
            quickMatchPhase = QuickMatchPhase.InLobby;
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
        catch (SessionNetworkStartException exception)
        {
            failureButtonText = "Try Again";
            Debug.LogWarning($"[Matchmaking] Relay/network startup failed; the assigned session will be cleaned up. {exception.Message}");
        }
        catch (Exception exception)
        {
            if ((quickMatchCancelRequested || cancellationToken.IsCancellationRequested) && IsTicketMissing(exception))
            {
                // A status request can overlap the ticket deletion during the
                // final cancel check. The resulting 21404 is an expected race.
                Debug.Log("[Matchmaking] Ticket disappeared during cancellation; treating EntityNotFound as canceled search.");
            }
            else if (quickMatchCancelRequested || cancellationToken.IsCancellationRequested)
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
            // Matchmaker tickets are explicitly deleted by RunMatchmakerTicketAsync
            // after a final status check; no ticket may remain after a failed search.
            if (!matched && !string.IsNullOrWhiteSpace(quickMatchTicketId))
            {
                await DeleteQuickMatchTicketQuietlyAsync(quickMatchTicketId);
                quickMatchTicketId = null;
            }
            if (!matched && searchCancellation != null && !searchCancellation.IsCancellationRequested)
            {
                searchCancellation.Cancel();
            }

            if (!matched && networkConfig.ActiveSession != null)
            {
                GameData.IsLeavingMatchIntentionally = true;
                await networkConfig.LeaveActiveSessionAsync();
                GameData.IsLeavingMatchIntentionally = false;
            }

            quickMatchSearchPending = false;
            quickMatchPhase = matched ? QuickMatchPhase.InLobby : QuickMatchPhase.Idle;

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

            searchCancellation?.Dispose();
            if (quickMatchCancellation == searchCancellation)
            {
                quickMatchCancellation = null;
            }
            quickMatchTask = null;
        }
    }

    private async Task<ISession> TryJoinOpenRoomAsync(IMultiplayerService multiplayerService, SessionOptions sessionOptions)
    {
        float deadline = Time.realtimeSinceStartup + OpenRoomSearchTimeoutSeconds;
        while (Time.realtimeSinceStartup <= deadline)
        {
            QueryResponse response = null;
            if (!quickMatchCancelRequested && !isClosingMultiplayerPanel)
            {
                try
                {
                    response = await LobbyService.Instance.QueryLobbiesAsync(new QueryLobbiesOptions
                    {
                        Count = 100,
                        Filters = new List<QueryFilter>
                        {
                            new QueryFilter(QueryFilter.FieldOptions.AvailableSlots, "0", QueryFilter.OpOptions.GT),
                            new QueryFilter(QueryFilter.FieldOptions.MaxPlayers, sessionOptions.MaxPlayers.ToString(), QueryFilter.OpOptions.EQ)
                        }
                    });
                }
                catch (Exception exception)
                {
                    if (!quickMatchCancelRequested && !isClosingMultiplayerPanel)
                    {
                        Debug.LogWarning($"[Matchmaking] Could not query open rooms ({exception.Message}); continuing to Matchmaker.");
                    }
                    return null;
                }
            }

            if (response?.Results != null)
            {
                foreach (Unity.Services.Lobbies.Models.Lobby lobby in response.Results)
                {
                    if (lobby == null || lobby.IsPrivate || lobby.IsLocked || lobby.AvailableSlots <= 0 ||
                        lobby.MaxPlayers != sessionOptions.MaxPlayers || string.IsNullOrWhiteSpace(lobby.Id))
                    {
                        continue;
                    }

                    // A public room candidate is the point of no-cancel: show the state
                    // before asking MPS to join the lobby and start its Relay modules.
                    bool cancellationWasPending = quickMatchCancelRequested;
                    SetQuickMatchConnectingUI();
                    Debug.Log($"[Matchmaking] Found open room '{lobby.Id}'. Joining before Matchmaker fallback.");
                    try
                    {
                        return await multiplayerService.JoinSessionByIdAsync(lobby.Id, new JoinSessionOptions
                        {
                            Type = sessionOptions.Type,
                            PlayerProperties = sessionOptions.PlayerProperties
                        });
                    }
                    catch (SessionException exception) when (
                        exception.Error == SessionError.SessionNotFound ||
                        exception.Error == SessionError.SessionDeleted ||
                        exception.Error == SessionError.SessionConflict)
                    {
                        Debug.LogWarning($"[Matchmaking] Open room '{lobby.Id}' was no longer joinable ({exception.Error}); continuing search.");
                        if (cancellationWasPending)
                        {
                            quickMatchCancelRequested = true;
                            return null;
                        }

                        SetQuickMatchSearchingUI(QuickMatchPhase.CheckingOpenRooms);
                    }
                }
            }

            if (quickMatchCancelRequested || isClosingMultiplayerPanel || Time.realtimeSinceStartup >= deadline)
            {
                break;
            }

            await Task.Delay(OpenRoomPollMilliseconds);
        }

        return null;
    }

    private void SetQuickMatchSearchingUI(QuickMatchPhase phase)
    {
        quickMatchPhase = phase;
        if (findRoomButton != null) findRoomButton.interactable = true;
        if (findRoomButtonLabel != null) findRoomButtonLabel.text = "Cancel Search";
    }

    private async Task<ISession> RunMatchmakerTicketAsync(string playerId, MatchmakerOptions matchOptions,
        SessionOptions sessionOptions)
    {
        string queueName = matchOptions.QueueName;
        List<QosResult> qosResults = await GetMatchmakerQosResultsAsync(queueName);
        if (quickMatchCancelRequested || isClosingMultiplayerPanel)
        {
            throw new OperationCanceledException("Search canceled before creating a Matchmaker ticket.");
        }

        var player = new Unity.Services.Matchmaker.Models.Player(playerId, null, qosResults);
        var ticketOptions = new CreateTicketOptions(queueName, matchOptions.TicketAttributes);
        IMatchmakerService matchmakerService = MatchmakerService.Instance;
        CreateTicketResponse ticket = await matchmakerService.CreateTicketAsync(
            new List<Unity.Services.Matchmaker.Models.Player> { player }, ticketOptions);
        if (ticket == null || string.IsNullOrWhiteSpace(ticket.Id))
        {
            throw new InvalidOperationException("Matchmaker created a ticket without an ID.");
        }

        quickMatchTicketId = ticket.Id;
        Debug.Log($"[Matchmaking] Created ticket '{ticket.Id}' for queue '{queueName}'.");
        while (true)
        {
            if (quickMatchCancelRequested || isClosingMultiplayerPanel)
            {
                TicketStatusResponse finalStatus;
                try
                {
                    finalStatus = await matchmakerService.GetTicketAsync(ticket.Id);
                }
                catch (Exception exception) when (IsTicketMissing(exception))
                {
                    Debug.Log("[Matchmaking] Ticket was already consumed while cancellation was requested.");
                    quickMatchTicketId = null;
                    throw new OperationCanceledException("The ticket disappeared during cancellation.", exception);
                }
                catch (Exception exception)
                {
                    // Do not delete a ticket while its assignment state is
                    // unknown: the other player may already have been assigned.
                    Debug.LogWarning($"[Matchmaking] Could not verify ticket before cancellation ({exception.Message}); retrying status check.");
                    await Task.Delay(1000);
                    continue;
                }

                if (TryGetFoundMatchId(finalStatus, out string assignedMatchId))
                {
                    quickMatchCancelRequested = false;
                    quickMatchTicketId = null;
                    SetQuickMatchConnectingUI();
                    Debug.Log($"[Matchmaking] Match '{assignedMatchId}' was assigned during cancellation check; continuing to connect.");
                    return await JoinAssignedMatchAsync(MultiplayerService.Instance, assignedMatchId, sessionOptions);
                }

                try
                {
                    await matchmakerService.DeleteTicketAsync(ticket.Id);
                    quickMatchTicketId = null;
                    Debug.Log($"[Matchmaking] Deleted unassigned ticket '{ticket.Id}' after cancellation check.");
                }
                catch (Exception exception) when (IsTicketMissing(exception))
                {
                    // Assignment can win between the status read and DELETE. Read once
                    // more before treating the ticket as canceled.
                    try
                    {
                        TicketStatusResponse racedStatus = await matchmakerService.GetTicketAsync(ticket.Id);
                        if (TryGetFoundMatchId(racedStatus, out assignedMatchId))
                        {
                            quickMatchCancelRequested = false;
                            quickMatchTicketId = null;
                            SetQuickMatchConnectingUI();
                            return await JoinAssignedMatchAsync(MultiplayerService.Instance, assignedMatchId, sessionOptions);
                        }
                    }
                    catch (Exception statusException) when (IsTicketMissing(statusException))
                    {
                        // No live ticket and no published assignment: cancellation won.
                        quickMatchTicketId = null;
                    }
                    catch (Exception statusException)
                    {
                        Debug.LogWarning($"[Matchmaking] Could not resolve ticket deletion race ({statusException.Message}); retrying status check.");
                        await Task.Delay(1000);
                        continue;
                    }
                }
                catch (Exception exception)
                {
                    // The delete request may have reached the service even if
                    // its response did not. Re-read the ticket before deciding.
                    Debug.LogWarning($"[Matchmaking] Ticket cancellation response was uncertain ({exception.Message}); retrying status check.");
                    await Task.Delay(1000);
                    continue;
                }

                quickMatchTicketId = null;
                throw new OperationCanceledException("Matchmaking search canceled before an assignment was found.");
            }

            TicketStatusResponse status;
            try
            {
                status = await matchmakerService.GetTicketAsync(ticket.Id);
            }
            catch (Exception exception) when (IsTicketMissing(exception))
            {
                quickMatchTicketId = null;
                throw;
            }
            catch (Exception exception)
            {
                // Preserve the live ticket and retry instead of cleaning it up
                // while a match assignment could be in flight.
                Debug.LogWarning($"[Matchmaking] Ticket status check failed ({exception.Message}); retrying.");
                await Task.Delay(1000);
                continue;
            }

            if (TryGetFoundMatchId(status, out string matchId))
            {
                // Do this synchronously on the Unity context before starting the
                // session join, which can trigger a Relay allocation request.
                quickMatchTicketId = null;
                SetQuickMatchConnectingUI();
                Debug.Log($"[Matchmaking] Ticket '{ticket.Id}' assigned match '{matchId}'. Connecting now.");
                return await JoinAssignedMatchAsync(MultiplayerService.Instance, matchId, sessionOptions);
            }

            if (IsMatchmakerAssignmentFailure(status, out string failure))
            {
                throw new InvalidOperationException($"Matchmaker ticket '{ticket.Id}' failed: {failure}");
            }

            await Task.Delay(1000);
        }
    }

    private static async Task DeleteQuickMatchTicketQuietlyAsync(string ticketId)
    {
        try
        {
            await MatchmakerService.Instance.DeleteTicketAsync(ticketId);
            Debug.Log($"[Matchmaking] Deleted leftover ticket '{ticketId}'.");
        }
        catch (Exception exception) when (IsTicketMissing(exception))
        {
            Debug.Log($"[Matchmaking] Ticket '{ticketId}' was already removed.");
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"[Matchmaking] Could not clean up ticket '{ticketId}': {exception.Message}");
        }
    }

    private static async Task<List<QosResult>> GetMatchmakerQosResultsAsync(string queueName)
    {
        IQosService qosService = QosService.Instance;
        IList<Unity.Services.Qos.V2.Models.QosServer> servers = await qosService.GetAllServersAsync();
        if (servers == null) throw new InvalidOperationException("Could not retrieve QoS servers for Matchmaker.");

        const string unknownRegion = "unknown-region";
        string GetRegion(QosServerAnnotations annotations)
        {
            if (annotations?.RelayRegionId != null && annotations.RelayRegionId.Count > 0) return annotations.RelayRegionId[0];
            if (annotations?.MultiplayRegionId != null && annotations.MultiplayRegionId.Count > 0) return annotations.MultiplayRegionId[0];
            return unknownRegion;
        }

        var queueServers = servers.Where(server => server.Annotations?.MatchmakerQueueName != null)
            .Where(server => server.Annotations.MatchmakerQueueName.Contains(queueName))
            .Where(server => GetRegion(server.Annotations) != unknownRegion).ToList();
        if (queueServers.Count == 0) return new List<QosResult>();

        var measurements = await qosService.GetQosResultsAsync(queueServers);
        if (measurements == null) throw new InvalidOperationException("Could not measure QoS for Matchmaker.");

        return measurements.Where(result => result.Item2.AverageLatencyMs != int.MaxValue &&
                result.Item2.AverageLatencyMs >= 0 && result.Item2.PacketLossPercent >= 0.0 && result.Item2.PacketLossPercent < 1.0)
            .GroupBy(result => GetRegion(result.Item1.Annotations))
            .Select(group => new QosResult(group.Key, group.Average(item => item.Item2.PacketLossPercent),
                group.Average(item => item.Item2.AverageLatencyMs), ExtractMatchmakerPoolId(group.First().Item1.Annotations)))
            .OrderBy(result => result.Latency).ThenBy(result => result.PacketLoss).ToList();
    }

    private static Dictionary<string, List<string>> ExtractMatchmakerPoolId(QosServerAnnotations annotations)
    {
        if (annotations?.MatchmakerPoolId == null || annotations.MatchmakerPoolId.Count == 0) return null;
        return new Dictionary<string, List<string>> { { "MatchmakerPoolId", annotations.MatchmakerPoolId } };
    }

    private static bool TryGetFoundMatchId(TicketStatusResponse response, out string matchId)
    {
        matchId = null;
        if (response?.Value is MatchIdAssignment assignment && assignment.Status == MatchIdAssignment.StatusOptions.Found &&
            !string.IsNullOrWhiteSpace(assignment.MatchId))
        {
            matchId = assignment.MatchId;
            return true;
        }

        return false;
    }

    private static bool IsMatchmakerAssignmentFailure(TicketStatusResponse response, out string message)
    {
        message = null;
        if (response?.Value is NoneAssignment assignment && assignment.Status != NoneAssignment.StatusOptions.InProgress)
        {
            message = $"{assignment.Status}: {assignment.Message}";
            return true;
        }

        if (response?.Value is MatchIdAssignment matchAssignment &&
            (matchAssignment.Status == MatchIdAssignment.StatusOptions.Failed || matchAssignment.Status == MatchIdAssignment.StatusOptions.Timeout))
        {
            message = $"{matchAssignment.Status}: {matchAssignment.Message}";
            return true;
        }

        return false;
    }

    private static Task<ISession> JoinAssignedMatchAsync(IMultiplayerService multiplayerService, string matchId,
        SessionOptions sessionOptions)
    {
        // MatchId assignments are rendezvous IDs: the first assigned player
        // creates the Lobby and the other joins it. This mirrors the SDK's
        // MatchIdAssignment flow and avoids a SessionNotFound race.
        return multiplayerService.CreateOrJoinSessionAsync(matchId, sessionOptions);
    }

    private void SetQuickMatchConnectingUI()
    {
        quickMatchCancelRequested = false;
        quickMatchPhase = QuickMatchPhase.Connecting;
        if (findRoomButton != null) findRoomButton.interactable = false;
        if (findRoomButtonLabel != null) findRoomButtonLabel.text = "Connecting...";
    }

    private static bool IsTicketMissing(Exception exception)
    {
        for (Exception current = exception; current != null; current = current.InnerException)
        {
            string message = current.Message ?? string.Empty;
            if (message.IndexOf("EntityNotFound", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("Ticket not found", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("21404", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
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
        UnsubscribeManualRoomEvents();
        GameData.SelectedDino = 0;
        RoomData.HostDino = -1;
        RoomData.ClientDino = -1;
        RoomData.RoomCode = string.Empty;
        manualJoinPending = false;
        manualRoomCreatePending = false;
        manualRoomCancelPending = false;

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
        if (manualRoomCreatePending || quickMatchSearchPending || manualJoinPending ||
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

        manualRoomCreatePending = true;
        if (findRoomButton != null) findRoomButton.interactable = false;
        if (joinRoomButton != null) joinRoomButton.interactable = false;

        GameData.IsHost = true;

        try
        {
            ISession session = await networkConfig.CreateHostedSessionAsync();
            manualRoomSession = session;
            manualRoomPlayerFound = session.PlayerCount > 1;
            manualRoomPlayerJoinedHandler = OnManualRoomPlayerJoined;
            session.PlayerJoined += manualRoomPlayerJoinedHandler;
            if (manualRoomPlayerFound)
            {
                OnManualRoomPlayerJoined(null);
            }

            await WaitForNetworkAndLobbyAsync(session, true, CancellationToken.None);

            manualRoomCreatePending = false;
            waitingForManualRoom = !manualRoomPlayerFound && session.PlayerCount <= 1;
            RoomData.RoomCode = session.Code;
            if (findRoomButton != null) findRoomButton.interactable = false;
            if (joinRoomButton != null) joinRoomButton.interactable = false;
            if (playButton != null) playButton.interactable = waitingForManualRoom;
            if (playButtonLabel != null)
                playButtonLabel.text = waitingForManualRoom ? "Cancel Room" : "Connecting...";
            if (codeDisplayInput != null) codeDisplayInput.text = session.Code;
            dinoSelectPanel?.SetActive(true);
            Debug.Log($"[Room] Waiting for a player. Session '{session.Id}' is open to Quick Join; code='{session.Code}'.");
        }
        catch (System.Exception ex)
        {
            manualRoomCreatePending = false;
            Debug.LogError($"Failed to create room: {ex.Message}");
            UnsubscribeManualRoomEvents();
            if (networkConfig.ActiveSession != null)
            {
                await networkConfig.LeaveActiveSessionAsync();
            }
            ResetMultiplayerLobbyUI();
            GameData.IsHost = false;
        }

    }

    private void OnManualRoomPlayerJoined(string playerId)
    {
        manualRoomPlayerFound = true;
        waitingForManualRoom = false;
        if (playButton != null) playButton.interactable = false;
        if (playButtonLabel != null) playButtonLabel.text = "Connecting...";
        Debug.Log("[Room] A player joined the created room; Cancel Room is now disabled.");
    }

    private void UnsubscribeManualRoomEvents()
    {
        if (manualRoomSession != null && manualRoomPlayerJoinedHandler != null)
        {
            manualRoomSession.PlayerJoined -= manualRoomPlayerJoinedHandler;
        }

        manualRoomSession = null;
        manualRoomPlayerJoinedHandler = null;
        manualRoomPlayerFound = false;
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
        if (quickMatchPhase == QuickMatchPhase.Connecting)
        {
            return;
        }
        if (quickMatchSearchPending || (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening))
        {
            CloseMultiplayerPanel();
            return;
        }

        multiplayerPanel.SetActive(false);
        dinoSelectPanel.SetActive(false);

        mainPanel.SetActive(true);
    }

    // =========================
    // CLOSE CURRENT PANEL
    // =========================
    public void CloseAllPanels()
    {
        if (quickMatchPhase == QuickMatchPhase.Connecting)
        {
            return;
        }
        if (quickMatchSearchPending || (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening))
        {
            CloseMultiplayerPanel();
            return;
        }

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
        if (isClosingMultiplayerPanel || closePanelRequestPending)
        {
            return;
        }

        if (manualJoinPending || manualRoomCreatePending || manualRoomCancelPending)
        {
            Debug.Log("[Multiplayer] Ignoring panel close while a manual join/create operation is connecting.");
            return;
        }

        if (quickMatchPhase == QuickMatchPhase.Connecting)
        {
            Debug.Log("[Matchmaking] Ignoring panel close while an assigned match is connecting.");
            return;
        }

        if (quickMatchSearchPending && quickMatchPhase != QuickMatchPhase.InLobby)
        {
            closePanelRequestPending = true;
            quickMatchCancelRequested = true;
            quickMatchCancellationReason = "closing Multiplayer panel";
            Debug.LogWarning("[Matchmaking] Closing the panel requested a safe search cancellation.");
            if (quickMatchPhase != QuickMatchPhase.CheckingOpenRooms &&
                quickMatchPhase != QuickMatchPhase.WaitingForMatchmaker)
            {
                quickMatchPhase = QuickMatchPhase.Canceling;
                quickMatchCancellation?.Cancel();
            }

            Task pendingMatchTask = quickMatchTask;
            if (pendingMatchTask != null)
            {
                await pendingMatchTask;
            }

            // If matchmaking won the race, keep both the connection and the
            // selection screen. Closing the panel must not orphan the partner.
            if (quickMatchPhase == QuickMatchPhase.Connecting || quickMatchPhase == QuickMatchPhase.InLobby)
            {
                closePanelRequestPending = false;
                Debug.Log("[Matchmaking] A match was found while closing was pending; keeping the multiplayer flow open.");
                return;
            }
            closePanelRequestPending = false;
        }

        isClosingMultiplayerPanel = true;

        if (LobbyManager.Instance != null && LobbyManager.Instance.IsSpawned &&
            NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
        {
            LobbyManager.Instance.ResetDinoSelections();
        }

        NetworkConfig networkConfig = NetworkConfig.Instance;
        if (networkConfig != null)
        {
            GameData.IsLeavingMatchIntentionally = true;
            await networkConfig.LeaveActiveSessionAsync();
        }

        ResetMultiplayerLobbyUI();
        GameData.IsMultiplayer = false;
        GameData.IsHost = false;

        if (multiplayerPanel != null) multiplayerPanel.SetActive(false);
        if (mainPanel != null) mainPanel.SetActive(true);

        isClosingMultiplayerPanel = false;
        quickMatchCancelRequested = false;
        quickMatchPhase = QuickMatchPhase.Idle;
        GameData.IsLeavingMatchIntentionally = false;
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
            await WaitForNetworkAndLobbyAsync(session, false, CancellationToken.None);
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

    private async Task WaitForNetworkAndLobbyAsync(ISession session, bool expectHost, CancellationToken cancellationToken)
    {
        float deadline = Time.realtimeSinceStartup + NetworkJoinTimeoutSeconds;
        SessionError? startFailure = null;
        bool networkWasStarting = false;
        IClientSessionNetwork clientNetwork = null;
        IHostSessionNetwork hostNetwork = null;
        Action<SessionError> onStartFailed = error => startFailure = error;
        Action<NetworkState> onNetworkStateChanged = state =>
        {
            if (state == NetworkState.Starting)
            {
                networkWasStarting = true;
            }
            else if (state == NetworkState.Stopped && networkWasStarting && !startFailure.HasValue)
            {
                startFailure = SessionError.NetworkSetupFailed;
            }
        };

        if (session != null && session.IsHost && session is IHostSession hostSession)
        {
            hostNetwork = hostSession.Network;
            hostNetwork.StartFailed += onStartFailed;
            hostNetwork.StateChanged += onNetworkStateChanged;
            networkWasStarting = hostNetwork.State == NetworkState.Starting;
        }
        else if (session != null)
        {
            clientNetwork = session.Network;
            clientNetwork.StartFailed += onStartFailed;
            clientNetwork.StateChanged += onNetworkStateChanged;
            networkWasStarting = clientNetwork.State == NetworkState.Starting;
        }

        try
        {
            while (Time.realtimeSinceStartup < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (startFailure.HasValue)
                {
                    throw new SessionNetworkStartException(startFailure.Value);
                }

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

            if (startFailure.HasValue)
            {
                throw new SessionNetworkStartException(startFailure.Value);
            }

            NetworkManager currentNetwork = NetworkManager.Singleton;
            bool listening = currentNetwork != null && currentNetwork.IsListening;
            bool connected = currentNetwork != null && currentNetwork.IsConnectedClient;
            bool lobbySpawned = LobbyManager.Instance != null && LobbyManager.Instance.IsSpawned;
            throw new TimeoutException(
                $"Timed out waiting for {(expectHost ? "host" : "client")} network/lobby readiness " +
                $"(session network state={GetSessionNetworkState(session, expectHost)}, " +
                $"listening={listening}, connected={connected}, LobbyManager spawned={lobbySpawned}).");
        }
        finally
        {
            if (clientNetwork != null)
            {
                clientNetwork.StartFailed -= onStartFailed;
                clientNetwork.StateChanged -= onNetworkStateChanged;
            }
            if (hostNetwork != null)
            {
                hostNetwork.StartFailed -= onStartFailed;
                hostNetwork.StateChanged -= onNetworkStateChanged;
            }
        }
    }

    private static string GetSessionNetworkState(ISession session, bool expectHost)
    {
        if (session == null) return "unavailable";
        if (expectHost && session is IHostSession hostSession) return hostSession.Network.State.ToString();
        return session.Network.State.ToString();
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
