using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Multiplayer respawn gate: both players must tap "Continue" before the scene reloads.
/// Attach this script + NetworkObject to a scene GameObject in every game level.
/// </summary>
public class RespawnSync : NetworkBehaviour
{
    public static RespawnSync Instance { get; private set; }

    private readonly HashSet<ulong> readyPlayers = new();
    private readonly HashSet<ulong> pendingDeathShakeClients = new();
    private bool panelTriggered = false;
    private bool respawnPanelShown;
    private ulong dyingClientId;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        if (IsServer && NetworkManager != null)
        {
            NetworkManager.OnClientDisconnectCallback += OnClientDisconnected;
        }
    }

    public override void OnNetworkDespawn()
    {
        if (NetworkManager != null)
        {
            NetworkManager.OnClientDisconnectCallback -= OnClientDisconnected;
        }

        if (Instance == this)
        {
            Instance = null;
        }

        base.OnNetworkDespawn();
    }

    // ── SERVER ENTRY POINT ────────────────────────────────────────────────────

    public void TriggerShowPanel(ulong dyingClientId)
    {
        if (!IsServer) return;
        if (panelTriggered) { Debug.Log("[RespawnSync] TriggerShowPanel blocked (already triggered)"); return; }
        panelTriggered = true;
        respawnPanelShown = false;
        this.dyingClientId = dyingClientId;
        readyPlayers.Clear();
        pendingDeathShakeClients.Clear();
        foreach (ulong clientId in NetworkManager.ConnectedClientsIds)
        {
            pendingDeathShakeClients.Add(clientId);
        }

        CameraShake cameraShake = CameraShake.Instance;
        float duration = cameraShake != null ? cameraShake.DefaultDuration : 0.3f;
        float magnitude = cameraShake != null ? cameraShake.DefaultMagnitude : 0.2f;
        Debug.Log($"[RespawnSync] TriggerShowPanel for client {dyingClientId}");
        BeginDeathShakeClientRpc(dyingClientId, duration, magnitude);
        StartCoroutine(DeathShakeTimeout(duration + 2f));

        if (pendingDeathShakeClients.Count == 0)
        {
            ShowRespawnPanelToAll();
        }
    }

    // ── CLIENT RPC ────────────────────────────────────────────────────────────

    [ClientRpc]
    private void BeginDeathShakeClientRpc(ulong dyingClientId, float duration, float magnitude)
    {
        PlayerMovement.FindForClient(dyingClientId)?.PlayDeathSfx();

        CameraShake cameraShake = CameraShake.Instance;
        if (cameraShake == null)
        {
            NotifyDeathShakeComplete();
            return;
        }

        cameraShake.Shake(duration, magnitude, NotifyDeathShakeComplete);
    }

    [ClientRpc]
    private void ShowPanelClientRpc(ulong dyingClientId)
    {
        bool iAmDead = NetworkManager.Singleton.LocalClientId == dyingClientId;
        LevelManager.Instance?.ShowRespawnPanel(iAmDead);
    }

    private void NotifyDeathShakeComplete()
    {
        if (IsServer)
        {
            RegisterDeathShakeComplete(NetworkManager.LocalClientId);
        }
        else
        {
            DeathShakeCompleteRpc();
        }
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void DeathShakeCompleteRpc(RpcParams rpcParams = default)
    {
        RegisterDeathShakeComplete(rpcParams.Receive.SenderClientId);
    }

    private void RegisterDeathShakeComplete(ulong clientId)
    {
        if (!IsServer || !panelTriggered || respawnPanelShown)
        {
            return;
        }

        pendingDeathShakeClients.Remove(clientId);
        if (pendingDeathShakeClients.Count == 0)
        {
            ShowRespawnPanelToAll();
        }
    }

    private IEnumerator DeathShakeTimeout(float timeout)
    {
        yield return new WaitForSecondsRealtime(timeout);
        if (panelTriggered && !respawnPanelShown)
        {
            Debug.LogWarning("[RespawnSync] Death shake acknowledgement timed out; showing the respawn panel.");
            ShowRespawnPanelToAll();
        }
    }

    private void ShowRespawnPanelToAll()
    {
        if (!IsServer || respawnPanelShown)
        {
            return;
        }

        respawnPanelShown = true;
        pendingDeathShakeClients.Clear();
        ShowPanelClientRpc(dyingClientId);
    }

    private void OnClientDisconnected(ulong clientId)
    {
        if (!IsServer || !panelTriggered || respawnPanelShown)
        {
            return;
        }

        pendingDeathShakeClients.Remove(clientId);
        if (pendingDeathShakeClients.Count == 0)
        {
            ShowRespawnPanelToAll();
        }
    }

    [ClientRpc]
    private void ShowWaitingOnClientRpc(ClientRpcParams rpcParams = default)
    {
        LevelManager.Instance?.ShowWaiting();
    }

    [ClientRpc]
    private void DoReloadClientRpc()
    {
        GameData.IsRespawning = true;
        LevelManager.Instance?.PlayCloseTransitionOnly();
    }

    // ── SERVER RPC ────────────────────────────────────────────────────────────

    [ServerRpc(RequireOwnership = false)]
    public void PlayerReadyServerRpc(ServerRpcParams rpcParams = default)
    {
        ulong clientId = rpcParams.Receive.SenderClientId;

        if (readyPlayers.Contains(clientId)) return;
        readyPlayers.Add(clientId);

        Debug.Log($"[RespawnSync] Player {clientId} ready. readyPlayers={readyPlayers.Count}, connectedIds={string.Join(",", NetworkManager.Singleton.ConnectedClientsIds)}");

        ShowWaitingOnClientRpc(new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } }
        });

        // Require every connected client ID to have tapped.
        // ConnectedClients.Count is unreliable in NGO 2.x (may or may not include host).
        bool allReady = true;
        foreach (ulong id in NetworkManager.Singleton.ConnectedClientsIds)
        {
            if (!readyPlayers.Contains(id)) { allReady = false; break; }
        }

        if (allReady)
            StartCoroutine(ReloadAfterTransition());
    }

    private IEnumerator ReloadAfterTransition()
    {
        DoReloadClientRpc();
        // WaitForSecondsRealtime because Time.timeScale is 0 while panel is shown.
        yield return new WaitForSecondsRealtime(0.35f);
        string sceneName = SceneManager.GetActiveScene().name;
        NetworkManager.Singleton.SceneManager.LoadScene(sceneName, LoadSceneMode.Single);
    }
}
