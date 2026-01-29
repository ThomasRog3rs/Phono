using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Phono.Services;

public class TorrentSettings
{
    public string BaseUrl { get; set; } = "http://qbittorrent:8080";
    public string Username { get; set; } = "admin";
    public string Password { get; set; } = "adminadmin";
    public string Category { get; set; } = "phono";
    public string QBitDownloadsPath { get; set; } = "/downloads";
    public string IncomingPath { get; set; } = "/app/incoming";
    public int PollSeconds { get; set; } = 10;
    public int StallMinutes { get; set; } = 30;
}

public class QBitClientService
{
    private readonly HttpClient _httpClient;
    private readonly TorrentSettings _settings;
    private readonly ILogger<QBitClientService> _logger;
    private bool _isAuthenticated;

    public QBitClientService(HttpClient httpClient, IOptions<TorrentSettings> options, ILogger<QBitClientService> logger)
    {
        _httpClient = httpClient;
        _settings = options.Value;
        _logger = logger;
    }

    public string MapToIncomingPath(string qbitPath)
    {
        if (string.IsNullOrWhiteSpace(qbitPath))
            return qbitPath;

        if (qbitPath.StartsWith(_settings.QBitDownloadsPath, StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(_settings.IncomingPath, qbitPath.Substring(_settings.QBitDownloadsPath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        return qbitPath;
    }

    public async Task<string?> AddMagnetAsync(string magnetLink, string? title = null, CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        using var content = new FormUrlEncodedContent(new Dictionary<string, string?>
        {
            ["urls"] = magnetLink,
            ["category"] = _settings.Category,
            ["savepath"] = _settings.QBitDownloadsPath
        });

        var response = await _httpClient.PostAsync("/api/v2/torrents/add", content, cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!string.Equals(body.Trim(), "Ok.", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("qBittorrent add magnet response: {Body}", body);
        }

        // qBittorrent doesn't return hash from add; caller should resolve hash via info list
        return null;
    }

    public async Task<List<QBitTorrentInfo>> GetTorrentsAsync(string? hashFilter = null, CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);
        var query = string.IsNullOrWhiteSpace(hashFilter) ? string.Empty : $"?hashes={WebUtility.UrlEncode(hashFilter)}";
        var response = await _httpClient.GetAsync($"/api/v2/torrents/info{query}", cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<List<QBitTorrentInfo>>(json) ?? new List<QBitTorrentInfo>();
    }

    public async Task<List<QBitTorrentFile>> GetTorrentFilesAsync(string hash, CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);
        var response = await _httpClient.GetAsync($"/api/v2/torrents/files?hash={WebUtility.UrlEncode(hash)}", cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<List<QBitTorrentFile>>(json) ?? new List<QBitTorrentFile>();
    }

    public async Task DeleteTorrentAsync(string hash, bool deleteFiles, CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string?>
        {
            ["hashes"] = hash,
            ["deleteFiles"] = deleteFiles ? "true" : "false"
        });

        var response = await _httpClient.PostAsync("/api/v2/torrents/delete", content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task EnsureAuthenticatedAsync(CancellationToken cancellationToken)
    {
        if (_isAuthenticated)
            return;

        using var content = new FormUrlEncodedContent(new Dictionary<string, string?>
        {
            ["username"] = _settings.Username,
            ["password"] = _settings.Password
        });

        var response = await _httpClient.PostAsync("/api/v2/auth/login", content, cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!string.Equals(body.Trim(), "Ok.", StringComparison.OrdinalIgnoreCase))
        {
            var message = $"qBittorrent auth failed: {body}. " +
                          "Set the WebUI username/password in qBittorrent and ensure " +
                          "your Torrent settings match (Username/Password).";
            throw new InvalidOperationException(message);
        }

        _isAuthenticated = true;
    }
}

public class QBitTorrentInfo
{
    [JsonPropertyName("hash")]
    public string Hash { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("progress")]
    public double Progress { get; set; }

    [JsonPropertyName("dlspeed")]
    public long DownloadSpeed { get; set; }

    [JsonPropertyName("num_seeds")]
    public int Seeds { get; set; }

    [JsonPropertyName("content_path")]
    public string ContentPath { get; set; } = string.Empty;

    [JsonPropertyName("save_path")]
    public string SavePath { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;
}

public class QBitTorrentFile
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("progress")]
    public double Progress { get; set; }
}
