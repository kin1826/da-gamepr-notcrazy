using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerMovement : NetworkBehaviour
{
    public static PlayerMovement LocalPlayer { get; private set; }

    // Co-op players should not physically push each other. Keeping this as
    // collider-pair ignores (instead of a layer rule) preserves all player
    // interactions with ground, moving platforms, buttons, triggers and traps.
    private static readonly HashSet<Collider2D> ActivePlayerColliders = new();

    // Set by scene-specific gameplay modifiers. Keeping this here makes both
    // keyboard input and the mobile movement buttons follow the same rule.
    public static bool IsHorizontalInputInverted { get; private set; }

    public float moveSpeed = 8f;
    public float jumpForce = 5f;
    public float gravity = 2f;

    [Header("Ground Check")]
    public Transform groundCheck;
    public float groundRadius = 0.2f;
    public LayerMask groundLayer;

    [Header("Physics")]
    public bool useNoFrictionMaterial = true;

    [SerializeField] private Animator animator;

    [Header("Character Audio")]
    [SerializeField] private AudioSource characterAudioSource;
    [SerializeField] private AudioClip footstepSfx;
    [SerializeField] private AudioClip jumpSfx;
    [SerializeField] private AudioClip deathSfx;
    [SerializeField] private AudioClip respawnSfx;
    [Range(0f, 1f)] [SerializeField] private float localPlayerSfxVolume = 0.5f;
    [Range(0f, 1f)] [SerializeField] private float teammateSfxVolume = 0.2f;
    [Min(0.1f)] [SerializeField] private float footstepInterval = 0.32f;

    private NetworkVariable<bool> isRunningNetwork = new NetworkVariable<bool>(false);
    private NetworkVariable<int> facingSignNetwork = new NetworkVariable<int>(1);

    private Rigidbody2D rb;
    private Collider2D[] playerColliders;
    private Vector2 moveInput;
    private Vector2 rawMoveInput;
    private bool isGrounded;
    private bool lastRunning;
    private int lastFacingSign = 1;
    private float footstepElapsed;

    private void Awake()
    {
        if (characterAudioSource == null)
        {
            characterAudioSource = GetComponent<AudioSource>();
        }

        if (characterAudioSource != null)
        {
            characterAudioSource.playOnAwake = false;
            characterAudioSource.loop = false;
            characterAudioSource.spatialBlend = 0f;
            characterAudioSource.dopplerLevel = 0f;
        }
    }

    private void Start()
    {
        rb = GetComponent<Rigidbody2D>();

        if (!IsMultiplayer())
        {
            LocalPlayer = this;

            // NetworkRigidbody2D starts unspawned objects as kinematic so that
            // remote network instances cannot simulate their own physics.
            // Single-player characters are instantiated without being network
            // spawned, so explicitly restore normal physics for that mode.
            if (rb != null)
            {
                rb.bodyType = RigidbodyType2D.Dynamic;
            }
        }

        if (rb == null)
        {
            Debug.LogError("PlayerMovement: Rigidbody2D not found on this object!");
        }

        if (groundCheck == null)
        {
            Debug.LogError("PlayerMovement: groundCheck Transform not assigned!");
        }

        IgnoreOtherPlayerCollisions();
        ApplyNoFrictionMaterial();
    }

    private void IgnoreOtherPlayerCollisions()
    {
        playerColliders = GetComponentsInChildren<Collider2D>();
        ActivePlayerColliders.RemoveWhere(collider => collider == null);

        foreach (Collider2D playerCollider in playerColliders)
        {
            if (playerCollider == null || playerCollider.isTrigger)
            {
                continue;
            }

            foreach (Collider2D otherPlayerCollider in ActivePlayerColliders)
            {
                if (otherPlayerCollider != null && otherPlayerCollider != playerCollider)
                {
                    Physics2D.IgnoreCollision(playerCollider, otherPlayerCollider, true);
                }
            }

            ActivePlayerColliders.Add(playerCollider);
        }
    }

    public override void OnNetworkSpawn()
    {
        if (IsOwner)
        {
            LocalPlayer = this;
        }

        isRunningNetwork.OnValueChanged += OnRunningChanged;
        facingSignNetwork.OnValueChanged += OnFacingChanged;

        ApplyRunning(isRunningNetwork.Value);
        ApplyFacing(facingSignNetwork.Value);
    }

    public void OnMove(InputValue value)
    {
        if (!HasLocalControl()) return;

        SetMoveInput(value.Get<Vector2>());
    }

    public void OnJump(InputValue value)
    {
        if (!HasLocalControl()) return;

        if (value.isPressed && rb != null && (isGrounded || FlappyJumpModifier.IsActive))
        {
            Jump();
        }
    }

    public void MoveLeftDown()
    {
        if (!HasLocalControl()) return;

        SetMoveInput(Vector2.left);
    }

    public void MoveRightDown()
    {
        if (!HasLocalControl()) return;

        SetMoveInput(Vector2.right);
    }

    public void MoveStop()
    {
        if (!HasLocalControl()) return;

        SetMoveInput(Vector2.zero);
    }

    public void JumpPressed()
    {
        if (!HasLocalControl()) return;

        if (rb != null && (isGrounded || FlappyJumpModifier.IsActive))
        {
            Jump();
        }
    }

    private void SetMoveInput(Vector2 input)
    {
        rawMoveInput = input;
        ApplyMovementModifier();
    }

    private void ApplyMovementModifier()
    {
        moveInput = rawMoveInput;

        if (IsHorizontalInputInverted)
        {
            moveInput.x = -moveInput.x;
        }
    }

    public static void SetHorizontalInputInverted(bool inverted)
    {
        IsHorizontalInputInverted = inverted;
        LocalPlayer?.ApplyMovementModifier();
    }

    private void Jump()
    {
        if (IsMultiplayer() && !IsOwner) return;
        if (rb == null) return;

        if (FlappyJumpModifier.TryGetFlapVelocity(out float flapVelocity))
        {
            rb.linearVelocity = new Vector2(rb.linearVelocity.x, flapVelocity);
            if (flapVelocity > 0f) PlayJumpSfx();
            return;
        }

        if (CeilingJumpModifier.TryGetJumpVelocity(this, rb, out float ceilingJumpVelocity))
        {
            rb.linearVelocity = new Vector2(rb.linearVelocity.x, ceilingJumpVelocity);
            if (ceilingJumpVelocity > 0f) PlayJumpSfx();
            return;
        }

        rb.linearVelocity = new Vector2(rb.linearVelocity.x, jumpForce);
        if (jumpForce > 0f) PlayJumpSfx();
    }

    private void Update()
    {
        if (groundCheck != null)
        {
            isGrounded = Physics2D.OverlapCircle(groundCheck.position, groundRadius, groundLayer);
        }

        if (!HasLocalControl())
        {
            UpdateFootstepSfx(isRunningNetwork.Value);
            return;
        }

        if (rb == null || groundCheck == null) return;

        float speed = Mathf.Abs(moveInput.x);
        bool isRunning = speed > 0.1f;

        if (speed < 0.3f && Mathf.Abs(rb.linearVelocity.x) > 0.3f)
        {
            isRunning = true;
        }

        ApplyRunning(isRunning);
        UpdateFootstepSfx(isRunning);
        if (isRunning != lastRunning)
        {
            lastRunning = isRunning;
            if (IsMultiplayer())
            {
                SetRunningServerRpc(isRunning);
            }
        }

        if (moveInput.x != 0)
        {
            int facingSign = moveInput.x > 0 ? 1 : -1;
            ApplyFacing(facingSign);

            if (facingSign != lastFacingSign)
            {
                lastFacingSign = facingSign;
                if (IsMultiplayer())
                {
                    SetFacingServerRpc(facingSign);
                }
            }
        }
    }

    private void FixedUpdate()
    {
        if (!HasLocalControl()) return;
        if (rb == null) return;

        rb.linearVelocity = new Vector2(moveInput.x * moveSpeed, rb.linearVelocity.y);
    }

    private void UpdateFootstepSfx(bool isMoving)
    {
        if (!isMoving || !isGrounded || footstepSfx == null)
        {
            footstepElapsed = 0f;
            return;
        }

        footstepElapsed += Time.deltaTime;
        if (footstepElapsed < footstepInterval) return;

        footstepElapsed = 0f;
        PlayCharacterSfx(footstepSfx);
    }

    private void PlayJumpSfx()
    {
        if (jumpSfx == null) return;

        // Play immediately for the owner so the sound stays in step with input.
        PlayCharacterSfx(jumpSfx);

        // Jump is an occasional event, so relay it once to the teammate rather
        // than trying to reproduce it from smoothed remote physics.
        if (IsMultiplayer())
        {
            RelayJumpSfxServerRpc();
        }
    }

    public void PlayDeathSfx()
    {
        PlayCharacterSfx(deathSfx);
    }

    public void PlayRespawnSfx()
    {
        PlayCharacterSfx(respawnSfx);
    }

    public static PlayerMovement FindForClient(ulong clientId)
    {
        PlayerMovement[] players = FindObjectsByType<PlayerMovement>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.None
        );

        foreach (PlayerMovement player in players)
        {
            if (player != null && player.IsSpawned && player.OwnerClientId == clientId)
            {
                return player;
            }
        }

        return null;
    }

    private void PlayCharacterSfx(AudioClip clip)
    {
        if (clip == null || characterAudioSource == null) return;

        AudioManager audioManager = AudioManager.Instance;
        if (audioManager != null && audioManager.IsSFXMuted) return;

        float masterSfxVolume = audioManager != null ? audioManager.sfxVolume : 1f;
        float characterVolume = HasLocalControl() ? localPlayerSfxVolume : teammateSfxVolume;
        characterAudioSource.PlayOneShot(clip, masterSfxVolume * characterVolume);
    }

    [ServerRpc]
    private void RelayJumpSfxServerRpc(ServerRpcParams rpcParams = default)
    {
        if (NetworkManager.Singleton == null || rpcParams.Receive.SenderClientId != OwnerClientId)
        {
            return;
        }

        List<ulong> recipients = new List<ulong>();
        foreach (ulong clientId in NetworkManager.Singleton.ConnectedClientsIds)
        {
            if (clientId != OwnerClientId)
            {
                recipients.Add(clientId);
            }
        }

        if (recipients.Count == 0) return;

        RelayJumpSfxClientRpc(new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = recipients.ToArray() }
        });
    }

    [ClientRpc]
    private void RelayJumpSfxClientRpc(ClientRpcParams clientRpcParams = default)
    {
        PlayCharacterSfx(jumpSfx);
    }

    // Called by KillZoneReload on the owning client to immediately notify the server
    // without waiting for NetworkTransform lag (~50-100ms).
    // Player is already frozen+hidden on the client side before this RPC is sent.
    [ServerRpc]
    public void RequestKillZoneReloadServerRpc()
    {
        if (!IsServer) return;
        RespawnSync.Instance?.TriggerShowPanel(OwnerClientId);
    }

    // Relay used by LevelComplete when it doesn't have a NetworkObject component.
    // Client calls this from OnTriggerEnter/Exit; server forwards to LevelComplete.
    [ServerRpc]
    public void NotifyLevelExitServerRpc(string groupId, bool isEntering, ServerRpcParams rpcParams = default)
    {
        if (isEntering)
            LevelComplete.ServerHandlePlayerEnter(rpcParams.Receive.SenderClientId, groupId);
        else
            LevelComplete.ServerHandlePlayerExit(rpcParams.Receive.SenderClientId, groupId);
    }

    [ClientRpc]
    public void GrantLevelRewardClientRpc(int amount, string rewardEventId)
    {
        UserSession.QueueLevelReward(amount, rewardEventId);
    }

    [ServerRpc]
    private void SetRunningServerRpc(bool value)
    {
        isRunningNetwork.Value = value;
    }

    [ServerRpc]
    private void SetFacingServerRpc(int value)
    {
        facingSignNetwork.Value = value >= 0 ? 1 : -1;
    }

    private void OnRunningChanged(bool oldValue, bool newValue)
    {
        ApplyRunning(newValue);
    }

    private void OnFacingChanged(int oldValue, int newValue)
    {
        ApplyFacing(newValue);
    }

    private void ApplyRunning(bool value)
    {
        if (animator != null)
        {
            animator.SetBool("isRunning", value);
        }
    }

    private void ApplyFacing(int value)
    {
        Vector3 scale = transform.localScale;
        scale.x = (value >= 0 ? 1 : -1) * Mathf.Abs(scale.x);
        transform.localScale = scale;
    }

    private bool HasLocalControl()
    {
        return IsMultiplayer() ? IsOwner : true;
    }

    private bool IsMultiplayer()
    {
        return NetworkManager.Singleton != null && NetworkManager.Singleton.IsConnectedClient;
    }

    private void ApplyNoFrictionMaterial()
    {
        if (!useNoFrictionMaterial)
        {
            return;
        }

        Collider2D playerCollider = GetComponent<Collider2D>();
        if (playerCollider == null)
        {
            return;
        }

        playerCollider.sharedMaterial = new PhysicsMaterial2D("Player_NoFriction")
        {
            friction = 0f,
            bounciness = 0f
        };
    }

    public override void OnNetworkDespawn()
    {
        if (LocalPlayer == this)
        {
            LocalPlayer = null;
        }

        isRunningNetwork.OnValueChanged -= OnRunningChanged;
        facingSignNetwork.OnValueChanged -= OnFacingChanged;
        base.OnNetworkDespawn();
    }

    public override void OnDestroy()
    {
        if (playerColliders != null)
        {
            foreach (Collider2D playerCollider in playerColliders)
            {
                if (playerCollider != null)
                {
                    ActivePlayerColliders.Remove(playerCollider);
                }
            }
        }

        base.OnDestroy();
    }
}
