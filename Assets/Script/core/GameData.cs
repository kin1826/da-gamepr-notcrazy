using UnityEngine;

public static class GameData
{
    public static int SelectedDino = 0;

    public static bool IsMultiplayer = false;

    public static bool IsHost = false;

    // Set just before a death-triggered level reload and consumed after the
    // local player finishes its spawn sequence, so initial level entry is silent.
    public static bool IsRespawning = false;

    // Set when the player who is leaving explicitly confirms exiting a multiplayer match.
    public static bool IsLeavingMatchIntentionally = false;

    // Carried across the gameplay-to-menu scene load and consumed by MainManager.
    public static bool ShowTeammateDisconnectedNotice = false;

    public static int SelectedLevel = 1;
}
