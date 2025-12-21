using HtmlAgilityPack;
using KamiYomu.CrawlerAgents.Core;
using KamiYomu.CrawlerAgents.Core.Catalog;
using KamiYomu.CrawlerAgents.Core.Catalog.Builders;
using KamiYomu.CrawlerAgents.Core.Catalog.Definitions;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Page = KamiYomu.CrawlerAgents.Core.Catalog.Page;

namespace KamiYomu.CrawlerAgents.WeebCentral;

[DisplayName("KamiYomu Crawler Agent – weebcentral.com")]
public class WeebCentralCrawlerAgent : AbstractCrawlerAgent, ICrawlerAgent
{
    private bool _disposed = false;
    private readonly Uri _baseUri;
    private readonly string _language;
    private Lazy<Task<IBrowser>> _browser;
    private readonly string _timezone;

    public WeebCentralCrawlerAgent(IDictionary<string, object> options) : base(options)
    {
        _baseUri = new Uri("https://weebcentral.com");
        _browser = new Lazy<Task<IBrowser>>(CreateBrowserAsync, true);
        _timezone = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "Eastern Standard Time"
            : "America/New_York";
    }

    public Task<IBrowser> GetBrowserAsync() => _browser.Value;

    private async Task<IBrowser> CreateBrowserAsync()
    {
        var launchOptions = new LaunchOptions
        {
            Headless = true,
            Timeout = TimeoutMilliseconds,
            Args = [
                "--disable-blink-features=AutomationControlled",
                "--no-sandbox",
                "--disable-dev-shm-usage"
            ]
        };

        return await Puppeteer.LaunchAsync(launchOptions);
    }

    private async Task PreparePageForNavigationAsync(IPage page)
    {
        page.Console += (sender, e) =>
        {
            // e.Message contains the console message
            Logger?.LogDebug($"[Browser Console] {e.Message.Type}: {e.Message.Text}");

            // You can also inspect arguments
            if (e.Message.Args != null)
            {
                foreach (var arg in e.Message.Args)
                {
                    Logger?.LogDebug($"   Arg: {arg.RemoteObject.Value}");
                }
            }
        };

        await page.EvaluateExpressionOnNewDocumentAsync(@"
                // Neutralize devtools detection
                const originalLog = console.log;
                console.log = function(...args) {
                    if (args.length === 1 && args[0] === '[object HTMLDivElement]') {
                        return; // skip detection trick
                    }
                    return originalLog.apply(console, args);
                };

                // Override reload to do nothing
                window.location.reload = () => console.log('Reload prevented');
            ");

        await page.EmulateTimezoneAsync(_timezone);

        var fixedDate = DateTime.Now;

        var fixedDateIso = fixedDate.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        await page.EvaluateExpressionOnNewDocumentAsync($@"
                // Freeze time to a specific date
                const fixedDate = new Date('{fixedDateIso}');
                Date = class extends Date {{
                    constructor(...args) {{
                        if (args.length === 0) {{
                            return fixedDate;
                        }}
                        return super(...args);
                    }}
                    static now() {{
                        return fixedDate.getTime();
                    }}
                }};
            ");

    }

    private string NormalizeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;

        if (!url.StartsWith("/") && Uri.TryCreate(url, UriKind.Absolute, out var absolute))
            return absolute.ToString();

        var resolved = new Uri(_baseUri, url);
        return resolved.ToString();
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            if (_browser.IsValueCreated)
            {
                var browserTask = _browser.Value;
                if (browserTask.IsCompletedSuccessfully)
                {
                    browserTask.Result.Dispose();
                }
            }
        }

        _disposed = true;
    }

    // // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
    ~WeebCentralCrawlerAgent()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: false);
    }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    private static bool IsGenreNotFamilySafe(string p)
    {
        if (string.IsNullOrWhiteSpace(p)) return false;
        return p.Contains("adult", StringComparison.OrdinalIgnoreCase)
            || p.Contains("harem", StringComparison.OrdinalIgnoreCase)
            || p.Contains("hentai", StringComparison.OrdinalIgnoreCase)
            || p.Contains("ecchi", StringComparison.OrdinalIgnoreCase)
            || p.Contains("violence", StringComparison.OrdinalIgnoreCase)
            || p.Contains("smut", StringComparison.OrdinalIgnoreCase)
            || p.Contains("shota", StringComparison.OrdinalIgnoreCase)
            || p.Contains("sexual", StringComparison.OrdinalIgnoreCase);
    }

    public Task<Uri> GetFaviconAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(new Uri(_baseUri, "/favicon.ico"));
    }

    public async Task<PagedResult<Manga>> SearchAsync(string titleName, PaginationOptions paginationOptions, CancellationToken cancellationToken)
    {
        var browser = await GetBrowserAsync();
        using var page = await browser.NewPageAsync();
        await PreparePageForNavigationAsync(page);
        await page.SetUserAgentAsync(HttpClientDefaultUserAgent);

        var targetUri = new Uri(new Uri(_baseUri.ToString()), $"search?limit={paginationOptions?.Limit ?? 30}&offset={paginationOptions?.OffSet ?? 0}&text={titleName}&sort=Best+Match&order=Descending&official=Any&anime=Any&adult=Any&display_mode=Full+Display");

        await page.GoToAsync(targetUri.ToString(), new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.DOMContentLoaded, WaitUntilNavigation.Load, WaitUntilNavigation.Networkidle0],
            Timeout = TimeoutMilliseconds
        });

        foreach (var cookie in await page.GetCookiesAsync())
        {
            Logger?.LogDebug("{name}={value}; Domain={domain}; Path={path}", cookie.Name, cookie.Value, cookie.Domain, cookie.Path);
        }

        var content = await page.GetContentAsync();

        var document = new HtmlDocument();
        document.LoadHtml(content);

        List<Manga> mangas = [];

        var nodes = document.DocumentNode.SelectNodes("//section[@id='search-results']//article[*[1][self::section]]");

        if (nodes != null)
        {
            foreach (var divNode in nodes)
            {
                Manga manga = ConvertToMangaFromList(divNode);
                mangas.Add(manga);
            }
        }

        return PagedResultBuilder<Manga>.Create()
            .WithData(mangas)
            .WithPaginationOptions(new PaginationOptions(paginationOptions.OffSet + paginationOptions.Limit, paginationOptions.Limit))
            .Build();
    }

    private Manga ConvertToMangaFromList(HtmlNode articleNode)
    {
        var linkNode = articleNode.SelectSingleNode(".//section[1]//a");
        var websiteUrl = linkNode?.GetAttributeValue("href", string.Empty) ?? "";

        var imgNode = articleNode.SelectSingleNode(".//section[1]//img");
        var coverUrl = imgNode?.GetAttributeValue("src", string.Empty) ?? "";

        var coverFileName = Path.GetFileName(new Uri(coverUrl).AbsolutePath);

        var titleNode = articleNode.SelectSingleNode(".//section[2]//a[contains(@class,'link-hover')]");
        var title = titleNode?.InnerText.Trim() ?? "Unknown Title";

        string id = "";
        if (!string.IsNullOrEmpty(websiteUrl))
        {
            var parts = websiteUrl.Split('/');
            int idx = Array.IndexOf(parts, "series");
            if (idx >= 0 && idx + 1 < parts.Length)
                id = parts[idx + 1];
        }

        var yearNode = articleNode.SelectSingleNode(".//strong[text()='Year:']/following-sibling::span");
        var year = yearNode?.InnerText.Trim() ?? "";

        var statusNode = articleNode.SelectSingleNode(".//strong[text()='Status:']/following-sibling::span");
        var status = statusNode?.InnerText.Trim() ?? "";

        var typeNode = articleNode.SelectSingleNode(".//strong[text()='Type:']/following-sibling::span");
        var type = typeNode?.InnerText.Trim() ?? "";

        var authorNodes = articleNode.SelectNodes(".//strong[contains(text(),'Author')]/following-sibling::span/a");
        var authors = authorNodes?.Select(a => a.InnerText.Trim()).ToList() ?? new List<string>();

        var genreNodes = articleNode.SelectNodes(".//strong[text()='Tag(s): ']/following-sibling::span");
        var genres = genreNodes?.Select(g => g.InnerText.Trim().TrimEnd(',')).ToList() ?? new List<string>();

        var manga = MangaBuilder.Create()
            .WithId(id)
            .WithTitle(title)
            .WithAuthors([.. authors])
            .WithDescription("No Description Available")
            .WithCoverUrl(new Uri(coverUrl))
            .WithCoverFileName(coverFileName)
            .WithWebsiteUrl(websiteUrl)
            .WithAlternativeTitles(new Dictionary<string, string>())
            .WithLatestChapterAvailable(0)
            .WithLastVolumeAvailable(0)
            .WithTags([.. genres])
            .WithOriginalLanguage(_language)
            .WithIsFamilySafe(!genres.Any(IsGenreNotFamilySafe))
            .Build();

        return manga;
    }

    public async Task<Manga> GetByIdAsync(string id, CancellationToken cancellationToken)
    {
        var browser = await GetBrowserAsync();
        using var page = await browser.NewPageAsync();
        await PreparePageForNavigationAsync(page);
        await page.SetUserAgentAsync(HttpClientDefaultUserAgent);

        var finalUrl = new Uri(_baseUri, $"series/{id}").ToString();
        var response = await page.GoToAsync(finalUrl, new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.DOMContentLoaded, WaitUntilNavigation.Load, WaitUntilNavigation.Networkidle0],
            Timeout = TimeoutMilliseconds
        });

        foreach (var cookie in await page.GetCookiesAsync())
        {
            Logger?.LogDebug("{name}={value}; Domain={domain}; Path={path}", cookie.Name, cookie.Value, cookie.Domain, cookie.Path);
        }

        var content = await page.GetContentAsync();
        var document = new HtmlDocument();
        document.LoadHtml(content);
        var rootNode = document.DocumentNode.SelectSingleNode("//div[@id='top']//section");
        Manga manga = ConvertToMangaFromSingleBook(rootNode, id);

        return manga;
    }

    private Manga ConvertToMangaFromSingleBook(HtmlNode rootNode, string id)
    {
        // --- TITLE ---
        var title = rootNode
            .SelectSingleNode(".//h1[contains(@class,'text-2xl')]")
            ?.InnerText
            ?.Trim()
            ?? string.Empty;

        // --- COVER URL ---
        var imgNode = rootNode.SelectSingleNode(".//picture/img");
        var coverUrl = imgNode?.GetAttributeValue("src", "") ?? "";
        var coverFileName = Path.GetFileName(coverUrl);

        // --- AUTHORS ---
        var authors = rootNode
            .SelectNodes(".//strong[contains(text(),'Author')]/parent::li//a")
            ?.Select(a => a.InnerText.Trim())
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .ToList()
            ?? new List<string>();

        // --- GENRES / TAGS ---
        var genres = rootNode
            .SelectNodes(".//strong[contains(text(),'Tags')]/parent::li//a")
            ?.Select(a => a.InnerText.Trim())
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .ToList()
            ?? new List<string>();

        // --- RELEASE STATUS ---
        var releaseStatus = rootNode
            .SelectSingleNode(".//strong[contains(text(),'Status')]/parent::li/a")
            ?.InnerText
            ?.Trim()
            ?? "Unknown";

        // --- RELEASE YEAR ---
        var releaseYear = rootNode
            .SelectSingleNode(".//strong[contains(text(),'Released')]/parent::li/span")
            ?.InnerText
            ?.Trim()
            ?? string.Empty;

        // --- DESCRIPTION ---
        var description = rootNode
            .SelectSingleNode(".//strong[contains(text(),'Description')]/parent::li/p")
            ?.InnerText
            ?.Trim()
            ?? string.Empty;

        // --- ALTERNATIVE TITLES ---
        var altTitles = rootNode
            .SelectNodes(".//strong[contains(text(),'Associated')]/parent::li//ul/li")
            ?.Select(li => li.InnerText.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList()
            ?? new List<string>();

        // --- WEBSITE URL (you already have href externally) ---
        var href = ""; // fill from caller if needed

        // --- BUILD MANGA OBJECT ---
        var manga = MangaBuilder.Create()
            .WithId(id)
            .WithTitle(title)
            .WithAlternativeTitles(
                altTitles.Select((p, i) => new { i = i.ToString(), p })
                         .ToDictionary(x => x.i, x => x.p))
            .WithDescription(description)
            .WithAuthors([.. authors])
            .WithTags([.. genres])
            .WithCoverUrl(new Uri(coverUrl))
            .WithCoverFileName(coverFileName)
            .WithWebsiteUrl(NormalizeUrl(href))
            .WithIsFamilySafe(!genres.Any(g => IsGenreNotFamilySafe(g)))
            .WithReleaseStatus(releaseStatus.ToLowerInvariant() switch
            {
                "complete" or "completed" => ReleaseStatus.Completed,
                "hiatus" => ReleaseStatus.OnHiatus,
                "cancelled" => ReleaseStatus.Cancelled,
                _ => ReleaseStatus.Continuing,
            })
            .Build();

        return manga;
    }

    public async Task<PagedResult<Chapter>> GetChaptersAsync(Manga manga, PaginationOptions paginationOptions, CancellationToken cancellationToken)
    {

        var browser = await GetBrowserAsync();
        using var page = await browser.NewPageAsync();
        await PreparePageForNavigationAsync(page);
        await page.SetUserAgentAsync(HttpClientDefaultUserAgent);

        var finalUrl = new Uri(_baseUri, $"series/{manga.Id}").ToString();
        var response = await page.GoToAsync(finalUrl, new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.DOMContentLoaded, WaitUntilNavigation.Load, WaitUntilNavigation.Networkidle0],
            Timeout = TimeoutMilliseconds
        });

        foreach (var cookie in await page.GetCookiesAsync())
        {
            Logger?.LogDebug("{name}={value}; Domain={domain}; Path={path}", cookie.Name, cookie.Value, cookie.Domain, cookie.Path);
        }

        var button = await page.QuerySelectorAsync("#chapter-list button");

        if (button != null)
        {
            await button.ClickAsync();
            await Task.Delay(1500, cancellationToken);
        }
        else
        {
            Logger?.LogWarning("No button found inside #chapter-list");
        }

        var content = await page.GetContentAsync();

        var document = new HtmlDocument();
        document.LoadHtml(content);
        HtmlNodeCollection? nodes = document.DocumentNode.SelectNodes("//div[@id='chapter-list']//div");
        IEnumerable<Chapter> chapters = ConvertChaptersFromSingleBook(manga, nodes);

        return PagedResultBuilder<Chapter>.Create()
                                          .WithPaginationOptions(new PaginationOptions(chapters.Count(), chapters.Count(), chapters.Count()))
                                          .WithData(chapters)
                                          .Build();
    }

    private IEnumerable<Chapter> ConvertChaptersFromSingleBook(Manga manga, HtmlNodeCollection nodes)
    {
        var chapters = new List<Chapter>();

        foreach (var chapterDiv in nodes)
        {
            // --- <a> element containing chapter info ---
            var linkNode = chapterDiv.SelectSingleNode(".//a");
            if (linkNode == null)
                continue;

            var uri = linkNode.GetAttributeValue("href", "").Trim();
            var chapterId = uri.Split('/').Last(); // e.g. "01J76XYZWFJV8EXH5V998ZN7VP"

            // --- Chapter title text ---
            var titleNode = linkNode.SelectSingleNode(".//span[contains(text(),'Chapter')]");
            var title = titleNode?.InnerText.Trim() ?? "Unknown Chapter";

            // --- Extract chapter number ---
            // "Chapter 335" → 335
            int number = 0;
            var match = Regex.Match(title, @"(\d+)");
            if (match.Success)
                number = int.Parse(match.Groups[1].Value);

            // --- Volume (not provided on this site) ---
            int volume = 0;

            // --- Release date (optional) ---
            var timeNode = linkNode.SelectSingleNode(".//time");
            var releaseDate = timeNode?.GetAttributeValue("datetime", null);

            // --- Build Chapter object ---
            var chapter = ChapterBuilder.Create()
                .WithId(chapterId)
                .WithTitle(title)
                .WithParentManga(manga)
                .WithVolume(volume)
                .WithNumber(number)
                .WithUri(new Uri(NormalizeUrl(uri)))
                .WithTranslatedLanguage("en")
                .Build();

            chapters.Add(chapter);
        }

        return chapters;
    }

    public async Task<IEnumerable<Core.Catalog.Page>> GetChapterPagesAsync(Chapter chapter, CancellationToken cancellationToken)
    {
        var browser = await GetBrowserAsync();
        using var page = await browser.NewPageAsync();

        await PreparePageForNavigationAsync(page);
        await page.SetUserAgentAsync(HttpClientDefaultUserAgent);

        await page.GoToAsync(chapter.Uri.ToString(), new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.DOMContentLoaded, WaitUntilNavigation.Load, WaitUntilNavigation.Networkidle0],
            Timeout = TimeoutMilliseconds
        });

        await page.EvaluateFunctionAsync(@"async () => {
            await new Promise(resolve => {
                let totalHeight = 0;
                const distance = 500;
                const timer = setInterval(() => {
                    window.scrollBy(0, distance);
                    totalHeight += distance;

                    if (totalHeight >= document.body.scrollHeight) {
                        clearInterval(timer);
                        resolve();
                    }
                }, 200);
            });
        }");

        await Task.Delay(1000, cancellationToken);

        foreach (var cookie in await page.GetCookiesAsync())
        {
            Logger?.LogDebug("{name}={value}; Domain={domain}; Path={path}", cookie.Name, cookie.Value, cookie.Domain, cookie.Path);
        }

        var content = await page.GetContentAsync();
        var document = new HtmlDocument();
        document.LoadHtml(content);

        var pageNodes = document.DocumentNode.SelectNodes("//section[contains(@class,'cursor-pointer')]//img");
        return ConvertToChapterPages(chapter, pageNodes);
    }

    private IEnumerable<Page> ConvertToChapterPages(Chapter chapter, HtmlNodeCollection pageNodes)
    {
        if (pageNodes == null)
            return Enumerable.Empty<Page>();

        var pages = new List<Page>();
        int index = 1;

        foreach (var node in pageNodes)
        {
            // --- Extract image URL ---
            var imageUrl = node.GetAttributeValue("src", "").Trim();
            if (string.IsNullOrWhiteSpace(imageUrl))
                continue;

            // --- Extract page number from alt="Page X" ---
            var alt = node.GetAttributeValue("alt", "").Trim();
            int pageNumber = 0;

            var match = Regex.Match(alt, @"(\d+)");
            if (match.Success)
                pageNumber = int.Parse(match.Groups[1].Value);
            else
                pageNumber = index; // fallback

            // --- Generate a unique page ID ---
            // Example: chapterId + "-p001"
            var idAttr = $"{chapter.Id}-p{index:D3}";

            // --- Build Page object ---
            var page = PageBuilder.Create()
                .WithChapterId(chapter.Id)
                .WithId(idAttr)
                .WithPageNumber(pageNumber)
                .WithImageUrl(new Uri(imageUrl))
                .WithParentChapter(chapter)
                .Build();

            pages.Add(page);
            index++;
        }

        return pages;
    }
}
