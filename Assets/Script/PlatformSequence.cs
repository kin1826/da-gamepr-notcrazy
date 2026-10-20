using System;
using Unity.Netcode;
using UnityEngine;

public class PlatformSequence : NetworkBehaviour
{
    public Transform platform;
    public Transform targetPos;
    public float speed = 5f;

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

    private readonly NetworkVariable<MotionState> motion = new(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private MotionState localMotion;
    private Vector3 startPosition;

    private void Awake()
    {
        if (platform != null)
        {
            startPosition = platform.position;
        }

        localMotion = CreateStationaryState(startPosition);
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        if (IsServer)
        {
            motion.Value = CreateStationaryState(startPosition);
        }
    }

    private void Update()
    {
        if (platform == null)
        {
            return;
        }

        MotionState state = IsSpawned ? motion.Value : localMotion;
        if (!state.IsMoving)
        {
            return;
        }

        double now = GetSynchronizedTime();
        float progress = EvaluateProgress(state, now);
        platform.position = Vector3.LerpUnclamped(state.From, state.To, progress);

        if (progress >= 1f && (!IsSpawned || IsServer))
        {
            state.From = state.To;
            state.StartedAt = now;
            state.Duration = 0f;
            state.IsMoving = false;
            SetMotionState(state);
        }
    }

    public void TriggerEvent()
    {
        if (platform == null || targetPos == null)
        {
            Debug.LogWarning("PlatformSequence: Platform or target position is missing.");
            return;
        }

        if (IsMultiplayer())
        {
            if (IsSpawned && IsServer)
            {
                StartMotion();
            }
            else if (IsSpawned)
            {
                TriggerRpc();
            }

            return;
        }

        StartMotion();
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void TriggerRpc()
    {
        StartMotion();
    }

    private void StartMotion()
    {
        if (IsSpawned && !IsServer)
        {
            return;
        }

        Vector3 from = platform.position;
        Vector3 to = targetPos.position;
        float distance = Vector3.Distance(from, to);
        if (distance <= 0.001f)
        {
            platform.position = to;
            SetMotionState(CreateStationaryState(to));
            return;
        }

        SetMotionState(new MotionState
        {
            From = from,
            To = to,
            StartedAt = GetSynchronizedTime(),
            Duration = distance / Mathf.Max(0.001f, speed),
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
                motion.Value = state;
            }

            return;
        }

        localMotion = state;
    }

    private double GetSynchronizedTime()
    {
        return IsSpawned && NetworkManager != null
            ? NetworkManager.ServerTime.Time
            : Time.timeAsDouble;
    }

    private bool IsMultiplayer()
    {
        return NetworkManager.Singleton != null
            && NetworkManager.Singleton.IsConnectedClient;
    }
}
