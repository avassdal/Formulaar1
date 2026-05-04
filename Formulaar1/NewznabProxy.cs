using APIv3SonarrDotcore.Api;
using APIv3SonarrDotcore.Model;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using static Formulaar1.Helpers;

namespace Formulaar1
{
    /// <summary>
    /// Proxies Prowlarr's Newznab/Torznab feed to Sonarr, rewriting release titles
    /// so Sonarr can match F1/F2/F3 episodes correctly.
    /// 
    /// Add to Sonarr as a Newznab indexer:
    ///   URL: http://localhost:5000/newznab  API Path: /api
    /// </summary>
    internal static class NewznabProxy
    {
        private static List<int>? _cachedIndexerIds;

        private static async Task<List<int>> GetIndexerIdsAsync(HttpClient httpClient, string prowlarrBasePath, string prowlarrApiKey)
        {
            if (_cachedIndexerIds != null) return _cachedIndexerIds;

            var url = $"{prowlarrBasePath.TrimEnd('/')}/api/v1/indexer?apikey={prowlarrApiKey}";
            var json = await httpClient.GetStringAsync(url);
            var ids = new List<int>();
            using var doc = JsonDocument.Parse(json);
            foreach (var el in doc.RootElement.EnumerateArray())
                if (el.TryGetProperty("id", out var idProp))
                    ids.Add(idProp.GetInt32());

            Console.WriteLine($"[Newznab] Found {ids.Count} Prowlarr indexer(s): {string.Join(", ", ids)}");
            _cachedIndexerIds = ids;
            return ids;
        }

        internal static async Task HandleAsync(
            HttpContext context,
            HttpClient httpClient,
            string prowlarrBasePath,
            string prowlarrApiKey,
            string sonarrBasePath,
            string sonarrApiKey)
        {
            var queryString = context.Request.QueryString.Value ?? string.Empty;
            var tParam = context.Request.Query["t"].ToString().ToLowerInvariant();

            List<int> ids;
            try
            {
                ids = await GetIndexerIdsAsync(httpClient, prowlarrBasePath, prowlarrApiKey);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Newznab] Failed to fetch indexer list: {ex.Message}");
                context.Response.StatusCode = 502;
                await context.Response.WriteAsync("Could not retrieve Prowlarr indexer list");
                return;
            }

            if (ids.Count == 0)
            {
                context.Response.StatusCode = 503;
                await context.Response.WriteAsync("No indexers configured in Prowlarr");
                return;
            }

            // For caps and non-search requests: proxy to first indexer only
            if (tParam == "caps" || (tParam != "search" && tParam != "tvsearch" && tParam != ""))
            {
                var capsUrl = $"{prowlarrBasePath.TrimEnd('/')}/api/v1/indexer/{ids[0]}/newznab{queryString}";
                if (!capsUrl.Contains("apikey="))
                    capsUrl += (queryString.Length > 0 ? "&" : "?") + $"apikey={prowlarrApiKey}";

                Console.WriteLine($"[Newznab] → {capsUrl}");
                var capsResp = await httpClient.GetAsync(capsUrl);
                Console.WriteLine($"[Newznab] ← {(int)capsResp.StatusCode}");
                context.Response.ContentType = "application/rss+xml; charset=utf-8";
                context.Response.StatusCode = (int)capsResp.StatusCode;
                await context.Response.WriteAsync(await capsResp.Content.ReadAsStringAsync());
                return;
            }

            // For search: fan-out to all indexers and merge items
            var allItems = new List<XElement>();
            XDocument? templateDoc = null;

