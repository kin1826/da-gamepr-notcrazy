// Quản lý trạng thái đăng nhập trong runtime và local storage.
public static class UserSession
{
    public static AuthResponse Current { get; private set; }
    public static bool IsGuest { get; private set; }

    public static bool IsLoggedIn => Current != null && !string.IsNullOrEmpty(Current.token);

    // Gọi sau khi login/register thành công — lưu vào RAM và disk
    public static void Set(AuthResponse user)
    {
        EnsureSaveData();
        IsGuest = false;
        user.gold += SaveManager.Data.unsyncedRewardGold;
        Current = user;
        GameData.SelectedLevel = user.highestLevel;

        SaveManager.Data.savedUserId       = user.id;
        SaveManager.Data.savedEmail        = user.email;
        SaveManager.Data.savedName         = user.name;
        SaveManager.Data.savedToken        = user.token;
        SaveManager.Data.savedHighestLevel = user.highestLevel;
        SaveManager.Data.savedGold         = user.gold;
        SaveManager.Save();
        CommitPendingLevelRewards();
    }

    // Gọi khi app khởi động — thử khôi phục session từ disk
    public static bool TryRestore()
    {
        var data = SaveManager.Data;
        if (string.IsNullOrEmpty(data.savedToken) || data.savedUserId == 0)
            return false;

        Current = new AuthResponse
        {
            id           = data.savedUserId,
            email        = data.savedEmail,
            name         = data.savedName,
            token        = data.savedToken,
            highestLevel = data.savedHighestLevel,
            gold         = data.savedGold
        };

        GameData.SelectedLevel = Current.highestLevel;
        return true;
    }

    public static bool SpendGold(int amount)
    {
        if (Current == null || Current.gold < amount) return false;
        Current.gold -= amount;
        if (IsGuest)
        {
            SaveManager.Data.savedGuestGold = Current.gold;
        }
        else
        {
            SaveManager.Data.savedGold = Current.gold;
        }
        SaveManager.Save();
        if (!IsGuest) ApiManager.Instance?.UpdateGold(-amount);
        return true;
    }

    /// <summary>Applies a server balance while preserving locally queued rewards.</summary>
    public static void ApplyServerGold(int serverGold)
    {
        EnsureSaveData();
        int mergedGold = System.Math.Max(0, serverGold) + SaveManager.Data.unsyncedRewardGold;
        SaveManager.Data.savedGold = mergedGold;
        if (Current != null) Current.gold = mergedGold;
        SaveManager.Save();
        MainManager.Instance?.RefreshGold();
    }

    /// <summary>
    /// Records one level-clear reward locally. The event ID makes the reward
    /// idempotent across scene transitions and app restarts.
    /// </summary>
    public static bool QueueLevelReward(int amount, string rewardEventId)
    {
        if (amount <= 0 || string.IsNullOrWhiteSpace(rewardEventId)) return false;
        EnsureSaveData();

        if (SaveManager.Data.processedLevelRewardIds == null)
            SaveManager.Data.processedLevelRewardIds = new System.Collections.Generic.List<string>();

        if (SaveManager.Data.processedLevelRewardIds.Contains(rewardEventId)) return false;

        SaveManager.Data.processedLevelRewardIds.Add(rewardEventId);
        SaveManager.Data.pendingLevelRewardGold += amount;
        SaveManager.Data.lastLevelRewardGold = amount;
        SaveManager.Save();
        LevelRewardHud.RefreshActive();
        return true;
    }

    /// <summary>
    /// Transfers this play session's locally staged rewards into the local
    /// wallet. Called when returning to the main menu/leaving a match.
    /// </summary>
    public static int CommitPendingLevelRewards()
    {
        EnsureSaveData();
        int amount = SaveManager.Data.pendingLevelRewardGold;
        if (amount <= 0) return 0;

        SaveManager.Data.pendingLevelRewardGold = 0;
        SaveManager.Data.lastLevelRewardGold = 0;
        if (IsGuest)
        {
            SaveManager.Data.savedGuestGold += amount;
            if (Current != null) Current.gold = SaveManager.Data.savedGuestGold;
        }
        else
        {
            SaveManager.Data.savedGold += amount;
            SaveManager.Data.unsyncedRewardGold += amount;
            if (Current != null) Current.gold = SaveManager.Data.savedGold;
        }
        SaveManager.Save();
        MainManager.Instance?.RefreshGold();

        // Server sync intentionally remains opt-in while the database/API is unavailable.
        return amount;
    }

    /// <summary>
    /// Sync hook for when the backend is healthy again. Rewards stay queued
    /// locally if the request fails; this method is deliberately not called yet.
    /// </summary>
    public static void SyncQueuedRewardGoldToServer()
    {
        // TODO: connect this queue to an idempotent server reward endpoint once
        // the database is healthy. Do not send a raw gold delta until retries
        // cannot accidentally grant the same reward twice.
    }

    private static void EnsureSaveData()
    {
        if (SaveManager.Data == null) SaveManager.Load();
    }

    // Gọi khi chơi chế độ khách — chỉ set RAM, không lưu disk
    public static void SetGuest()
    {
        EnsureSaveData();
        IsGuest = true;
        Current = new AuthResponse
        {
            id           = 0,
            email        = "",
            name         = "Guest",
            token        = "",
            highestLevel = 1,
            gold         = SaveManager.Data.savedGuestGold
        };
        GameData.SelectedLevel = 1;
    }

    // Gọi khi logout
    public static void Clear()
    {
        Current = null;
        IsGuest = false;
        SaveManager.Data.savedUserId  = 0;
        SaveManager.Data.savedEmail   = "";
        SaveManager.Data.savedName    = "";
        SaveManager.Data.savedToken   = "";
        SaveManager.Save();
    }
}
