using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

public class LevelComplete : NetworkBehaviour
{
    [Header("Exit")]
    public string groupId = "default";
    public float slideSpeed = 3f;
    public float delayBeforeLoad = 1.5f;
    public string nextSceneName = "";

    public static bool IsCompleting { get; private set; }

    private static readonly Dictionary<string, Dictionary<ulong, LevelComplete>> ReadyByGroup = new Dictionary<string, Dictionary<ulong, LevelComplete>>();
    private static readonly HashSet<string> CompletingGroups = new HashSet<string>();

    private bool activated;
    private Transform player;
    private Vector3 playerOffset;
    private bool previousPlayerSimulated = true;
    private bool previousPlayerMovementEnabled = true;

    private void OnDestroy()
    {
        IsCompleting = false;

        if (!ReadyByGroup.TryGetValue(groupId, out Dictionary<ulong, LevelComplete> readyPlayers))
        {
            return;
        }

        List<ulong> clientsToRemove = new List<ulong>();
        foreach (KeyValuePair<ulong, LevelComplete> pair in readyPlayers)
        {
            if (pair.Value == this)
            {
                clientsToRemove.Add(pair.Key);
            }
        }

        foreach (ulong clientId in clientsToRemove)
        {
            readyPlayers.Remove(clientId);
        }

        if (readyPlayers.Count == 0)
        {
            ReadyByGroup.Remove(groupId);
            CompletingGroups.Remove(groupId);
        }
    }

    private void OnTriggerEnter2D(Collider2D collision)
    {
        if (!TryGetPlayer(collision, out NetworkObject playerNetworkObject, out Transform playerTransform))
        {
            return;
        }

        if (!IsMultiplayer())
        {
            BeginCompletionVisual(playerTransform);
            StartCoroutine(LoadNextSceneAfterDelay());
            return;
        }

        if (NetworkManager.Singleton == null)
        {
            return;
        }

        if (NetworkManager.Singleton.IsServer)
        {
            MarkPlayerReady(playerNetworkObject.OwnerClientId, this);
            return;
        }

        if (playerNetworkObject.IsOwner)
        {
            // If this LevelComplete is a spawned NetworkObject, send RPC directly.
            // Otherwise relay through the player's own NetworkObject (always spawned).
            if (IsSpawned)
                EnterExitServerRpc();
            else
                playerNetworkObject.GetComponent<PlayerMovement>()
                    ?.NotifyLevelExitServerRpc(groupId, true);
        }
    }

    private void OnTriggerExit2D(Collider2D collision)
    {
        if (!TryGetPlayer(collision, out NetworkObject playerNetworkObject, out _))
        {
            return;
        }

        if (!IsMultiplayer() || CompletingGroups.Contains(groupId))
        {
            return;
        }

        if (NetworkManager.Singleton == null)
        {
            return;
        }

        if (NetworkManager.Singleton.IsServer)
        {
            RemovePlayerReady(playerNetworkObject.OwnerClientId, this);
            return;
        }

        if (playerNetworkObject.IsOwner)
        {
            if (IsSpawned)
                ExitExitServerRpc();
            else
                playerNetworkObject.GetComponent<PlayerMovement>()
                    ?.NotifyLevelExitServerRpc(groupId, false);
        }
    }

    public void SavePlayerDone()
    {
        int next = UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex + 1;

        GameData.SelectedLevel = next;

        if(next > SaveManager.Data.highestLevel)
        {
            SaveManager.Data.highestLevel = next;
            SaveManager.Save();
        }
    }

    [ServerRpc(RequireOwnership = false)]
    private void EnterExitServerRpc(ServerRpcParams rpcParams = default)
    {
        MarkPlayerReady(rpcParams.Receive.SenderClientId, this);
    }

    [ServerRpc(RequireOwnership = false)]
    private void ExitExitServerRpc(ServerRpcParams rpcParams = default)
    {
        RemovePlayerReady(rpcParams.Receive.SenderClientId, this);
    }

    // Called by PlayerMovement.NotifyLevelExitServerRpc when LevelComplete is not a NetworkObject.
    public static void ServerHandlePlayerEnter(ulong clientId, string groupId)
    {
        LevelComplete[] exits = Object.FindObjectsByType<LevelComplete>(FindObjectsSortMode.None);
        foreach (LevelComplete exit in exits)
        {
            if (exit.groupId == groupId)
            {
                MarkPlayerReady(clientId, exit);
                return;
            }
        }
        Debug.LogWarning($"LevelComplete.ServerHandlePlayerEnter: no exit found for groupId '{groupId}'");
    }

    public static void ServerHandlePlayerExit(ulong clientId, string groupId)
    {
        LevelComplete[] exits = Object.FindObjectsByType<LevelComplete>(FindObjectsSortMode.None);
        foreach (LevelComplete exit in exits)
        {
            if (exit.groupId == groupId)
            {
                RemovePlayerReady(clientId, exit);
                return;
            }
        }
    }

