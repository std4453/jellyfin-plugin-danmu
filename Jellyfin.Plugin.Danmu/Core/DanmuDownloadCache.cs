using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.Danmu.Core;

/// <summary>与自动下载共用的持久冷却记录，内容未变化时也记录成功获取时间。</summary>
public sealed class DanmuDownloadCache
{
    private readonly string _directory;
    public DanmuDownloadCache(string directory) => _directory = directory;

    private string ReceiptPath(string path) => Path.Combine(_directory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))) + ".json");

    public bool IsFresh(string path, string source, string id, int hours, DateTime now)
    {
        if (hours <= 0 || !File.Exists(path)) return false;
        var file = new FileInfo(path);
        var downloaded = file.LastWriteTimeUtc;
        var receipt = ReceiptPath(path);
        if (File.Exists(receipt))
        {
            try
            {
                var record = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(receipt));
                if (record == null || record.Source != source || record.Id != id ||
                    record.Length != file.Length || record.WriteTicks != file.LastWriteTimeUtc.Ticks) return false;
                downloaded = record.Downloaded;
            }
            catch (IOException) { return false; }
            catch (JsonException) { return false; }
        }
        return downloaded <= now && now - downloaded < TimeSpan.FromHours(hours);
    }

    public void Record(string path, string source, string id, DateTime now)
    {
        var file = new FileInfo(path);
        Directory.CreateDirectory(_directory);
        var destination = ReceiptPath(path);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Receipt(source, id, file.Length, file.LastWriteTimeUtc.Ticks, now)));
            File.Move(temporary, destination, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public sealed record Receipt(string Source, string Id, long Length, long WriteTicks, DateTime Downloaded);
}
