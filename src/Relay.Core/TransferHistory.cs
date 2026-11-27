using System.Globalization;

namespace Relay.Core;

public enum HistoryStatus { All, Active, AwaitingSave, Delivered, Attention, Stopped }
public enum HistoryDirection { All, Outgoing, Incoming }

public static class TransferHistory
{
    public static TransferJob[] Search(IEnumerable<TransferJob> jobs, string query, HistoryStatus status, HistoryDirection direction)
    {
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return jobs.Where(job => (status == HistoryStatus.All || StatusOf(job) == status ||
                status == HistoryStatus.Delivered && job.HasConfirmedPeerSync)
            && (direction == HistoryDirection.All || DirectionOf(job) == direction)
            && terms.All(term => Matches(job, term)))
            .OrderByDescending(job => job.Created).ToArray();
    }

    public static HistoryStatus StatusOf(TransferJob job)
    {
        if (job.Saved) return HistoryStatus.Delivered;
        if (job.Error != null || job.State is TransferJob.PreparationInterrupted or TransferJob.PreparationFailed or
            TransferJob.FriendAccessRevoked or TransferJob.ShareMissing or TransferJob.SaveFailed or TransferJob.SaveInterrupted or TransferJob.SaveRejected)
            return HistoryStatus.Attention;
        if (job.CanSave) return HistoryStatus.AwaitingSave;
        if (job.Detached || job.Paused) return HistoryStatus.Stopped;
        return job.HasConfirmedPeerSync ? HistoryStatus.Delivered : HistoryStatus.Active;
    }

    public static HistoryDirection DirectionOf(TransferJob job) => job.Kind switch
    {
        "send" or "watch-send" => HistoryDirection.Outgoing,
        "receive" or "watch-receive" => HistoryDirection.Incoming,
        _ => HistoryDirection.All
    };

    public static string DirectionLabel(TransferJob job) => job.Kind switch
    {
        "send" => "傳送",
        "receive" => "接收",
        "watch-send" => "持續傳送",
        "watch-receive" => "持續接收",
        _ => "其他工作"
    };

    private static bool Matches(TransferJob job, string term) =>
        job.Label.Contains(term, StringComparison.OrdinalIgnoreCase) || job.FriendName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
        job.Created.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture).Contains(term, StringComparison.Ordinal) ||
        job.Created.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture).Contains(term, StringComparison.Ordinal);
}
