using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Phono.Data;
using Phono.Models;

namespace Phono.Services;

public class TorrentMonitorService : BackgroundService
{
    private static readonly string[] SupportedExtensions = { ".mp3", ".wav", ".flac" };
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TorrentMonitorService> _logger;
    private readonly TorrentSettings _settings;

    public TorrentMonitorService(IServiceScopeFactory scopeFactory, ILogger<TorrentMonitorService> logger, IOptions<TorrentSettings> options)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _settings = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessJobsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while processing torrent jobs");
            }

            await Task.Delay(TimeSpan.FromSeconds(_settings.PollSeconds), stoppingToken);
        }
    }

    private async Task ProcessJobsAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var qbit = scope.ServiceProvider.GetRequiredService<QBitClientService>();
        var compression = scope.ServiceProvider.GetRequiredService<AudioCompressionService>();
        var sync = scope.ServiceProvider.GetRequiredService<MetadataSyncService>();

        var activeJobs = await db.TorrentJobs
            .Where(j => j.Status != TorrentJobStatus.Completed && j.Status != TorrentJobStatus.Failed)
            .OrderBy(j => j.CreatedAt)
            .ToListAsync(stoppingToken);

        if (activeJobs.Count == 0)
            return;

        var torrents = await qbit.GetTorrentsAsync(cancellationToken: stoppingToken);

        foreach (var job in activeJobs)
        {
            try
            {
                await ProcessJobAsync(job, torrents, db, qbit, compression, sync, stoppingToken);
            }
            catch (Exception ex)
            {
                job.Status = TorrentJobStatus.Failed;
                job.ErrorMessage = ex.Message;
                job.UpdatedAt = DateTime.UtcNow;
                _logger.LogError(ex, "Failed processing torrent job {JobId}", job.Id);
            }

            await db.SaveChangesAsync(stoppingToken);
        }
    }

    private async Task ProcessJobAsync(
        TorrentJob job,
        List<QBitTorrentInfo> torrents,
        ApplicationDbContext db,
        QBitClientService qbit,
        AudioCompressionService compression,
        MetadataSyncService sync,
        CancellationToken stoppingToken)
    {
        var torrent = ResolveTorrent(job, torrents);
        if (torrent == null)
        {
            job.UpdatedAt = DateTime.UtcNow;
            return;
        }

        var previousProgress = job.Progress;
        job.TorrentHash ??= torrent.Hash;
        job.Progress = Math.Round(torrent.Progress * 100, 2);
        job.DownloadSpeed = torrent.DownloadSpeed;
        job.Seeds = torrent.Seeds;
        job.UpdatedAt = DateTime.UtcNow;
        if (job.Progress > previousProgress)
        {
            job.LastProgressAt = DateTime.UtcNow;
        }

        var status = MapStatus(torrent.State);
        job.Status = status;
        if (status == TorrentJobStatus.Failed && string.IsNullOrWhiteSpace(job.ErrorMessage))
        {
            job.ErrorMessage = $"Torrent error: {torrent.State}";
        }
        if (status == TorrentJobStatus.Stalled && job.Seeds == 0)
        {
            job.ErrorMessage ??= "Waiting for active seeders.";
        }
        else if (job.ErrorMessage == "Waiting for active seeders.")
        {
            job.ErrorMessage = null;
        }

        if (status == TorrentJobStatus.Stalled && job.Seeds == 0)
        {
            var lastProgressAt = job.LastProgressAt ?? job.CreatedAt;
            if (DateTime.UtcNow - lastProgressAt > TimeSpan.FromMinutes(_settings.StallMinutes))
            {
                job.Status = TorrentJobStatus.Failed;
                job.ErrorMessage = "Download stalled: No active seeders found.";
            }
        }

        if (torrent.Progress >= 1.0)
        {
            job.Status = TorrentJobStatus.Processing;
            await db.SaveChangesAsync(stoppingToken);

            var rawPath = string.IsNullOrWhiteSpace(torrent.ContentPath)
                ? Path.Combine(torrent.SavePath, torrent.Name)
                : torrent.ContentPath;
            var contentPath = qbit.MapToIncomingPath(rawPath);
            if (string.IsNullOrWhiteSpace(contentPath))
            {
                job.Status = TorrentJobStatus.Failed;
                job.ErrorMessage = "Download completed but content path was unavailable.";
                return;
            }

            var audioFiles = GetAudioFiles(contentPath).ToList();
            if (audioFiles.Count == 0)
            {
                job.Status = TorrentJobStatus.Failed;
                job.ErrorMessage = "No supported audio files found in torrent.";
                return;
            }

            foreach (var filePath in audioFiles)
            {
                var targetName = EnsureUniqueFileName(Path.GetFileName(filePath));
                var destinationPath = Path.Combine(Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads"), targetName);

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                File.Move(filePath, destinationPath);

                var compressionResult = await compression.CompressIfNeededAsync(targetName);
                await sync.ImportFileAsync(compressionResult.CompressedFileName);
            }

            await qbit.DeleteTorrentAsync(torrent.Hash, deleteFiles: true, cancellationToken: stoppingToken);
            job.Status = TorrentJobStatus.Completed;
            job.CompletedAt = DateTime.UtcNow;
            job.ErrorMessage = null;
        }
    }

    private static TorrentJobStatus MapStatus(string state)
    {
        if (string.IsNullOrWhiteSpace(state))
            return TorrentJobStatus.Queued;

        return state switch
        {
            "error" => TorrentJobStatus.Failed,
            "missingFiles" => TorrentJobStatus.Failed,
            "stalledDL" => TorrentJobStatus.Stalled,
            "pausedDL" => TorrentJobStatus.Stalled,
            "queuedDL" => TorrentJobStatus.Queued,
            "checkingDL" => TorrentJobStatus.Downloading,
            "downloading" => TorrentJobStatus.Downloading,
            "forcedDL" => TorrentJobStatus.Downloading,
            "uploading" => TorrentJobStatus.Processing,
            "stalledUP" => TorrentJobStatus.Processing,
            "queuedUP" => TorrentJobStatus.Processing,
            _ => TorrentJobStatus.Downloading
        };
    }

    private QBitTorrentInfo? ResolveTorrent(TorrentJob job, List<QBitTorrentInfo> torrents)
    {
        if (!string.IsNullOrWhiteSpace(job.TorrentHash))
        {
            return torrents.FirstOrDefault(t => t.Hash.Equals(job.TorrentHash, StringComparison.OrdinalIgnoreCase));
        }

        if (string.IsNullOrWhiteSpace(job.Title))
            return null;

        return torrents.FirstOrDefault(t =>
            t.Name.Equals(job.Title, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(t.Category, _settings.Category, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> GetAudioFiles(string rootPath)
    {
        if (File.Exists(rootPath))
        {
            var ext = Path.GetExtension(rootPath).ToLowerInvariant();
            if (SupportedExtensions.Contains(ext))
                return new[] { rootPath };
            return Array.Empty<string>();
        }

        if (!Directory.Exists(rootPath))
            return Array.Empty<string>();

        return Directory.EnumerateFiles(rootPath, "*.*", SearchOption.AllDirectories)
            .Where(file => SupportedExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()));
    }

    private static string EnsureUniqueFileName(string fileName)
    {
        var uploadsPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads");
        var candidate = fileName;
        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var counter = 1;

        while (File.Exists(Path.Combine(uploadsPath, candidate)))
        {
            candidate = $"{nameWithoutExt}_{counter}{extension}";
            counter++;
        }

        return candidate;
    }
}