            await Task.WhenAll(ids.Select(async id =>
            {
                var searchUrl = $"{prowlarrBasePath.TrimEnd('/')}/api/v1/indexer/{id}/newznab{queryString}";
                if (!searchUrl.Contains("apikey="))
                    searchUrl += (queryString.Length > 0 ? "&" : "?") + $"apikey={prowlarrApiKey}";

                Console.WriteLine($"[Newznab] → {searchUrl}");
                try
                {
                    var resp = await httpClient.GetAsync(searchUrl);
                    Console.WriteLine($"[Newznab] ← [{id}] {(int)resp.StatusCode}");
                    var xml = await resp.Content.ReadAsStringAsync();
                    var doc = XDocument.Parse(xml);
                    var items = doc.Descendants("item").ToList();
                    lock (allItems)
                    {
                        if (templateDoc == null) templateDoc = doc;
                        allItems.AddRange(items);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Newznab] Indexer {id} failed: {ex.Message}");
                }
            }));

            if (templateDoc == null)
            {
                context.Response.StatusCode = 502;
                await context.Response.WriteAsync("All Prowlarr indexers failed");
                return;
            }

            // Replace items in the template document with the merged set
            var channel = templateDoc.Descendants("channel").FirstOrDefault();
            if (channel != null)
            {
                channel.Elements("item").Remove();
                foreach (var item in allItems)
                    channel.Add(item);
            }

            // Rewrite titles
            string mergedXml;
            try
            {
                mergedXml = await RewriteTitlesAsync(templateDoc, httpClient, sonarrBasePath, sonarrApiKey);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Newznab] Title rewrite failed, returning original: {ex.Message}");
                mergedXml = templateDoc.ToString();
            }

