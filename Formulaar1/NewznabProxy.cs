using APIv3SonarrDotcore.Api;
using APIv3SonarrDotcore.Model;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using static Formulaar1.Helpers;

namespace Formulaar1
{
    /// <summary>
    /// Proxies Prowlarr's Newznab/Torznab feed to Sonarr, rewriting release titles
    /// so Sonarr can match F1/F2/F3 episodes correctly.
    /// 
    /// Add to Sonarr as a Newznab indexer pointing at:
    ///   http://localhost:5000/newznab?apikey=YOUR_PROWLARR_KEY&amp;...
    /// </summary>
    internal static class NewznabProxy
    {
        internal static async Task HandleAsync(
            HttpContext context,
            HttpClient httpClient,
            string prowlarrBasePath,
            string prowlarrApiKey,
            SeriesApi seriesApi,
            EpisodeApi episodeApi)
        {
            var queryString = context.Request.QueryString.Value ?? string.Empty;

            // Prowlarr aggregate Newznab endpoint (id=0 = all indexers)
            var prowlarrUrl = $"{prowlarrBasePath.TrimEnd('/')}/api/v1/search{queryString}";
            if (!prowlarrUrl.Contains("apikey="))
                prowlarrUrl += (queryString.Length > 0 ? "&" : "?") + $"apikey={prowlarrApiKey}";

            Console.WriteLine($"[Newznab] → {prowlarrUrl}");

            HttpResponseMessage prowlarrResponse;
            try
            {
                prowlarrResponse = await httpClient.GetAsync(prowlarrUrl);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Newznab] Failed to reach Prowlarr: {ex.Message}");
                context.Response.StatusCode = 502;
                await context.Response.WriteAsync("Prowlarr unreachable");
                return;
            }

            Console.WriteLine($"[Newznab] ← {(int)prowlarrResponse.StatusCode}");
            var xml = await prowlarrResponse.Content.ReadAsStringAsync();

            // Pass through non-search responses (caps, auth errors, etc.) unchanged
            var tParam = context.Request.Query["t"].ToString().ToLowerInvariant();
            if (tParam != "search" && tParam != "tvsearch" && tParam != "")
            {
                context.Response.ContentType = "application/rss+xml; charset=utf-8";
                context.Response.StatusCode = (int)prowlarrResponse.StatusCode;
                await context.Response.WriteAsync(xml);
                return;
            }

            // Rewrite titles in the RSS/Newznab XML
            try
            {
                xml = await RewriteTitlesAsync(xml, seriesApi, episodeApi);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Newznab] Title rewrite failed, returning original: {ex.Message}");
            }

            context.Response.ContentType = "application/rss+xml; charset=utf-8";
            context.Response.StatusCode = (int)prowlarrResponse.StatusCode;
            await context.Response.WriteAsync(xml);
        }

        private static async Task<string> RewriteTitlesAsync(
            string xml, SeriesApi seriesApi, EpisodeApi episodeApi)
        {
            XDocument doc;
            try { doc = XDocument.Parse(xml); }
            catch { return xml; }

            XNamespace torznab = "http://torznab.com/schemas/2015/feed";

            var items = doc.Descendants("item").ToList();
            if (items.Count == 0) return xml;

            foreach (var item in items)
            {
                var titleEl = item.Element("title");
                if (titleEl == null) continue;

                var originalTitle = titleEl.Value;
                var rewritten = await TryRewriteTitleAsync(originalTitle, seriesApi, episodeApi);

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

        private static async Task<string?> TryRewriteTitleAsync(
            string originalTitle, SeriesApi seriesApi, EpisodeApi episodeApi)
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

            var seriesList = await seriesApi.ApiV3SeriesGetAsync(seriesInfo.TvdbId);
            if (seriesList == null || seriesList.Count == 0) return null;

            var episodes = await episodeApi.ApiV3EpisodeGetAsync(seriesList[0].Id);
            var candidates = episodes
                .Where(x => x.SeasonNumber == seasonId)
                .Where(x => x.Title.Contains(country, StringComparison.OrdinalIgnoreCase));

            var matched = GetEpisodesByShowType(candidates, seriesInfo.Title, showType).FirstOrDefault();
            if (matched == null) return null;

            var quality = Regex.Match(originalTitle,
                @"(2160[Pp]|4[Kk]|1080[Pp]|720[Pp]|480[Pp]|240[Pp])", RegexOptions.IgnoreCase);

            var seriesDetail = await seriesApi.ApiV3SeriesIdGetAsync(matched.SeriesId);
            if (seriesDetail == null) return null;

            return $"{seriesDetail.Title} - S{matched.SeasonNumber}E{matched.EpisodeNumber:00} - {matched.Title} {quality}".TrimEnd();
        }
    }
}