    private static void MarkPlayerReady(ulong clientId, LevelComplete exit)
    {
        if (!ReadyByGroup.TryGetValue(exit.groupId, out Dictionary<ulong, LevelComplete> readyPlayers))
        {
            readyPlayers = new Dictionary<ulong, LevelComplete>();
            ReadyByGroup.Add(exit.groupId, readyPlayers);
        }

        readyPlayers[clientId] = exit;
        Debug.Log($"Player {clientId} ready at level exit. Ready: {readyPlayers.Count}/{GetRequiredPlayers()}");

        if (readyPlayers.Count >= GetRequiredPlayers())
        {
            StartGroupCompletion(exit.groupId, readyPlayers);
        }
    }

    private static void RemovePlayerReady(ulong clientId, LevelComplete exit)
    {
        if (!ReadyByGroup.TryGetValue(exit.groupId, out Dictionary<ulong, LevelComplete> readyPlayers))
        {
            return;
        }

        if (readyPlayers.TryGetValue(clientId, out LevelComplete currentExit) && currentExit == exit)
        {
            readyPlayers.Remove(clientId);
            Debug.Log($"Player {clientId} left level exit. Ready: {readyPlayers.Count}/{GetRequiredPlayers()}");
        }
    }

    private static void StartGroupCompletion(string groupId, Dictionary<ulong, LevelComplete> readyPlayers)
    {
        if (CompletingGroups.Contains(groupId))
        {
            Debug.Log($"LevelComplete: StartGroupCompletion skipped — group '{groupId}' already completing.");
            return;
        }

        Debug.Log($"LevelComplete: All {readyPlayers.Count} players ready. Starting group completion for '{groupId}'.");
        CompletingGroups.Add(groupId);

        LevelComplete loadOwner = null;
        foreach (KeyValuePair<ulong, LevelComplete> pair in readyPlayers)
        {
            if (loadOwner == null)
            {
                loadOwner = pair.Value;
            }

            pair.Value.StartCompletionForPlayer(pair.Key);
        }

        // Use NetworkManager.Singleton instead of loadOwner.NetworkManager —
        // loadOwner.NetworkManager is null if the GameObject lacks a NetworkObject component.
        if (loadOwner != null && NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
        {
            loadOwner.StartCoroutine(loadOwner.LoadNextSceneAfterDelay());
        }
    }

    private void StartCompletionForPlayer(ulong clientId)
    {
        if (IsSpawned)
        {
            StartCompletionClientRpc(clientId);
            return;
        }

        Transform targetPlayer = FindPlayerByClientId(clientId);
        if (targetPlayer != null)
        {
            BeginCompletionVisual(targetPlayer);
        }
    }

    [ClientRpc]
    private void StartCompletionClientRpc(ulong clientId)
    {
        Transform targetPlayer = FindPlayerByClientId(clientId);
        if (targetPlayer != null)
        {
            BeginCompletionVisual(targetPlayer);
        }
    }

    private void BeginCompletionVisual(Transform targetPlayer)
    {
        if (activated)
        {
            return;
        }

        activated = true;
        IsCompleting = true;
        player = targetPlayer;

        // Protect the owning player before the door animation moves it. The
        // protection remains active across the scene load until PlayerSpawner
        // has placed and stabilized the player at the next spawn point.
        player.GetComponent<PlayerSpawner>()?.BeginSpawnProtection();

        player.position = new Vector3(transform.position.x, player.position.y, player.position.z);
        playerOffset = player.position - transform.position;

        Rigidbody2D rb = player.GetComponent<Rigidbody2D>();
        if (rb != null)
        {
            previousPlayerSimulated = rb.simulated;
            rb.linearVelocity = Vector2.zero;
            rb.simulated = false;
        }

        PlayerMovement movement = player.GetComponent<PlayerMovement>();
        if (movement != null)
        {
            previousPlayerMovementEnabled = movement.enabled;
            movement.enabled = false;
        }

        GetComponentInParent<LevelCompleteDoorGroup>()?.PlayDoorStartSfxOnce();
        StartCoroutine(SlideDown());
        StartCoroutine(RestoreCompletionAfterDelay());
    }

    private IEnumerator RestoreCompletionAfterDelay()
    {
        yield return new WaitForSeconds(delayBeforeLoad);
        RestoreCompletionLocal();
    }

    private void RestoreCompletionLocal()
    {
        if (player == null)
        {
            return;
        }

        Rigidbody2D rb = player.GetComponent<Rigidbody2D>();
        if (rb != null)
        {
            rb.simulated = previousPlayerSimulated;
        }

        PlayerMovement movement = player.GetComponent<PlayerMovement>();
        if (movement != null)
        {
            movement.enabled = previousPlayerMovementEnabled;
        }

        activated = false;
    }

    private IEnumerator SlideDown()
    {
        float timer = 0f;

        while (timer < delayBeforeLoad)
        {
            transform.position += Vector3.down * slideSpeed * Time.deltaTime;

            if (player != null)
            {
                player.position = transform.position + playerOffset;
            }

            timer += Time.deltaTime;
            yield return null;
        }
    }

    private IEnumerator LoadNextSceneAfterDelay()
    {
        yield return new WaitForSeconds(delayBeforeLoad);
        
        string target = GetNextSceneName();
        if (string.IsNullOrWhiteSpace(target))
        {
            Debug.LogError("LevelComplete: No next scene is configured (Door 1 for grouped exits), and there is no next scene in Build Settings.");
            yield break;
        }

        Debug.Log($"LevelComplete: Loading level '{target}'");
        SavePlayerDone();

        int reward = LevelManager.LevelCompletionReward;
        if (reward > 0)
        {
            string rewardEventId = System.Guid.NewGuid().ToString("N");
            if (IsMultiplayer() && NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
            {
                NetworkObject hostPlayer = NetworkManager.Singleton.SpawnManager.GetLocalPlayerObject();
                PlayerMovement rewardRelay = hostPlayer != null ? hostPlayer.GetComponent<PlayerMovement>() : null;
                if (rewardRelay != null && rewardRelay.IsSpawned)
                {
                    rewardRelay.GrantLevelRewardClientRpc(reward, rewardEventId);
                }
                else
                {
                    Debug.LogError("LevelComplete: Could not find the host PlayerMovement to distribute the co-op level reward.");
                }
            }
            else
            {
                UserSession.QueueLevelReward(reward, rewardEventId);
            }
        }

        if (LevelManager.Instance == null)
        {
            Debug.LogError("LevelComplete: LevelManager.Instance is null — cannot load next scene.");
            yield break;
        }

        LevelManager.Instance.LoadLevelAfterCompletion(target);

        yield break;
    }

    private string GetNextSceneName()
    {
        LevelCompleteDoorGroup doorGroup = GetComponentInParent<LevelCompleteDoorGroup>();
        if (doorGroup != null)
        {
            // Door 1 is the single source of truth for scene progression. Either
            // door can trigger co-op completion, but Door 2's scene text must
            // never determine which level is loaded.
            LevelComplete primaryDoor = doorGroup.primaryDoor != null
                ? doorGroup.primaryDoor.GetComponent<LevelComplete>()
                : null;

            if (primaryDoor != null && !string.IsNullOrWhiteSpace(primaryDoor.nextSceneName))
            {
                return primaryDoor.nextSceneName.Trim();
            }

            Debug.LogWarning("LevelComplete: Door 1 has no next-scene text; falling back to the next scene in Build Settings.");
            return GetNextSceneFromBuildOrder();
        }

        // Standalone exits that are not part of a two-door group keep using
        // their own configured destination.
        if (!string.IsNullOrWhiteSpace(nextSceneName))
        {
            return nextSceneName.Trim();
        }

        return GetNextSceneFromBuildOrder();
    }

    private static string GetNextSceneFromBuildOrder()
    {
        int nextBuildIndex = SceneManager.GetActiveScene().buildIndex + 1;
        if (nextBuildIndex >= SceneManager.sceneCountInBuildSettings)
        {
            return "";
        }

        string scenePath = SceneUtility.GetScenePathByBuildIndex(nextBuildIndex);
        return System.IO.Path.GetFileNameWithoutExtension(scenePath);
    }

    private static int GetRequiredPlayers()
    {
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsConnectedClient)
        {
            return Mathf.Max(1, NetworkManager.Singleton.ConnectedClientsIds.Count);
        }

        return 1;
    }

    private bool TryGetPlayer(Collider2D collision, out NetworkObject playerNetworkObject, out Transform playerTransform)
    {
        playerNetworkObject = collision.GetComponentInParent<NetworkObject>();
        PlayerMovement movement = collision.GetComponentInParent<PlayerMovement>();
        playerTransform = movement != null ? movement.transform : collision.transform;

        if (!collision.CompareTag("Player") && movement == null)
        {
            return false;
        }

        return !IsMultiplayer() || playerNetworkObject != null;
    }

    private static Transform FindPlayerByClientId(ulong clientId)
    {
        if (NetworkManager.Singleton == null)
        {
            return null;
        }

        try
        {
            if (NetworkManager.Singleton.ConnectedClients.TryGetValue(clientId, out var networkClient))
            {
                if (networkClient.PlayerObject != null)
                {
                    var pm = networkClient.PlayerObject.GetComponent<PlayerMovement>();
                    if (pm != null)
                    {
                        return networkClient.PlayerObject.transform;
                    }
                }
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"FindPlayerByClientId: lookup failed: {ex.Message}");
        }

        return null;
    }

    private bool IsMultiplayer()
    {
        return NetworkManager.Singleton != null && NetworkManager.Singleton.IsConnectedClient;
    }
}
