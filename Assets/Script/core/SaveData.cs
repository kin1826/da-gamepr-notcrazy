using System;

[Serializable]
public class SaveData
{
    public int    highestLevel  = 1;
    public int    selectedDino  = 0;

    // Auth — lưu để auto-login khi mở lại app
    public long   savedUserId   = 0;
    public string savedEmail    = "";
    public string savedName     = "";
    public string savedToken    = "";
    public int    savedHighestLevel = 1;
    public int    savedGold         = 0;
    public int    savedGuestGold    = 0;

    // Level-clear rewards are staged locally until the player returns to Menu.
    public int    pendingLevelRewardGold = 0;
    public int    lastLevelRewardGold = 0;
    public int    unsyncedRewardGold = 0;
    public System.Collections.Generic.List<string> processedLevelRewardIds = new System.Collections.Generic.List<string>();
}
