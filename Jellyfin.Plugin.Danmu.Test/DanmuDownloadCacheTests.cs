using Jellyfin.Plugin.Danmu.Core;

namespace Jellyfin.Plugin.Danmu.Test;

[TestClass]
public class DanmuDownloadCacheTests
{
    [TestMethod]
    public void PersistedSuccessfulDownloadExtendsCooldownWithoutRewritingXml()
    {
        var directory = Path.Combine(Path.GetTempPath(), "danmu-cache-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "episode.xml");
            File.WriteAllText(path, "<i/>");
            var now = DateTime.UtcNow;
            File.SetLastWriteTimeUtc(path, now.AddHours(-73));
            var writeTime = File.GetLastWriteTimeUtc(path);
            var cache = new DanmuDownloadCache(Path.Combine(directory, "receipts"));
            Assert.IsFalse(cache.IsFresh(path, "DandanID", "123", 72, now));
            cache.Record(path, "DandanID", "123", now);
            Assert.AreEqual(writeTime, File.GetLastWriteTimeUtc(path));
            var restarted = new DanmuDownloadCache(Path.Combine(directory, "receipts"));
            Assert.IsTrue(restarted.IsFresh(path, "DandanID", "123", 72, now.AddHours(71)));
            Assert.IsFalse(restarted.IsFresh(path, "DandanID", "123", 72, now.AddHours(72)));
            Assert.IsFalse(restarted.IsFresh(path, "DandanID", "124", 72, now));
            Assert.IsFalse(restarted.IsFresh(path, "DandanID", "123", 0, now));
            File.Delete(path);
            Assert.IsFalse(restarted.IsFresh(path, "DandanID", "123", 72, now));
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public void ExistingXmlUsesDiskCooldownAndRejectsFutureTimestamps()
    {
        var directory = Path.Combine(Path.GetTempPath(), "danmu-cache-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "episode.xml");
            File.WriteAllText(path, "<i><chatid>123</chatid><chatserver>api.dandanplay.net</chatserver></i>");
            var cache = new DanmuDownloadCache(Path.Combine(directory, "receipts"));
            var now = DateTime.UtcNow;
            File.SetLastWriteTimeUtc(path, now.AddHours(-1));
            Assert.IsTrue(cache.IsFresh(path, "DandanID", "123", 72, now));
            Assert.IsFalse(cache.IsFresh(path, "DandanID", "124", 72, now));
            File.SetLastWriteTimeUtc(path, now.AddHours(1));
            Assert.IsFalse(cache.IsFresh(path, "DandanID", "123", 72, now));
        }
        finally { Directory.Delete(directory, true); }
    }
}
