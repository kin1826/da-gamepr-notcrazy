using UnityEngine;

/// <summary>
/// Scene-level Flappy Bird jump mode. Every jump input launches the local player
/// upward, even while airborne; gravity and solid ceilings still behave normally.
/// </summary>
public class FlappyJumpModifier : MonoBehaviour
{
    public static FlappyJumpModifier Active { get; private set; }
    public static bool IsActive => Active != null;

    [Tooltip("Upward velocity applied on every jump press. Higher values make each flap stronger.")]
    [Min(0f)]
    [SerializeField] private float flapVelocity = 12f;

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

    public static bool TryGetFlapVelocity(out float velocity)
    {
        velocity = 0f;
        if (Active == null)
        {
            return false;
        }

        velocity = Active.flapVelocity;
        return true;
    }
}
