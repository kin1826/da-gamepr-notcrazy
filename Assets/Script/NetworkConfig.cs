using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using Unity.Services.Multiplayer;
using UnityEngine;
using UnityEngine.SceneManagement;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

/// <summary>
/// Centralized network configuration for relay and multiplayer settings.
/// Handles Unity Relay setup, authentication, and connection management.
/// </summary>
public class NetworkConfig : MonoBehaviour
{
    public static NetworkConfig Instance { get; private set; }

    [Header("Relay Settings")]
    public bool useRelay = true;
    public int maxPlayers = 2;
    public string relayCode = "";

    [Header("Network Settings")]
    public string joinCode = "";
    public bool isHost = false;

    private UnityTransport transport;
    private Task initializeTask;
    private GameObject singlePlayerInstance;
    private ISession activeSession;

    public string AuthenticationProfileName { get; private set; } = "default";

    public ISession ActiveSession => activeSession;

    public static NetworkConfig EnsureInstance()
    {
        if (Instance != null)
        {
            return Instance;
        }

        NetworkManager networkManager = NetworkManager.Singleton != null
            ? NetworkManager.Singleton
            : Object.FindFirstObjectByType<NetworkManager>();

        if (networkManager == null)
        {
            Debug.LogError("NetworkManager not found!");
            return null;
        }

        NetworkConfig config = networkManager.GetComponent<NetworkConfig>();
        if (config == null)
        {
            config = networkManager.gameObject.AddComponent<NetworkConfig>();
        }

        return config;
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        SceneManager.sceneLoaded += OnSceneLoaded;

        transport = GetComponent<UnityTransport>();
        if (transport == null)
        {
            transport = gameObject.AddComponent<UnityTransport>();
            Debug.Log("Added UnityTransport component to NetworkManager");
        }

        NetworkManager networkManager = GetComponent<NetworkManager>();
        if (networkManager != null && networkManager.NetworkConfig.NetworkTransport == null)
        {
            networkManager.NetworkConfig.NetworkTransport = transport;
        }
    }

    private async void Start()
    {
        await EnsureUnityServicesInitialized();
    }

    private void OnDestroy()
    {
        if (Instance == this)
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            Instance = null;
        }
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        NetworkManager[] networkManagers = Object.FindObjectsByType<NetworkManager>(FindObjectsSortMode.None);
        foreach (NetworkManager networkManager in networkManagers)
        {
            if (networkManager.gameObject != gameObject)
            {
                Debug.LogWarning($"Destroying duplicate NetworkManager in scene '{scene.name}'.");
                Destroy(networkManager.gameObject);
            }
        }

        if (scene.name == "Menu")
        {
            ResetForMenu();
            ApiManager.Instance?.StartGoldPolling();
        }
        else
        {
            ApiManager.Instance?.StopGoldPolling();
        }

