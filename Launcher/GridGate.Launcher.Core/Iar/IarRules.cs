using GridGate.Launcher.Core.Grid;
using GridGate.Launcher.Core.Settings;

namespace GridGate.Launcher.Core.Iar;

/// <summary>Why a resident can't start a backup or an upload now, checked in this order.</summary>
public enum IarRefusal { GridOff, PersonOff, NamedInCase, DailyLimit, Busy, GridBusy, LowDisk, NoRegion, TakedownsUnreadable }

/// <summary>
/// Whether a resident may start a backup (<see cref="IarJobKind.Save"/>) or an upload (<see cref="IarJobKind.Load"/>) now (spec
/// 2026-11-11-iar-backups-design §5 and §8). <c>effective</c> is the grid's settings with the person's own limits in place. The
/// owner's and admins' own jobs never ask these rules. <see cref="IarRefusal.NoRegion"/> is never returned here: it comes from the
/// region job itself and is shown with the same words.
/// </summary>
public static class IarRules
{
    private const long Gb = 1L << 30;

    public static IarRefusal? Refusal(IarJobKind kind, IarSettings effective, bool personOn, bool namedInOpenCase, int usedToday,
        bool personHasJob, int jobsRunning, long freeDiskBytes, bool takedownsReadable)
    {
        var save = kind == IarJobKind.Save;
        if (!(save ? effective.DownloadsOn : effective.UploadsOn)) return IarRefusal.GridOff;
        if (!personOn) return IarRefusal.PersonOff;
        if (namedInOpenCase) return IarRefusal.NamedInCase;
        if (usedToday >= (save ? effective.DownloadsPerDay : effective.UploadsPerDay)) return IarRefusal.DailyLimit;
        if (personHasJob) return IarRefusal.Busy;
        if (jobsRunning >= effective.JobsAtOnce) return IarRefusal.GridBusy;
        if (freeDiskBytes < effective.MinFreeDiskGb * Gb) return IarRefusal.LowDisk;
        if (!save && !takedownsReadable) return IarRefusal.TakedownsUnreadable;
        return null;
    }
}
