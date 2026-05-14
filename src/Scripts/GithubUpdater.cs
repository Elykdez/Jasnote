using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jesnote;

public static class GithubUpdater
{
    public sealed class ReleaseInfo
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = "";
    }

    public static async Task<(string latest, bool isNewer)> CheckAsync(
        string current,
        CancellationToken ct
    )
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.GithubUserAgent);
            var json = await http.GetStringAsync(AppInfo.GithubLatestReleaseApiUrl, ct)
                .ConfigureAwait(false);
            var info = JsonSerializer.Deserialize<ReleaseInfo>(json);
            if (info == null || string.IsNullOrEmpty(info.TagName))
                return ("", false);
            string tag = info.TagName.TrimStart('v');
            if (!Version.TryParse(tag, out var latest))
                return (tag, false);
            if (!Version.TryParse(current.TrimStart('v'), out var cur))
                return (tag, false);
            return (tag, cur < latest);
        }
        catch
        {
            return ("", false);
        }
    }
}
