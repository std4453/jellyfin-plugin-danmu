using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Web;
using Microsoft.Extensions.Caching.Memory;
using Jellyfin.Plugin.Danmu.Scrapers.Dandan.Entity;
using Jellyfin.Plugin.Danmu.Configuration;
using Jellyfin.Plugin.Danmu.Core.Extensions;
using MediaBrowser.Controller.Entities;
using System.IO;
using RateLimiter;
using ComposableAsync;
using System.Net;

namespace Jellyfin.Plugin.Danmu.Scrapers.Dandan;

public class DandanApi : AbstractApi
{
    private TimeLimiter _limitRequestConstraint = TimeLimiter.GetFromMaxCountByInterval(1, TimeSpan.FromMilliseconds(1000));
    private readonly SemaphoreSlim _downloadGate = new(1, 1);
    private DateTimeOffset _lastDownload = DateTimeOffset.MinValue;
    private readonly Func<DandanOption>? _options;
    private readonly bool _customTransport;

    /// <summary>
    /// 429封禁到期时间，此时间之前所有请求直接返回失败。
    /// </summary>
    private static DateTime _blockedUntil = DateTime.MinValue;

    public DandanOption Config
    {
        get
        {
            return _options?.Invoke() ?? Plugin.Instance?.Configuration.Dandan ?? new DandanOption();
        }
    }

    public string ApiID
    {
        get
        {
            if (!string.IsNullOrEmpty(Config.AppId)) return Config.AppId;
            var apiId = Environment.GetEnvironmentVariable("DANDAN_API_ID");
            if (!string.IsNullOrEmpty(apiId))
            {
                return apiId;
            }

            return string.Empty;
        }
    }

    public string ApiSecret
    {
        get
        {
            if (!string.IsNullOrEmpty(Config.AppSecret)) return Config.AppSecret;
            var apiSecret = Environment.GetEnvironmentVariable("DANDAN_API_SECRET");
            if (!string.IsNullOrEmpty(apiSecret))
            {
                return apiSecret;
            }

            return string.Empty;
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DandanApi"/> class.
    /// </summary>
    /// <param name="loggerFactory">The <see cref="ILoggerFactory"/>.</param>
    public DandanApi(ILoggerFactory loggerFactory)
        : this(loggerFactory, null, null)
    {
    }

    public DandanApi(ILoggerFactory loggerFactory, Func<DandanOption>? options, HttpMessageHandler? transport)
        : base(loggerFactory.CreateLogger<DandanApi>())
    {
        _options = options;
        _customTransport = transport != null;
        if (transport != null)
        {
            httpClient.Dispose();
            httpClient = new HttpClient(transport);
        }
        httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        httpClient.Timeout = TimeSpan.FromSeconds(10);
    }


    public async Task<List<Anime>> SearchAsync(string keyword, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(keyword))
        {
            return new List<Anime>();
        }

        var cacheKey = $"search_{keyword}";
        var expiredOption = new MemoryCacheEntryOptions() { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30) };
        if (_memoryCache.TryGetValue<List<Anime>>(cacheKey, out var searchResult))
        {
            return searchResult;
        }

        this.LimitRequestFrequently();

        keyword = HttpUtility.UrlEncode(keyword);
        var url = $"https://api.dandanplay.net/api/v2/search/anime?keyword={keyword}";
        using var response = await this.Request(url, cancellationToken).ConfigureAwait(false);
        var result = await response.Content.ReadFromJsonAsync<SearchResult>(_jsonOptions, cancellationToken).ConfigureAwait(false);
        if (result != null && result.Success)
        {
            _memoryCache.Set<List<Anime>>(cacheKey, result.Animes, expiredOption);
            return result.Animes;
        }

        _memoryCache.Set<List<Anime>>(cacheKey, new List<Anime>(), expiredOption);
        return new List<Anime>();
    }

    public async Task<List<MatchResultV2>> MatchAsync(BaseItem item, CancellationToken cancellationToken)
    {
        if (item == null)
        {
            return new List<MatchResultV2>();
        }

        var cacheKey = $"match_{item.Id}";
        var expiredOption = new MemoryCacheEntryOptions() { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) };
        if (_memoryCache.TryGetValue<List<MatchResultV2>>(cacheKey, out var matches))
        {
            return matches;
        }

