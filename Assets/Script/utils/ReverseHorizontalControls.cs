using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Scene-level modifier that swaps left and right movement for every local player.
/// Add this component to a manager object only in scenes that need the effect.
/// </summary>
public class ReverseHorizontalControls : MonoBehaviour
{
    public enum ActivationMode
    {
        AlwaysReversed,
        ToggleOnTrigger
    }

    public enum TriggerConsumption
    {
        Keep,
        DisableGameObject,
        DestroyGameObject
    }

    [Header("Mode")]
    [SerializeField] private ActivationMode activationMode = ActivationMode.AlwaysReversed;

    [Tooltip("Only used in Toggle On Trigger mode. Enable to start with controls reversed.")]
    [SerializeField] private bool initiallyReversed;

    [Header("Toggle On Trigger")]
    [Tooltip("Each trigger must have a Collider2D with Is Trigger enabled.")]
    [SerializeField] private List<Collider2D> triggerZones = new List<Collider2D>();

    [Tooltip("What happens to a trigger after it has toggled the controls.")]
    [SerializeField] private TriggerConsumption triggerConsumption = TriggerConsumption.Keep;

    private readonly Collider2D[] overlapResults = new Collider2D[16];
    private readonly HashSet<Collider2D> touchedZones = new HashSet<Collider2D>();

    private void OnEnable()
    {
        touchedZones.Clear();
        PlayerMovement.SetHorizontalInputInverted(
            activationMode == ActivationMode.AlwaysReversed || initiallyReversed
        );
    }

    private void Update()
    {
        if (activationMode != ActivationMode.ToggleOnTrigger)
        {
            return;
        }

        PlayerMovement player = PlayerMovement.LocalPlayer;
        if (player == null)
        {
            return;
        }

        for (int i = triggerZones.Count - 1; i >= 0; i--)
        {
            Collider2D triggerZone = triggerZones[i];
            if (triggerZone == null)
            {
                touchedZones.Remove(triggerZone);
                continue;
            }

            bool isTouching = IsPlayerTouchingZone(triggerZone, player);
            if (isTouching)
            {
                if (touchedZones.Add(triggerZone))
                {
                    PlayerMovement.SetHorizontalInputInverted(
                        !PlayerMovement.IsHorizontalInputInverted
                    );

                    ConsumeTrigger(triggerZone);
                }
            }
            else
            {
                touchedZones.Remove(triggerZone);
            }
        }
    }

    private void OnDisable()
    {
        // This component owns the scene-wide modifier, so leaving its scene
        // always restores normal controls for the next scene.
        PlayerMovement.SetHorizontalInputInverted(false);
    }

    private bool IsPlayerTouchingZone(Collider2D triggerZone, PlayerMovement player)
    {
        // NoFilter is an instance method in current Unity versions.
        ContactFilter2D filter = new ContactFilter2D().NoFilter();
        filter.useTriggers = true;

        int overlapCount = triggerZone.OverlapCollider(filter, overlapResults);
        for (int i = 0; i < overlapCount; i++)
        {
            Collider2D overlap = overlapResults[i];
            if (overlap != null && overlap.GetComponentInParent<PlayerMovement>() == player)
            {
                return true;
            }
        }

        return false;
    }

    private void ConsumeTrigger(Collider2D triggerZone)
    {
        switch (triggerConsumption)
        {
            case TriggerConsumption.DisableGameObject:
                triggerZone.gameObject.SetActive(false);
                break;

            case TriggerConsumption.DestroyGameObject:
                Destroy(triggerZone.gameObject);
                break;
        }
    }
}
