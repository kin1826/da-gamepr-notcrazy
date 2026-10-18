using UnityEngine;

/// <summary>
/// Scene-level jump modifier. It launches the player high enough to collide with
/// the nearest solid ceiling detected above their Collider2D.
/// </summary>
public class CeilingJumpModifier : MonoBehaviour
{
    public enum NoCeilingBehaviour
    {
        NormalJump,
        BlockJump
    }

    public static CeilingJumpModifier Active { get; private set; }

    [Header("Ceiling Detection")]
    [Tooltip("The layer containing solid ceilings. Level_08 GroundTilemap uses the Ground layer.")]
    [SerializeField] private LayerMask ceilingLayer = 1 << 6;

    [Min(0.1f)]
    [SerializeField] private float maxCeilingDistance = 20f;

    [Tooltip("Extra upward distance so the player reliably makes contact with the ceiling.")]
    [Min(0f)]
    [SerializeField] private float contactPadding = 0.03f;

    [Tooltip("1 = calculated speed. Increase this to make the player reach the ceiling faster.")]
    [Min(1f)]
    [SerializeField] private float ascentSpeedMultiplier = 1f;

    [Header("When No Ceiling Is Found")]
    [SerializeField] private NoCeilingBehaviour noCeilingBehaviour = NoCeilingBehaviour.NormalJump;

    private readonly RaycastHit2D[] ceilingHits = new RaycastHit2D[8];

    private void OnEnable()
    {
        Active = this;
    }

    private void OnDisable()
    {
        if (Active == this)
        {
            Active = null;
        }
    }

    public static bool TryGetJumpVelocity(PlayerMovement player, Rigidbody2D body, out float velocity)
    {
        velocity = 0f;
        return Active != null && Active.TryCalculateJumpVelocity(player, body, out velocity);
    }

    private bool TryCalculateJumpVelocity(PlayerMovement player, Rigidbody2D body, out float velocity)
    {
        velocity = 0f;

        Collider2D playerCollider = player.GetComponent<Collider2D>();
        if (playerCollider == null)
        {
            return noCeilingBehaviour == NoCeilingBehaviour.BlockJump;
        }

        ContactFilter2D filter = new ContactFilter2D();
        filter.SetLayerMask(ceilingLayer);
        filter.useTriggers = false;

        int hitCount = playerCollider.Cast(
            Vector2.up,
            filter,
            ceilingHits,
            maxCeilingDistance
        );

        float ceilingDistance = float.MaxValue;
        for (int i = 0; i < hitCount; i++)
        {
            RaycastHit2D hit = ceilingHits[i];
            if (hit.collider != null && hit.distance > 0.001f && hit.normal.y < -0.01f)
            {
                ceilingDistance = Mathf.Min(ceilingDistance, hit.distance);
            }
        }

        if (ceilingDistance == float.MaxValue)
        {
            return noCeilingBehaviour == NoCeilingBehaviour.BlockJump;
        }

        float gravity = Mathf.Abs(Physics2D.gravity.y * body.gravityScale);
        if (gravity <= 0.001f)
        {
            return false;
        }

        velocity = Mathf.Sqrt(2f * gravity * (ceilingDistance + contactPadding))
            * ascentSpeedMultiplier;
        return true;
    }
}
