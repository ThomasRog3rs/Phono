namespace Phono.Models;

public enum TorrentJobStatus
{
    Queued = 0,
    Downloading = 1,
    Processing = 2,
    Completed = 3,
    Failed = 4,
    Stalled = 5
}

public class TorrentJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string MagnetLink { get; set; } = string.Empty;
    public string? TorrentHash { get; set; }
    public string? Title { get; set; }
    public TorrentJobStatus Status { get; set; } = TorrentJobStatus.Queued;
    public double Progress { get; set; }
    public long DownloadSpeed { get; set; }
    public int Seeds { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime? LastProgressAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}
