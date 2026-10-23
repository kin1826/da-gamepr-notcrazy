using TMPro;
using UnityEngine;

/// <summary>Small top-of-screen readout for this play session's pending level rewards.</summary>
public sealed class LevelRewardHud : MonoBehaviour
{
    private static LevelRewardHud active;
    private TMP_Text label;

    private void Awake()
    {
        active = this;
        BuildLabel();
        Refresh();
    }

    private void OnDestroy()
    {
        if (active == this) active = null;
    }

    public static void RefreshActive() => active?.Refresh();

    private void BuildLabel()
    {
        TMP_Text[] texts = FindObjectsByType<TMP_Text>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (TMP_Text candidate in texts)
        {
            if (candidate.gameObject.name == "Coin_Text")
            {
                label = candidate;
                break;
            }
        }

        if (label == null)
        {
            Debug.LogWarning("LevelRewardHud: Canvas/Coin_Text was not found; reward is still saved locally.");
            return;
        }

        label.raycastTarget = false;
    }

    private void Refresh()
    {
        if (label == null || SaveManager.Data == null) return;

        int pending = SaveManager.Data.pendingLevelRewardGold;
        if (pending <= 0)
        {
            label.gameObject.SetActive(false);
            return;
        }

        label.gameObject.SetActive(true);
        // Show the total reward staged for this run, not just the most recent
        // level's reward (e.g. +10 after level 1, then +20 after level 2).
        label.text = $"+{pending} coins";
    }
}
