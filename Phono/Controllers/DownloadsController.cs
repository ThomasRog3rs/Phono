using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Phono.Data;

namespace Phono.Controllers;

[Authorize]
public class DownloadsController : Controller
{
    private readonly ApplicationDbContext _dbContext;

    public DownloadsController(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var jobs = await _dbContext.TorrentJobs
            .OrderByDescending(j => j.CreatedAt)
            .ToListAsync();

        return View(jobs);
    }
}
