using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Manages multiplayer lobby state (dino selections). Ensures single instance.
/// Syncs dino selections between host and clients using NetworkVariables.
/// </summary>
public class LobbyManager : NetworkBehaviour
{
    public static LobbyManager Instance { get; private set; }

    public NetworkVariable<int> HostDino =
        new NetworkVariable<int>(-1);

    public NetworkVariable<int> ClientDino =
        new NetworkVariable<int>(-1);

    private void Awake()
    {
        // Singleton pattern: prevent multiple instances
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("Duplicate LobbyManager found! Destroying this instance.");
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    [Rpc(SendTo.Server)]
    public void SetHostDinoRpc(int index)
    {
        HostDino.Value = index;
    }

    [Rpc(SendTo.Server)]
    public void SetClientDinoRpc(int index)
    {
        ClientDino.Value = index;
    }

    [Rpc(SendTo.Server)]
    public void ResetDinoSelectionsRpc()
    {
        HostDino.Value = -1;
        ClientDino.Value = -1;
    }

    public void ResetDinoSelections()
    {
        if (IsSpawned)
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsListening)
            {
                return;
            }

            if (IsServer)
            {
                HostDino.Value = -1;
                ClientDino.Value = -1;
            }
            else
            {
                ResetDinoSelectionsRpc();
            }

            return;
        }

        // The menu LobbyManager is a scene object even while networking is off.
        // Clear its local values so a canceled/closed room cannot leak Dino picks
        // into the next lobby before NGO spawns it again.
        HostDino.Value = -1;
        ClientDino.Value = -1;
    }

}
