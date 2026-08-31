namespace KeitaToolbox;

internal static class AutoTreasureOpenPolicy
{
    internal static bool IsReady(
        bool enabled,
        bool boundByDuty,
        bool soloOnly,
        bool isSoloDutySession,
        bool playerReady,
        bool inCombat,
        bool occupied,
        long millisecondsSinceCombat,
        int postCombatCooldownMs) =>
        enabled &&
        boundByDuty &&
        (!soloOnly || isSoloDutySession) &&
        playerReady &&
        !inCombat &&
        !occupied &&
        millisecondsSinceCombat >= postCombatCooldownMs;

    internal static bool IsSoloDutySession(int partyCount, int currentPartyMemberCount) =>
        partyCount == 1 && currentPartyMemberCount <= 1;
}