            context.Response.ContentType = "application/rss+xml; charset=utf-8";
            context.Response.StatusCode = 200;
            await context.Response.WriteAsync(mergedXml);
        }

        private static async Task<string> RewriteTitlesAsync(
            XDocument doc, HttpClient httpClient, string sonarrBasePath, string sonarrApiKey)
        {

            XNamespace torznab = "http://torznab.com/schemas/2015/feed";

            var items = doc.Descendants("item").ToList();
            if (items.Count == 0) return doc.ToString();

            foreach (var item in items)
            {
                var titleEl = item.Element("title");
                if (titleEl == null) continue;

                var originalTitle = titleEl.Value;
                var rewritten = await TryRewriteTitleAsync(originalTitle, httpClient, sonarrBasePath, sonarrApiKey);

                if (rewritten != null && rewritten != originalTitle)
                {
                    Console.WriteLine($"[Newznab] {originalTitle} → {rewritten}");
                    titleEl.Value = rewritten;

                    // Also update torznab:attr name="title" if present
                    var torznabTitle = item.Elements(torznab + "attr")
                        .FirstOrDefault(e => e.Attribute("name")?.Value == "title");
                    if (torznabTitle != null)
                        torznabTitle.SetAttributeValue("value", rewritten);
                }
            }

            return doc.Declaration != null
                ? doc.Declaration + Environment.NewLine + doc.ToString()
                : doc.ToString();
        }

        private static readonly System.Text.Json.JsonSerializerOptions _lenientJson = new()
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
            UnknownTypeHandling = System.Text.Json.Serialization.JsonUnknownTypeHandling.JsonNode,
        };

        private static async Task<string?> TryRewriteTitleAsync(
            string originalTitle, HttpClient httpClient, string sonarrBasePath, string sonarrApiKey)
        {
            var normalisedTitle = originalTitle.Replace(".", " ").Replace("-", " ");
            var seriesInfo = DetectSeries(normalisedTitle);
            if (seriesInfo == null) return null;

            _ = int.TryParse(
                Regex.Match(normalisedTitle, @"(?:(?:18|19|20|21)[0-9]{2})").Value,
                out int seasonId);
            if (seasonId == 0) return null;

            var country = Countries
                .FirstOrDefault(x => normalisedTitle.Contains(x.Key, StringComparison.OrdinalIgnoreCase))
                .Value;
            if (country == null) return null;

            var showType = NormaliseShowType(normalisedTitle);
            if (showType == null) return null;

            var base_ = sonarrBasePath.TrimEnd('/');
            using var req1 = new HttpRequestMessage(HttpMethod.Get,
                $"{base_}/api/v3/series?tvdbId={seriesInfo.TvdbId}");
            req1.Headers.Add("X-Api-Key", sonarrApiKey);
            var resp1 = await httpClient.SendAsync(req1);
            var seriesJson = await resp1.Content.ReadAsStringAsync();
            using var seriesDoc = System.Text.Json.JsonDocument.Parse(seriesJson);
            var seriesArr = seriesDoc.RootElement;
            if (seriesArr.GetArrayLength() == 0) return null;
            var seriesId = seriesArr[0].GetProperty("id").GetInt32();
            var seriesTitle = seriesArr[0].GetProperty("title").GetString();

            using var req2 = new HttpRequestMessage(HttpMethod.Get,
                $"{base_}/api/v3/episode?seriesId={seriesId}");
            req2.Headers.Add("X-Api-Key", sonarrApiKey);
            var resp2 = await httpClient.SendAsync(req2);
            var episodesJson = await resp2.Content.ReadAsStringAsync();
            using var episodesDoc = System.Text.Json.JsonDocument.Parse(episodesJson);

            // Collect candidates: correct season + country in title
            var candidates = episodesDoc.RootElement.EnumerateArray()
                .Where(ep =>
                    ep.TryGetProperty("seasonNumber", out var snProp) && snProp.GetInt32() == seasonId &&
                    ep.TryGetProperty("title", out var titleProp) &&
                    (titleProp.GetString() ?? "").Contains(country, StringComparison.OrdinalIgnoreCase) &&
                    ep.TryGetProperty("episodeNumber", out _))
                .ToList();

            bool isF1 = seriesInfo.Title.Equals("Formula 1", StringComparison.OrdinalIgnoreCase);

            // Apply show-type filter matching GetEpisodesByShowType logic
            Func<string, bool> epTitleFilter = showType switch
            {
                "Sprint Shootout" when isF1 => t =>
                    t.Contains("Shootout", StringComparison.OrdinalIgnoreCase) ||
                    t.Contains("Sprint Qualifying", StringComparison.OrdinalIgnoreCase),

                "Sprint Race" when isF1 => t =>
                    t.Contains("Sprint", StringComparison.OrdinalIgnoreCase) &&
                    !t.Contains("Shootout", StringComparison.OrdinalIgnoreCase),

                "Sprint" when isF1 => t =>
                    t.Contains("Sprint", StringComparison.OrdinalIgnoreCase) &&
                    !t.Contains("Shootout", StringComparison.OrdinalIgnoreCase),

                "Sprint Race" => t => t.Contains("Sprint Race", StringComparison.OrdinalIgnoreCase),
                "Sprint"      => t => t.Contains("Sprint Race", StringComparison.OrdinalIgnoreCase),
                "Feature Race"=> t => t.Contains("Feature Race", StringComparison.OrdinalIgnoreCase),

                "Race" when !isF1 => t => t.Contains("Feature Race", StringComparison.OrdinalIgnoreCase),

                _ => t => t.Contains(showType, StringComparison.OrdinalIgnoreCase),
            };

            System.Text.Json.JsonElement? matched = candidates
                .Where(ep =>
                {
                    var t = ep.GetProperty("title").GetString() ?? "";
                    return epTitleFilter(t);
                })
                .Cast<System.Text.Json.JsonElement?>()
                .FirstOrDefault();

            if (matched == null) return null;

            var quality = Regex.Match(originalTitle,
                @"(2160[Pp]|4[Kk]|1080[Pp]|720[Pp]|480[Pp]|240[Pp])", RegexOptions.IgnoreCase);

            var sn = matched.Value.GetProperty("seasonNumber").GetInt32();
            var en = matched.Value.GetProperty("episodeNumber").GetInt32();
            var epTitleFinal = matched.Value.GetProperty("title").GetString();

            return $"{seriesTitle} - S{sn}E{en:00} - {epTitleFinal} {quality}".TrimEnd();
        }
    }
}
