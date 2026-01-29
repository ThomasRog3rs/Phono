using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Phono.Services;
using Phono.ViewModels;
using Phono.Data;
using Phono.Models;

namespace Phono.Controllers;

[Authorize]
public class SearchController : Controller
{
    private readonly MagnetApiService _magnetApiService;
    private readonly QBitClientService _qBitClientService;
    private readonly ApplicationDbContext _dbContext;
    private readonly TorrentSettings _torrentSettings;

    public SearchController(
        MagnetApiService magnetApiService,
        QBitClientService qBitClientService,
        ApplicationDbContext dbContext,
        IOptions<TorrentSettings> torrentOptions)
    {
        _magnetApiService = magnetApiService;
        _qBitClientService = qBitClientService;
        _dbContext = dbContext;
        _torrentSettings = torrentOptions.Value;
    }

    [HttpGet]
    public async Task<IActionResult> Index(string? query)
    {
        var vm = new MagnetSearchViewModel
        {
            Query = query
        };

        if (!string.IsNullOrWhiteSpace(query))
        {
            vm.Result = await _magnetApiService.SearchPirateBayAudioAsync(query);
        }

        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Download(string? magnet, string? title)
    {
        if (string.IsNullOrWhiteSpace(magnet))
        {
            TempData["ErrorMessage"] = "Magnet link was missing.";
            return RedirectToAction("Index");
        }

        var normalizedTitle = title?.Trim();
        var job = new TorrentJob
        {
            MagnetLink = magnet,
            Title = normalizedTitle,
            Status = TorrentJobStatus.Queued,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _dbContext.TorrentJobs.Add(job);
        await _dbContext.SaveChangesAsync();

        try
        {
            await _qBitClientService.AddMagnetAsync(magnet, title);

            if (!string.IsNullOrWhiteSpace(normalizedTitle))
            {
                var torrents = await _qBitClientService.GetTorrentsAsync();
                var match = torrents.FirstOrDefault(t =>
                    t.Name.Equals(normalizedTitle, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(t.Category, _torrentSettings.Category, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    job.TorrentHash = match.Hash;
                    job.Progress = Math.Round(match.Progress * 100, 2);
                    job.DownloadSpeed = match.DownloadSpeed;
                    job.Seeds = match.Seeds;
                    if (job.Progress > 0)
                    {
                        job.LastProgressAt = DateTime.UtcNow;
                    }
                    job.Status = match.Progress >= 1.0 ? TorrentJobStatus.Processing : TorrentJobStatus.Downloading;
                    job.UpdatedAt = DateTime.UtcNow;
                }
            }

            await _dbContext.SaveChangesAsync();
            TempData["SuccessMessage"] = "Download queued. Track status in Downloads.";
        }
        catch (Exception ex)
        {
            job.Status = TorrentJobStatus.Failed;
            job.ErrorMessage = ex.Message;
            job.UpdatedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();
            TempData["ErrorMessage"] = $"Failed to start download: {ex.Message}";
        }

        return RedirectToAction("Index", "Downloads");
    }
}
