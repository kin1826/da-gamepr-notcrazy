using UnityEngine;

public static class GameData
{
    public static int SelectedDino = 0;

    public static bool IsMultiplayer = false;

    public static bool IsHost = false;

    // Set when the player who is leaving explicitly confirms exiting a multiplayer match.
    public static bool IsLeavingMatchIntentionally = false;

    // Carried across the gameplay-to-menu scene load and consumed by MainManager.
    public static bool ShowTeammateDisconnectedNotice = false;

    public static int SelectedLevel = 1;
}
