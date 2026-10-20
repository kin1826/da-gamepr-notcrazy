using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

public class TrapButton : NetworkBehaviour
{
    public enum ButtonMode
    {
        PressOnce,
        Toggle,
        Hold
    }

    [Header("Button")]
    public ButtonMode buttonMode = ButtonMode.Hold;
    public bool autoConvertHoldInSingle = true;
    public string[] extraActivatorTags = { "PushBlock", "ButtonWeight" };

    [Header("Visual")]
    public Transform visual;
    public float pressedYOffset = -0.05f;
    public float pulseDuration = 0.12f;

    [Header("Actions")]
    public TrapAction[] actions;

    private readonly HashSet<int> activatorsHolding = new HashSet<int>();
    private bool pressedOnce;
    private bool toggledOn;
    private Vector3 visualStartLocalPosition;
    private bool currentPressedState;

    private NetworkVariable<bool> isPressed = new NetworkVariable<bool>(false);

    private void Awake()
    {
        if (visual == null)
        {
            visual = transform;
        }

        visualStartLocalPosition = visual.localPosition;
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        isPressed.OnValueChanged += OnPressedChanged;
        UpdatePressedVisual(isPressed.Value);
    }

    public override void OnNetworkDespawn()
    {
        isPressed.OnValueChanged -= OnPressedChanged;
        base.OnNetworkDespawn();
    }

    private void OnPressedChanged(bool oldValue, bool newValue)
    {
        currentPressedState = newValue;
        UpdatePressedVisual(newValue);
    }

    private void OnTriggerEnter2D(Collider2D collision)
    {
        if (!TryGetActivatorId(collision, out int activatorId))
        {
            return;
        }

        if (!CanRunTrapLogic())
        {
            if (IsSpawned && TryGetLocalPlayerClientId(collision, out _))
            {
                ReportPlayerPresenceRpc(true);
            }

            return;
        }

        RegisterActivatorEnter(activatorId);
    }

    private void OnTriggerExit2D(Collider2D collision)
    {
        if (!TryGetActivatorId(collision, out int activatorId))
        {
            return;
        }

        if (!CanRunTrapLogic())
        {
            if (IsSpawned && TryGetLocalPlayerClientId(collision, out _))
            {
                ReportPlayerPresenceRpc(false);
            }

            return;
        }

        RegisterActivatorExit(activatorId);
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void ReportPlayerPresenceRpc(bool entered, RpcParams rpcParams = default)
    {
        int activatorId = rpcParams.Receive.SenderClientId.GetHashCode();
        if (entered)
        {
            RegisterActivatorEnter(activatorId);
        }
        else
        {
            RegisterActivatorExit(activatorId);
        }
    }

    private void RegisterActivatorEnter(int activatorId)
    {
        if (!activatorsHolding.Add(activatorId))
        {
            return;
        }

        Press();
    }

    private void RegisterActivatorExit(int activatorId)
    {
        if (!activatorsHolding.Remove(activatorId))
        {
            return;
        }

        if (GetEffectiveMode() != ButtonMode.Hold || activatorsHolding.Count != 0)
        {
            return;
        }

        DeactivateActions();
    }

    private void Press()
    {
        if (IsMultiplayer() && !IsServer) return;

        switch (GetEffectiveMode())
        {
            case ButtonMode.PressOnce:
                if (pressedOnce)
                {
                    return;
                }

                pressedOnce = true;
                SetPressedState(true);
                ActivateActions();
                break;

            case ButtonMode.Toggle:
                toggledOn = !toggledOn;
                SetPressedState(toggledOn);
                if (toggledOn)
                {
                    ActivateActions();
                }
                else
                {
                    DeactivateActions();
                }
                break;

            case ButtonMode.Hold:
                SetPressedState(true);
                ActivateActions();
                break;
        }
    }

    private ButtonMode GetEffectiveMode()
    {
        bool isSinglePlayer = NetworkManager.Singleton == null || !NetworkManager.Singleton.IsConnectedClient;
        if (isSinglePlayer && autoConvertHoldInSingle && buttonMode == ButtonMode.Hold)
        {
            return ButtonMode.PressOnce;
        }

        return buttonMode;
    }

    private void ActivateActions()
    {
        if (GetEffectiveMode() != ButtonMode.Toggle)
        {
            SetPressedState(true);
        }
        else
        {
            SetPressedState(toggledOn);
        }

        foreach (TrapAction action in actions)
        {
            if (action != null)
            {
                action.RequestActivate();
            }
        }
    }

    private void DeactivateActions()
    {
        if (IsMultiplayer() && !IsServer) return;

        if (GetEffectiveMode() != ButtonMode.Toggle)
        {
            SetPressedState(false);
        }

        foreach (TrapAction action in actions)
        {
            if (action != null)
            {
                action.RequestDeactivate();
            }
        }
    }

    private bool TryGetActivatorId(Collider2D collision, out int activatorId)
    {
        activatorId = 0;

        PlayerMovement player = collision.GetComponentInParent<PlayerMovement>();
        if (collision.CompareTag("Player") || player != null)
        {
            NetworkObject networkObject = collision.GetComponentInParent<NetworkObject>();
            activatorId = networkObject != null
                ? networkObject.OwnerClientId.GetHashCode()
                : collision.GetInstanceID();
            return true;
        }

        foreach (string tagName in extraActivatorTags)
        {
            if (!string.IsNullOrWhiteSpace(tagName) && HasTag(collision.gameObject, tagName))
            {
                activatorId = collision.attachedRigidbody != null
                    ? collision.attachedRigidbody.gameObject.GetInstanceID()
                    : collision.gameObject.GetInstanceID();
                return true;
            }
        }

        return false;
    }

    private bool TryGetLocalPlayerClientId(Collider2D collision, out ulong clientId)
    {
        clientId = 0;
        PlayerMovement player = collision.GetComponentInParent<PlayerMovement>();
        NetworkObject networkObject = collision.GetComponentInParent<NetworkObject>();
        if (player == null || networkObject == null || !networkObject.IsOwner)
        {
            return false;
        }

        clientId = networkObject.OwnerClientId;
        return true;
    }

    private bool HasTag(GameObject target, string tagName)
    {
        return string.Equals(
            target.tag.Trim(),
            tagName.Trim(),
            System.StringComparison.Ordinal
        );
    }

    private bool IsMultiplayer()
    {
        return NetworkManager.Singleton != null && NetworkManager.Singleton.IsConnectedClient;
    }

    private bool CanRunTrapLogic()
    {
        return !IsMultiplayer() || IsServer;
    }

    private void SetPressedState(bool pressed)
    {
        if (IsSpawned)
        {
            if (!IsServer)
            {
                return;
            }

            isPressed.Value = pressed;
        }

        UpdatePressedVisual(pressed);
    }

    private void SetPressedVisual(bool isPressed)
    {
        if (visual == null)
        {
            return;
        }

        visual.localPosition = visualStartLocalPosition + (isPressed ? new Vector3(0f, pressedYOffset, 0f) : Vector3.zero);
    }

    private void UpdatePressedVisual(bool pressed)
    {
        currentPressedState = pressed;
        SetPressedVisual(pressed);
    }

}
