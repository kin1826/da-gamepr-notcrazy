using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

public class TrapTrigger : MonoBehaviour
{
    public enum TriggerMode
    {
        Once,
        AfterEnterCount,
        UniquePlayers,
        AllPlayersInside
    }

    [Header("Trigger")]
    public TriggerMode triggerMode = TriggerMode.Once;
    public int requiredCount = 1;
    public bool triggerOnlyOnce = true;

    [Header("Actions")]
    public TrapAction[] actions;

    private readonly HashSet<ulong> playersInside = new HashSet<ulong>();
    private readonly HashSet<ulong> playersSeen = new HashSet<ulong>();
    private int enterCount;
    private bool hasTriggered;

    private void OnTriggerEnter2D(Collider2D collision)
    {
        if (!IsPlayer(collision, out ulong clientId))
        {
            return;
        }

        if (!CanRunTrapLogic() && !IsLocalOwnedPlayer(collision))
        {
            return;
        }

        RegisterPlayerEnter(clientId);
    }

    private void OnTriggerExit2D(Collider2D collision)
    {
        if (!IsPlayer(collision, out ulong clientId))
        {
            return;
        }

        if (!CanRunTrapLogic() && !IsLocalOwnedPlayer(collision))
        {
            return;
        }

        playersInside.Remove(clientId);
    }

    private void RegisterPlayerEnter(ulong clientId)
    {
        enterCount++;
        playersInside.Add(clientId);
        playersSeen.Add(clientId);

        if (ShouldActivate())
        {
            ActivateActions();
        }
    }

    private bool ShouldActivate()
    {
        if (triggerOnlyOnce && hasTriggered)
        {
            return false;
        }

        int countNeeded = Mathf.Max(1, requiredCount);

        switch (triggerMode)
        {
            case TriggerMode.Once:
                return true;
            case TriggerMode.AfterEnterCount:
                return enterCount >= countNeeded;
            case TriggerMode.UniquePlayers:
                return playersSeen.Count >= countNeeded;
            case TriggerMode.AllPlayersInside:
                return playersInside.Count >= GetRequiredPlayerCount();
            default:
                return false;
        }
    }

    private void ActivateActions()
    {
        hasTriggered = true;

        foreach (TrapAction action in actions)
        {
            if (action != null)
            {
                action.RequestActivate();
            }
        }
    }

    private int GetRequiredPlayerCount()
    {
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsConnectedClient)
        {
            return Mathf.Max(1, NetworkManager.Singleton.ConnectedClientsIds.Count);
        }

        return Mathf.Max(1, requiredCount);
    }

    private bool CanRunTrapLogic()
    {
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsConnectedClient)
        {
            return NetworkManager.Singleton.IsServer;
        }

        return true;
    }

    private bool IsLocalOwnedPlayer(Collider2D collision)
    {
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsConnectedClient)
        {
            return true;
        }

        NetworkObject networkObject = collision.GetComponentInParent<NetworkObject>();
        return networkObject != null && networkObject.IsOwner;
    }

    private bool IsPlayer(Collider2D collision, out ulong clientId)
    {
        clientId = 0;

        if (!collision.CompareTag("Player") && collision.GetComponentInParent<PlayerMovement>() == null)
        {
            return false;
        }

        NetworkObject networkObject = collision.GetComponentInParent<NetworkObject>();
        if (networkObject == null)
        {
            return false;
        }

        clientId = networkObject.OwnerClientId;
        return true;
    }
}