        var matchRequest = new Dictionary<string, object>
        {
            ["fileName"] = Path.GetFileNameWithoutExtension(item.Path),
            ["fileHash"] = "00000000000000000000000000000000",
            ["fileSize"] = item.Size ?? 0,
            ["videoDuration"] = (item.RunTimeTicks ?? 0) / 10000000,
            ["matchMode"] = "fileNameOnly",
        };
        if (this.Config.MatchByFileHash)
        {
            matchRequest["fileHash"] = await this.ComputeFileHashAsync(item.Path).ConfigureAwait(false);
            matchRequest["matchMode"] = "hashAndFileName";
        }

        var url = "https://api.dandanplay.net/api/v2/match";
        using var response = await this.Request(url, HttpMethod.Post, matchRequest, cancellationToken).ConfigureAwait(false);
        var result = await response.Content.ReadFromJsonAsync<MatchResponseV2>(_jsonOptions, cancellationToken).ConfigureAwait(false);
        if (result != null && result.Success && result.Matches != null)
        {
            _memoryCache.Set<List<MatchResultV2>>(cacheKey, result.Matches, expiredOption);
            return result.Matches;
        }

        _memoryCache.Set<List<MatchResultV2>>(cacheKey, new List<MatchResultV2>(), expiredOption);
        return new List<MatchResultV2>();
    }

    private async Task<string> ComputeFileHashAsync(string filePath)
    {
        try
        {
            using (var stream = File.OpenRead(filePath))
            {
                // 读取前16MB
                var buffer = new byte[16 * 1024 * 1024];
                var bytesRead = await stream.ReadAsync(buffer).ConfigureAwait(false);

                if (bytesRead > 0)
                {
                    // 如果文件小于16MB，调整buffer大小
                    if (bytesRead < buffer.Length)
                    {
                        Array.Resize(ref buffer, bytesRead);
                    }

                    var hash = MD5.HashData(buffer);
                    return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "计算文件哈希值时出错: {Path}", filePath);
        }

        return string.Empty;
    }

    public async Task<Anime?> GetAnimeAsync(long animeId, CancellationToken cancellationToken)
    {
        if (animeId <= 0)
        {
            return null;
        }

        var cacheKey = $"anime_{animeId}";
        var expiredOption = new MemoryCacheEntryOptions() { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30) };
        if (_memoryCache.TryGetValue<Anime?>(cacheKey, out var anime))
        {
            return anime;
        }

        var url = $"https://api.dandanplay.net/api/v2/bangumi/{animeId}";
        using var response = await this.Request(url, cancellationToken).ConfigureAwait(false);

        var result = await response.Content.ReadFromJsonAsync<AnimeResult>(_jsonOptions, cancellationToken).ConfigureAwait(false);
        if (result != null && result.Success && result.Bangumi != null)
        {
            anime = result.Bangumi;
            if (anime.Episodes != null)
            {
                // 过滤掉特典剧集，episodeNumber为S1/S2.。。
                anime.Episodes = anime.Episodes.Where(x => x.EpisodeNumber.ToInt() > 0).ToList();

                // 本接口与 search 返回不完全一致，补全缺失字段
                anime.EpisodeCount = anime.Episodes.Count;
                anime.StartDate = anime.Episodes.FirstOrDefault()?.AirDate;
            }
            _memoryCache.Set<Anime?>(cacheKey, anime, expiredOption);
            return anime;
        }

        _memoryCache.Set<Anime?>(cacheKey, null, expiredOption);
        return null;
    }

    public async Task<List<Comment>> GetCommentsAsync(long epId, CancellationToken cancellationToken)
    {
        if (epId <= 0)
        {
            throw new ArgumentNullException(nameof(epId));
        }

        await _downloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Config.Validate();
            var remaining = TimeSpan.FromSeconds(Config.DownloadIntervalSeconds) - (DateTimeOffset.UtcNow - _lastDownload);
            if (remaining > TimeSpan.Zero)
                await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
            _lastDownload = DateTimeOffset.UtcNow;
        }
        finally
        {
            _downloadGate.Release();
        }

        var withRelated = this.Config.WithRelatedDanmu ? "true" : "false";
        var chConvert = this.Config.ChConvert;
        var url = $"https://api.dandanplay.net/api/v2/comment/{epId}?withRelated={withRelated}&chConvert={chConvert}";
        using var response = await this.Request(url, cancellationToken).ConfigureAwait(false);
        var result = await response.Content.ReadFromJsonAsync<CommentResult>(_jsonOptions, cancellationToken).ConfigureAwait(false);
        if (result != null)
        {
            return result.Comments;
        }

        throw new Exception($"Request fail. epId={epId}");
    }

    protected async Task LimitRequestFrequently(double intervalMilliseconds = 1000)
    {
        await this._limitRequestConstraint;
    }

    protected async Task<HttpResponseMessage> Request(string url, CancellationToken cancellationToken)
    {
        return await Request(url, HttpMethod.Get, null, cancellationToken).ConfigureAwait(false);
    }

    protected async Task<HttpResponseMessage> Request(string url, HttpMethod method, object? content = null, CancellationToken cancellationToken = default)
    {
        // 检查是否在429封禁期
        CheckBlocked();

        var options = Config;
        options.Validate();
        var originalUri = new Uri(url);
        if (options.UsePublicProxy)
            url = "https://api.danmaku.weeblify.app/ddp/v1?path=" + Uri.EscapeDataString(originalUri.PathAndQuery.Substring(4));

        // 每次按当前配置选择代理，不把应用凭据写入 HttpClient 默认头或公开代理请求。
        using var proxyClient = !_customTransport && !string.IsNullOrEmpty(options.ProxyUrl)
            ? new HttpClient(new HttpClientHandler { Proxy = new WebProxy(options.ProxyUrl), UseProxy = true }) { Timeout = TimeSpan.FromSeconds(10) }
            : null;
        var client = proxyClient ?? httpClient;

        HttpResponseMessage response;
        using (var request = new HttpRequestMessage(method, url)) {
            if (!options.UsePublicProxy)
            {
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                request.Headers.Add("X-AppId", ApiID);
                request.Headers.Add("X-Signature", GenerateSignature(originalUri.AbsoluteUri, timestamp));
                request.Headers.Add("X-Timestamp", timestamp.ToString());
            }
            if (method == HttpMethod.Post && content != null)
            {
                request.Content = JsonContent.Create(content, null, _jsonOptions);
            }
            _logger.LogInformation("Dandanplay request {Method} {Path} via {Channel}", method, originalUri.AbsolutePath, options.UsePublicProxy ? "publicProxy" : "official");
            response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            // 检测429 Too Many Requests，封禁1小时
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                SetBlocked();
            }

            response.EnsureSuccessStatusCode();
        }

        return response;
    }

    /// <summary>
    /// 检查是否在429封禁期，如果是则抛出异常。
    /// </summary>
    private void CheckBlocked()
    {
        var remaining = _blockedUntil - DateTime.Now;
        if (remaining > TimeSpan.Zero)
        {
            var minutes = (int)remaining.TotalMinutes;
            throw new FrequentlyRequestException(
                new Exception($"弹弹play API触发429频率限制，已被封禁约{minutes}分钟，请稍候再试。"));
        }
    }

    /// <summary>
    /// 设置封禁到期时间为当前时间+1小时。
    /// </summary>
    private static void SetBlocked()
    {
        _blockedUntil = DateTime.Now.AddHours(1);
    }

    protected string GenerateSignature(string url, long timestamp)
    {
        if (string.IsNullOrEmpty(ApiID) || string.IsNullOrEmpty(ApiSecret))
        {
            throw new Exception("弹弹接口缺少API_ID和API_SECRET");
        }
        var uri = new Uri(url);
        var path = uri.AbsolutePath;
        var str = $"{ApiID}{timestamp}{path}{ApiSecret}";
        using (var sha256 = SHA256.Create())
        {
            var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(str));
            return Convert.ToBase64String(hashBytes);
        }
    }

}
