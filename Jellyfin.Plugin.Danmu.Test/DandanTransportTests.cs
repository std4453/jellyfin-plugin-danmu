using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Danmu.Configuration;
using Jellyfin.Plugin.Danmu.Scrapers.Dandan;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Danmu.Test;

[TestClass]
public class DandanTransportTests
{
    private sealed class Transport : HttpMessageHandler
    {
        public readonly List<(Uri Url, Dictionary<string, string> Headers)> Calls = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls.Add((request.RequestUri!, request.Headers.ToDictionary(x => x.Key, x => string.Join(",", x.Value))));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"count\":0,\"comments\":[]}", Encoding.UTF8, "application/json")
            });
        }
    }

    [TestMethod]
    public async Task OfficialRequestSignsPathWithoutQuery()
    {
        var options = new DandanOption { AppId = "test-app", AppSecret = "test-key", DownloadIntervalSeconds = 0 };
        var transport = new Transport();
        using var api = new DandanApi(LoggerFactory.Create(_ => { }), () => options, transport);
        await api.GetCommentsAsync(123, CancellationToken.None);
        var call = transport.Calls.Single();
        Assert.AreEqual("api.dandanplay.net", call.Url.Host);
        Assert.AreEqual("test-app", call.Headers["X-AppId"]);
        var input = "test-app" + call.Headers["X-Timestamp"] + "/api/v2/comment/123test-key";
        Assert.AreEqual(Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(input))), call.Headers["X-Signature"]);
        Assert.IsFalse(call.Headers.Values.Any(v => v.Contains("test-key")));
    }

    [TestMethod]
    public async Task PublicProxyDoesNotReceiveCredentialsAndZeroIntervalDoesNotDelay()
    {
        var options = new DandanOption { UsePublicProxy = true, DownloadIntervalSeconds = 0 };
        var transport = new Transport();
        using var api = new DandanApi(LoggerFactory.Create(_ => { }), () => options, transport);
        var clock = Stopwatch.StartNew();
        await api.GetCommentsAsync(123, CancellationToken.None);
        await api.GetCommentsAsync(124, CancellationToken.None);
        Assert.IsTrue(clock.Elapsed < TimeSpan.FromSeconds(5));
        Assert.AreEqual(2, transport.Calls.Count);
        foreach (var call in transport.Calls)
        {
            Assert.AreEqual("api.danmaku.weeblify.app", call.Url.Host);
            Assert.IsTrue(Uri.UnescapeDataString(call.Url.Query).Contains("path=/v2/comment/"));
            Assert.IsFalse(call.Headers.Keys.Any(k => k.StartsWith("X-App") || k == "X-Signature" || k == "X-Timestamp"));
        }
    }

    [TestMethod]
    public async Task CancellationDuringCooldownDoesNotSendRequest()
    {
        var options = new DandanOption { UsePublicProxy = true, DownloadIntervalSeconds = 30 };
        var transport = new Transport();
        using var api = new DandanApi(LoggerFactory.Create(_ => { }), () => options, transport);
        await api.GetCommentsAsync(123, CancellationToken.None);
        using var cancellation = new CancellationTokenSource(30);
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => api.GetCommentsAsync(124, cancellation.Token));
        Assert.AreEqual(1, transport.Calls.Count);
    }

    [TestMethod]
    public void InvalidConfigurationIsRejected()
    {
        Assert.ThrowsException<ArgumentException>(() => new DandanOption { UsePublicProxy = true, AppSecret = "secret" }.Validate());
        Assert.ThrowsException<ArgumentException>(() => new DandanOption { DownloadIntervalSeconds = -1 }.Validate());
        Assert.ThrowsException<ArgumentException>(() => new DandanOption { ProxyUrl = "ftp://proxy.invalid" }.Validate());
    }
}
