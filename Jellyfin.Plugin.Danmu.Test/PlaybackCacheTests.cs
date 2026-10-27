using Jellyfin.Plugin.Danmu.Controllers;
using Jellyfin.Plugin.Danmu.Scrapers;
using Jellyfin.Plugin.Danmu.Scrapers.Entity;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using Moq;

namespace Jellyfin.Plugin.Danmu.Test;

[TestClass]
public class PlaybackCacheTests
{
    [TestMethod]
    public async Task ConcurrentPlaybackUsesOneDownloadAndThenTheFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "danmu-playback-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var logger = LoggerFactory.Create(_ => { });
            var scraper = new Mock<AbstractScraper>(logger.CreateLogger("test"));
            scraper.SetupGet(x => x.ProviderId).Returns("DandanID");
            scraper.Setup(x => x.DownloadDanmuForApi("123")).Returns(async () =>
            {
                await Task.Delay(30);
                return new ScraperDanmaku();
            });
            var helper = new LibraryManagerEventsHelper(new Mock<IItemRepository>().Object,
                new Mock<ILibraryManager>().Object, logger, new Core.FileSystem(), new ScraperManager(logger));
            var item = new Movie { Id = Guid.NewGuid(), Path = Path.Combine(directory, "episode.mkv") };
            var results = await Task.WhenAll(helper.DownloadForPlayback(scraper.Object, item, "123"), helper.DownloadForPlayback(scraper.Object, item, "123"));
            CollectionAssert.AreEqual(results[0], results[1]);
            scraper.Verify(x => x.DownloadDanmuForApi("123"), Times.Once);
            Assert.IsTrue(File.Exists(Path.Combine(directory, "episode.xml")));
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public void ApiRequiresAuthentication()
    {
        Assert.IsTrue(typeof(DanmuController).IsDefined(typeof(AuthorizeAttribute), true));
        Assert.IsFalse(typeof(DanmuController).IsDefined(typeof(AllowAnonymousAttribute), true));
        Assert.IsTrue(typeof(ApiController).IsDefined(typeof(AuthorizeAttribute), true));
    }
}
