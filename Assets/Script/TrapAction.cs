using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

public class TrapAction : NetworkBehaviour
{
    public enum ActionType
    {
        MoveToTarget,
        MoveBackToStart,
        SetObjectActive,
        SetColliderEnabled,
        ReloadScene
    }

    [Header("Action")]
    public ActionType actionType = ActionType.MoveToTarget;
    public bool triggerOnlyOnce = true;

    [Header("Movement")]
    public Transform objectToMove;
    public Transform target;
    public float moveSpeed = 5f;

    [Header("Object State")]
    public GameObject targetObject;
    public bool activeState = true;
    public Collider2D targetCollider;
    public bool colliderState = true;

    private struct MotionState : INetworkSerializable, IEquatable<MotionState>
    {
        public Vector3 From;
        public Vector3 To;
        public double StartedAt;
        public float Duration;
        public bool IsMoving;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer)
            where T : IReaderWriter
        {
            serializer.SerializeValue(ref From);
            serializer.SerializeValue(ref To);
            serializer.SerializeValue(ref StartedAt);
            serializer.SerializeValue(ref Duration);
            serializer.SerializeValue(ref IsMoving);
        }

        public bool Equals(MotionState other)
        {
            return From == other.From
                && To == other.To
                && StartedAt.Equals(other.StartedAt)
                && Duration.Equals(other.Duration)
                && IsMoving == other.IsMoving;
        }
    }

    private readonly NetworkVariable<MotionState> networkMotion = new(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private readonly NetworkVariable<bool> networkObjectActive = new(
        true,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private readonly NetworkVariable<bool> networkColliderEnabled = new(
        true,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private MotionState localMotion;
    private bool hasTriggered;
    private Vector3 startPosition;

    private void Awake()
    {
        if (objectToMove == null)
        {
            objectToMove = transform;
        }

        startPosition = objectToMove.position;
        localMotion = CreateStationaryState(startPosition);
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        networkMotion.OnValueChanged += OnNetworkMotionChanged;
        networkObjectActive.OnValueChanged += OnNetworkObjectActiveChanged;
        networkColliderEnabled.OnValueChanged += OnNetworkColliderEnabledChanged;

        if (IsServer)
        {
            networkMotion.Value = CreateStationaryState(startPosition);
            if (targetObject != null)
            {
                networkObjectActive.Value = targetObject.activeSelf;
            }

            if (targetCollider != null)
            {
                networkColliderEnabled.Value = targetCollider.enabled;
            }
        }

        ApplyReplicatedPosition(networkMotion.Value);
        SetObjectActive(networkObjectActive.Value);
        SetColliderEnabled(networkColliderEnabled.Value);
    }

    public override void OnNetworkDespawn()
    {
        networkMotion.OnValueChanged -= OnNetworkMotionChanged;
        networkObjectActive.OnValueChanged -= OnNetworkObjectActiveChanged;
        networkColliderEnabled.OnValueChanged -= OnNetworkColliderEnabledChanged;
        base.OnNetworkDespawn();
    }

    private void Update()
    {
        if (objectToMove == null)
        {
            return;
        }

        MotionState state = IsSpawned ? networkMotion.Value : localMotion;
        if (!state.IsMoving)
        {
            if (IsSpawned && !IsServer)
            {
                objectToMove.position = state.To;
            }

            return;
        }

        double now = GetSynchronizedTime();
        float progress = EvaluateProgress(state, now);
        objectToMove.position = Vector3.LerpUnclamped(state.From, state.To, progress);

        if (progress < 1f || !HasMotionAuthority())
        {
            return;
        }

        state.From = state.To;
        state.StartedAt = now;
        state.Duration = 0f;
        state.IsMoving = false;
        SetMotionState(state);
    }

    private void ApplyReplicatedPosition(MotionState state)
    {
        if (objectToMove == null)
        {
            return;
        }

        objectToMove.position = state.IsMoving
            ? Vector3.LerpUnclamped(
                state.From,
                state.To,
                EvaluateProgress(state, GetSynchronizedTime())
            )
            : state.To;
    }

    private void OnNetworkMotionChanged(MotionState _, MotionState newState)
    {
        if (!IsServer)
        {
            ApplyReplicatedPosition(newState);
        }
    }

    private void OnNetworkObjectActiveChanged(bool _, bool newValue)
    {
        if (!IsServer)
        {
            SetObjectActive(newValue);
        }
    }

    private void OnNetworkColliderEnabledChanged(bool _, bool newValue)
    {
        if (!IsServer)
        {
            SetColliderEnabled(newValue);
        }
    }

    public void RequestActivate()
    {
        if (IsMultiplayer())
        {
            if (!IsSpawned)
            {
                if (NetworkManager.Singleton.IsServer)
                {
                    Activate();
                }

                return;
            }

            if (IsSpawned && IsServer)
            {
                Activate();
                return;
            }

            if (IsSpawned)
            {
                ActivateRpc();
            }

            return;
        }

        Activate();
    }

    public void RequestDeactivate()
    {
        if (IsMultiplayer())
        {
            if (!IsSpawned)
            {
                if (NetworkManager.Singleton.IsServer)
                {
                    Deactivate();
                }

                return;
            }

            if (IsSpawned && IsServer)
            {
                Deactivate();
                return;
            }

            if (IsSpawned)
            {
                DeactivateRpc();
            }

            return;
        }

        Deactivate();
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void ActivateRpc()
    {
        Activate();
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void DeactivateRpc()
    {
        Deactivate();
    }

    private void Activate()
    {
        if (triggerOnlyOnce && hasTriggered)
        {
            return;
        }

        hasTriggered = true;

        switch (actionType)
        {
            case ActionType.MoveToTarget:
                StartMotion(target != null ? target.position : startPosition);
                break;
            case ActionType.MoveBackToStart:
                StartMotion(startPosition);
                break;
            case ActionType.SetObjectActive:
                SetObjectActiveNetwork(activeState);
                break;
            case ActionType.SetColliderEnabled:
                SetColliderEnabledNetwork(colliderState);
                break;
            case ActionType.ReloadScene:
                ReloadScene();
                break;
        }
    }

    private void Deactivate()
    {
        switch (actionType)
        {
            case ActionType.MoveToTarget:
                StartMotion(startPosition);
                break;
            case ActionType.MoveBackToStart:
                StartMotion(target != null ? target.position : startPosition);
                break;
            case ActionType.SetObjectActive:
                SetObjectActiveNetwork(!activeState);
                break;
            case ActionType.SetColliderEnabled:
                SetColliderEnabledNetwork(!colliderState);
                break;
        }
    }

    private void StartMotion(Vector3 destination)
    {
        if (objectToMove == null || !HasMotionAuthority())
        {
            return;
        }

        double now = GetSynchronizedTime();
        MotionState current = IsSpawned ? networkMotion.Value : localMotion;
        if (current.IsMoving)
        {
            objectToMove.position = Vector3.LerpUnclamped(
                current.From,
                current.To,
                EvaluateProgress(current, now)
            );
        }

        Vector3 origin = objectToMove.position;
        float distance = Vector3.Distance(origin, destination);
        if (distance <= 0.001f)
        {
            objectToMove.position = destination;
            SetMotionState(CreateStationaryState(destination));
            return;
        }

        SetMotionState(new MotionState
        {
            From = origin,
            To = destination,
            StartedAt = now,
            Duration = distance / Mathf.Max(0.001f, moveSpeed),
            IsMoving = true
        });
    }

    private static float EvaluateProgress(MotionState state, double now)
    {
        if (!state.IsMoving || state.Duration <= 0f)
        {
            return 1f;
        }

        return Mathf.Clamp01((float)((now - state.StartedAt) / state.Duration));
    }

    private MotionState CreateStationaryState(Vector3 position)
    {
        return new MotionState
        {
            From = position,
            To = position,
            StartedAt = GetSynchronizedTime(),
            Duration = 0f,
            IsMoving = false
        };
    }

    private void SetMotionState(MotionState state)
    {
        if (IsSpawned)
        {
            if (IsServer)
            {
                networkMotion.Value = state;
            }

            return;
        }

        localMotion = state;
    }

    private bool HasMotionAuthority()
    {
        return !IsSpawned || IsServer;
    }

    private double GetSynchronizedTime()
    {
        return IsSpawned && NetworkManager != null
            ? NetworkManager.ServerTime.Time
            : Time.timeAsDouble;
    }

    private void SetObjectActiveNetwork(bool value)
    {
        if (IsSpawned && IsServer)
        {
            networkObjectActive.Value = value;
            SetObjectActive(value);
            return;
        }

        SetObjectActive(value);
    }

    private void SetColliderEnabledNetwork(bool value)
    {
        if (IsSpawned && IsServer)
        {
            networkColliderEnabled.Value = value;
            SetColliderEnabled(value);
            return;
        }

        SetColliderEnabled(value);
    }

    private void SetObjectActive(bool value)
    {
        if (targetObject != null)
        {
            targetObject.SetActive(value);
        }
    }

    private void SetColliderEnabled(bool value)
    {
        if (targetCollider != null)
        {
            targetCollider.enabled = value;
        }
    }

    private void ReloadScene()
    {
        if (IsMultiplayer())
        {
            if (!NetworkManager.Singleton.IsServer)
            {
                return;
            }

            NetworkManager.Singleton.SceneManager.LoadScene(
                SceneManager.GetActiveScene().name,
                LoadSceneMode.Single
            );
            return;
        }

        SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
    }

    private bool IsMultiplayer()
    {
        return NetworkManager.Singleton != null
            && NetworkManager.Singleton.IsConnectedClient;
    }
}
