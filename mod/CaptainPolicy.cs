namespace ValheimBoosted;

internal sealed class CaptainPolicy
{
    internal static bool Eligible(long peer, long characterOwner, long playerId, long grantedUser, double helmDistanceSquared, bool activeArea) =>
        peer != 0 && characterOwner == peer && playerId != 0 && grantedUser == playerId && activeArea
        && helmDistanceSquared >= 0 && helmDistanceSquared <= 2.25;
    private long candidate;
    private double since, lastTransfer = double.NegativeInfinity;
    internal bool ShouldTransfer(long owner, long captain, bool eligible, double now)
    {
        if (!eligible || captain == 0 || owner == 0 || owner == captain) { candidate = 0; return false; }
        if (candidate != captain) { candidate = captain; since = now; return false; }
        return now - since >= 1 && now - lastTransfer >= 5;
    }
    internal void Transferred(double now) { lastTransfer = now; candidate = 0; }
}