        SpawnSinglePlayerIfNeeded(scene);
    }

    private void ResetForMenu()
    {
        Time.timeScale = 1f;

        GameData.IsMultiplayer = false;
        GameData.IsHost        = false;
        GameData.IsLeavingMatchIntentionally = false;

        singlePlayerInstance = null;

        LevelManager.ResetDeathCount();

        _ = LeaveActiveSessionAsync();
    }

    public async Task EnsureServicesInitializedAsync()
    {
        await EnsureUnityServicesInitialized();

        if (UnityServices.State != ServicesInitializationState.Initialized ||
            !AuthenticationService.Instance.IsSignedIn)
        {
            throw new System.InvalidOperationException("Unity Services authentication is not ready for matchmaking.");
        }
    }

    public void SetActiveSession(ISession session)
    {
        activeSession = session;
    }

    public async Task<ISession> CreateHostedSessionAsync()
    {
        await EnsureServicesInitializedAsync();

        SessionOptions options = new SessionOptions
        {
            MaxPlayers = maxPlayers,
            IsPrivate = false,
            IsLocked = false
        }.WithRelayNetwork();
        ISession session = await MultiplayerService.Instance.CreateSessionAsync(options);
        activeSession = session;
        relayCode = session.Code;
        isHost = true;
        GameData.IsMultiplayer = true;
        GameData.IsHost = true;
        Debug.Log($"[Room] Created discoverable client-hosted session '{session.Id}' with code '{session.Code}'.");
        return session;
    }

    public async Task<ISession> JoinHostedSessionByCodeAsync(string code)
    {
        await EnsureServicesInitializedAsync();

        ISession session = await MultiplayerService.Instance.JoinSessionByCodeAsync(
            NormalizeJoinCode(code));
        activeSession = session;
        relayCode = session.Code;
        isHost = false;
        GameData.IsMultiplayer = true;
        GameData.IsHost = false;
        Debug.Log($"[Room] Joined client-hosted session '{session.Id}' with code '{session.Code}'.");
        return session;
    }

    /// <summary>
    /// Leave a Multiplayer Services session, or shut down a legacy Relay room,
    /// then clear the shared connection state before returning to the menu.
    /// </summary>
    public async Task LeaveActiveSessionAsync()
    {
        ISession sessionToLeave = activeSession;
        activeSession = null;

        try
        {
            if (sessionToLeave != null)
            {
                await sessionToLeave.LeaveAsync();
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"Failed to leave multiplayer session cleanly: {e.Message}");
        }
        finally
        {
            NetworkManager networkManager = GetComponent<NetworkManager>();
            if (networkManager != null && networkManager.IsListening)
            {
                networkManager.Shutdown();
            }

            isHost = false;
            relayCode = string.Empty;
            joinCode = string.Empty;
        }
    }

    private void SpawnSinglePlayerIfNeeded(Scene scene)
    {
        if (GameData.IsMultiplayer || scene.name == "Menu")
        {
            return;
        }

        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsConnectedClient)
        {
            return;
        }

        if (Object.FindFirstObjectByType<PlayerMovement>() != null)
        {
            return;
        }

        NetworkManager networkManager = GetComponent<NetworkManager>();
        if (networkManager == null || networkManager.NetworkConfig.PlayerPrefab == null)
        {
            Debug.LogError("Cannot spawn single player: NetworkManager PlayerPrefab is missing.");
            return;
        }

        Transform spawnPoint = FindSinglePlayerSpawnPoint();
        Vector3 spawnPosition = spawnPoint != null ? spawnPoint.position : Vector3.zero;

        singlePlayerInstance = Instantiate(networkManager.NetworkConfig.PlayerPrefab, spawnPosition, Quaternion.identity);
        singlePlayerInstance.name = "Player";

        PlayerSkinManager skinManager = singlePlayerInstance.GetComponent<PlayerSkinManager>();
        if (skinManager != null)
        {
            skinManager.SetSkin(GameData.SelectedDino);
        }

        Debug.Log($"Single player spawned at {(spawnPoint != null ? spawnPoint.name : "world origin")}.");
    }

    private Transform FindSinglePlayerSpawnPoint()
    {
        GameObject spawnOne = GameObject.Find("Spawn_1");
        if (spawnOne != null)
        {
            return spawnOne.transform;
        }

        GameObject[] spawns = GameObject.FindGameObjectsWithTag("Spawn");
        if (spawns.Length == 0)
        {
            Debug.LogWarning("No Spawn_1 or Spawn tag found. Single player will spawn at world origin.");
            return null;
        }

        System.Array.Sort(spawns, (a, b) => string.CompareOrdinal(a.name, b.name));
        return spawns[0].transform;
    }

    /// <summary>
    /// Initialize Unity Services and authenticate anonymously.
    /// </summary>
    private async Task InitializeUnityServices()
    {
        try
        {
            string requestedProfile = GetAuthenticationProfileName();
            if (UnityServices.State != ServicesInitializationState.Initialized)
            {
                InitializationOptions initializationOptions = new InitializationOptions()
                    .SetProfile(requestedProfile);
                await UnityServices.InitializeAsync(initializationOptions);
            }
            else if (AuthenticationService.Instance.Profile != requestedProfile)
            {
                Debug.LogWarning(
                    $"Unity Services was already initialized with profile '{AuthenticationService.Instance.Profile}', " +
                    $"but this project instance expects '{requestedProfile}'. Initialize UGS only through NetworkConfig.");
            }

            AuthenticationProfileName = AuthenticationService.Instance.Profile;
            if (!AuthenticationService.Instance.IsSignedIn)
            {
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
            }

            Debug.Log($"Unity Services authenticated. Profile='{AuthenticationProfileName}', " +
                $"PlayerId='{AuthenticationService.Instance.PlayerId}'.");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Failed to initialize Unity Services: {e.Message}");
        }
    }

    private static string GetAuthenticationProfileName()
    {
#if UNITY_EDITOR
        if (ParrelSync.ClonesManager.IsClone())
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string projectFolder = Path.GetFileName(projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            char[] profileCharacters = projectFolder
                .Select(character =>
                    (character >= 'A' && character <= 'Z') ||
                    (character >= 'a' && character <= 'z') ||
                    (character >= '0' && character <= '9') ||
                    character == '_' || character == '-'
                        ? character
                        : '_')
                .ToArray();
            string safeProjectFolder = new string(profileCharacters);

            if (safeProjectFolder.Length > 27)
            {
                safeProjectFolder = safeProjectFolder.Substring(safeProjectFolder.Length - 27);
            }

            return $"ps_{safeProjectFolder}";
        }
#endif
        return "default";
    }

    private Task EnsureUnityServicesInitialized()
    {
        initializeTask ??= InitializeUnityServices();
        return initializeTask;
    }

    /// <summary>
    /// Create a relay allocation for hosting.
    /// </summary>
    public async Task<string> CreateRelay()
    {
        try
        {
            await EnsureUnityServicesInitialized();

            Allocation allocation = await RelayService.Instance.CreateAllocationAsync(Mathf.Max(1, maxPlayers - 1));
            relayCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);

            transport.SetHostRelayData(
                allocation.RelayServer.IpV4,
                (ushort)allocation.RelayServer.Port,
                allocation.AllocationIdBytes,
                allocation.Key,
                allocation.ConnectionData
            );

            NetworkManager.Singleton.StartHost();
            isHost = true;
            GameData.IsMultiplayer = true;
            GameData.IsHost = true;

            Debug.Log($"Relay created! Join code: {relayCode}");
            return relayCode;
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Failed to create relay: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Join a relay using join code.
    /// </summary>
    public async Task<bool> JoinRelay(string joinCode)
    {
        try
        {
            await EnsureUnityServicesInitialized();

            string normalizedJoinCode = NormalizeJoinCode(joinCode);
            Debug.Log($"Joining Relay with code: {normalizedJoinCode}");

            JoinAllocation joinAllocation = await RelayService.Instance.JoinAllocationAsync(normalizedJoinCode);

            transport.SetClientRelayData(
                joinAllocation.RelayServer.IpV4,
                (ushort)joinAllocation.RelayServer.Port,
                joinAllocation.AllocationIdBytes,
                joinAllocation.Key,
                joinAllocation.ConnectionData,
                joinAllocation.HostConnectionData
            );

            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.StartClient())
            {
                throw new System.InvalidOperationException("Netcode failed to start the Relay client.");
            }

            isHost = false;
            GameData.IsMultiplayer = true;
            GameData.IsHost = false;

            Debug.Log("Joined relay successfully!");
            return true;
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Failed to join relay: {e.Message}");
            return false;
        }
    }

    public static string NormalizeJoinCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return "";
        }

        char[] allowedChars = code
            .Trim()
            .ToUpperInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray();

        return new string(allowedChars);
    }

    /// <summary>
    /// Start hosting without relay (direct connection).
    /// </summary>
    public void StartHostDirect()
    {
        NetworkManager.Singleton.StartHost();
        isHost = true;
        GameData.IsMultiplayer = true;
        GameData.IsHost = true;
        Debug.Log("Started hosting directly");
    }

    /// <summary>
    /// Start client without relay (direct connection).
    /// </summary>
    public void StartClientDirect()
    {
        NetworkManager.Singleton.StartClient();
        isHost = false;
        GameData.IsMultiplayer = true;
        GameData.IsHost = false;
        Debug.Log("Started client directly");
    }

    /// <summary>
    /// Shutdown network connection.
    /// </summary>
    public void Shutdown()
    {
        NetworkManager.Singleton.Shutdown();
        isHost = false;
        relayCode = "";
        joinCode = "";
        Debug.Log("Network shutdown");
    }
}
