using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace RaidDcBot.Services;

public sealed class JobIconCacheService
{
    private readonly HttpClient _http;
    private readonly string _cacheDir;
    private readonly ILogger<JobIconCacheService> _logger;
    private readonly Dictionary<string, string> _jobIconUrls;

    public JobIconCacheService(ILogger<JobIconCacheService> logger)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _cacheDir = "/src/data/job_icons";
        _logger = logger;
        Directory.CreateDirectory(_cacheDir);
        _jobIconUrls = BuildJobIconUrlMapping();
    }

    private static Dictionary<string, string> BuildJobIconUrlMapping()
    {
        var mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Tanks
            ["\u30ca\u30a4\u30c8"] = "https://xivapi.com/cj/1/paladin.png",
            ["\u6226\u58eb"] = "https://xivapi.com/cj/1/warrior.png",
            ["\u6697\u9ed2\u9a0e\u58eb"] = "https://xivapi.com/cj/1/darkknight.png",
            ["\u30ac\u30f3\u30d6\u30ec\u30a4\u30ab\u30fc"] = "https://xivapi.com/cj/1/gunbreaker.png",

            // Healers
            ["\u767d\u9b54\u9053\u58eb"] = "https://xivapi.com/cj/1/whitemage.png",
            ["\u5b66\u8005"] = "https://xivapi.com/cj/1/scholar.png",
            ["\u5360\u661f\u8853\u5e2b"] = "https://xivapi.com/cj/1/astrologian.png",
            ["\u8ce2\u8005"] = "https://raw.githubusercontent.com/xivapi/classjob-icons/master/icons/sage.png",

            // Melee DPS
            ["\u30e2\u30f3\u30af"] = "https://xivapi.com/cj/1/monk.png",
            ["\u7adc\u9a0e\u58eb"] = "https://xivapi.com/cj/1/dragoon.png",
            ["\u5fcd\u8005"] = "https://xivapi.com/cj/1/ninja.png",
            ["\u4f8d"] = "https://xivapi.com/cj/1/samurai.png",
            ["\u30ea\u30fc\u30d1\u30fc"] = "https://raw.githubusercontent.com/xivapi/classjob-icons/master/icons/reaper.png",
            ["\u30f4\u30a1\u30a4\u30d1\u30fc"] = "https://xivapi.com/cj/1/viper.png",

            // Ranged DPS
            ["\u541f\u904a\u8a69\u4eba"] = "https://xivapi.com/cj/1/bard.png",
            ["\u6a5f\u5de5\u58eb"] = "https://xivapi.com/cj/1/machinist.png",
            ["\u8e0a\u308a\u5b50"] = "https://xivapi.com/cj/1/dancer.png",

            // Magic DPS
            ["\u9ed2\u9b54\u9053\u58eb"] = "https://xivapi.com/cj/1/blackmage.png",
            ["\u53ec\u559a\u58eb"] = "https://xivapi.com/cj/1/summoner.png",
            ["\u8d64\u9b54\u9053\u58eb"] = "https://xivapi.com/cj/1/redmage.png",
            ["\u9752\u9b54\u9053\u58eb"] = "https://xivapi.com/cj/1/bluemage.png",
            ["\u30d4\u30af\u30c8\u30de\u30f3\u30b5\u30fc"] = "https://xivapi.com/cj/1/pictomancer.png",

            // Crafters
            ["\u6728\u5de5\u5e2b"] = "https://xivapi.com/cj/1/carpenter.png",
            ["\u935b\u51b6\u5e2b"] = "https://xivapi.com/cj/1/blacksmith.png",
            ["\u5f6b\u91d1\u5e2b"] = "https://xivapi.com/cj/1/goldsmith.png",
            ["甲冑師"] = "https://xivapi.com/cj/1/armorer.png",
            ["\u9769\u7d30\u5de5\u5e2b"] = "https://xivapi.com/cj/1/leatherworker.png",
            ["\u88c1\u7e2b\u5e2b"] = "https://xivapi.com/cj/1/weaver.png",
            ["\u932c\u91d1\u8853\u5e2b"] = "https://xivapi.com/cj/1/alchemist.png",
            ["\u8abf\u7406\u5e2b"] = "https://xivapi.com/cj/1/culinarian.png",

            // Gatherers
            ["\u63a1\u6398\u5e2b"] = "https://xivapi.com/cj/1/miner.png",
            ["\u5712\u82b8\u5e2b"] = "https://xivapi.com/cj/1/botanist.png",
            ["\u6f01\u5e2b"] = "https://xivapi.com/cj/1/fisher.png",
        };
        return mapping;
    }

    public async Task<Image<Rgba32>?> GetIconAsync(string jobName)
    {
        if (!_jobIconUrls.TryGetValue(jobName, out var primaryUrl))
        {
            _logger.LogDebug("No icon URL mapping for job: {JobName}", jobName);
            return null;
        }

        var safeName = string.Concat(jobName.Split(Path.GetInvalidFileNameChars()));
        var cachePath = Path.Combine(_cacheDir, $"{safeName}.png");

        if (File.Exists(cachePath))
        {
            try
            {
                return Image.Load<Rgba32>(cachePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load cached icon for {JobName}, will re-download", jobName);
                File.Delete(cachePath);
            }
        }

        // Try primary URL first
        try
        {
            _logger.LogDebug("Downloading icon for {JobName} from {Url}", jobName, primaryUrl);
            var bytes = await _http.GetByteArrayAsync(primaryUrl);
            await File.WriteAllBytesAsync(cachePath, bytes);
            return Image.Load<Rgba32>(bytes);
        }
        catch
        {
            // Fallback 1: xivapi companion path
            var fileName = Path.GetFileName(primaryUrl);
            var fallbackUrl = $"https://xivapi.com/cj/companion/{fileName}";
            try
            {
                _logger.LogDebug("Trying fallback URL for {JobName}: {Url}", jobName, fallbackUrl);
                var bytes = await _http.GetByteArrayAsync(fallbackUrl);
                await File.WriteAllBytesAsync(cachePath, bytes);
                return Image.Load<Rgba32>(bytes);
            }
            catch
            {
                // Fallback 2: GitHub xivapi/classjob-icons repo
                var githubUrl = $"https://raw.githubusercontent.com/xivapi/classjob-icons/master/icons/{fileName}";
                try
                {
                    _logger.LogDebug("Trying GitHub URL for {JobName}: {Url}", jobName, githubUrl);
                    var bytes = await _http.GetByteArrayAsync(githubUrl);
                    await File.WriteAllBytesAsync(cachePath, bytes);
                    return Image.Load<Rgba32>(bytes);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to download icon for {JobName} from all URLs", jobName);
                    return null;
                }
            }
        }
    }
}
