using System;

namespace ValheimBoosted;

// Uses monotonic time and plain inputs; never pauses simulation or blocks a thread.
internal sealed class IdleServerPolicy
{
    private readonly double delay;
    private readonly int idleFrameRate;
    private object session;
    private double? emptySince;
    private int savedFrameRate, appliedFrameRate;
    internal bool Idle { get; private set; }
    internal bool Conflict { get; private set; }

    internal IdleServerPolicy(double delay, int idleFrameRate)
    {
        this.delay = delay;
        this.idleFrameRate = idleFrameRate;
    }

    internal int Update(object world, bool eligible, bool busy, double now, int frameRate)
    {
        if (!ReferenceEquals(session, world))
        {
            frameRate = Restore(frameRate);
            session = world;
            Conflict = false;
        }
        if (Idle && frameRate != appliedFrameRate)
        {
            // Do not fight another component or later overwrite its frame cap.
            Idle = false;
            Conflict = true;
            emptySince = null;
        }
        if (world == null || !eligible || busy) return Restore(frameRate);
        if (Conflict || Idle) return frameRate;
        if (!emptySince.HasValue) emptySince = now;
        if (now - emptySince.Value < delay) return frameRate;
        savedFrameRate = frameRate;
        appliedFrameRate = frameRate > 0 ? Math.Min(frameRate, idleFrameRate) : idleFrameRate;
        Idle = true;
        return appliedFrameRate;
    }

    internal int Restore(int frameRate)
    {
        int restored = Idle && frameRate == appliedFrameRate ? savedFrameRate : frameRate;
        Idle = false;
        emptySince = null;
        return restored;
    }
}
