using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Discord;
using Discord.Commands;
using Discord.Interactions;
using Discord.WebSocket;
using HtmlAgilityPack;
using Microsoft.Extensions.DependencyInjection;

var token = Config.Token;
if (string.IsNullOrWhiteSpace(token))
{
    Console.WriteLine("Token is missing");
    return;
}

var client = new DiscordSocketClient(new DiscordSocketConfig
{
    GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.MessageContent
});

var interactionService = new InteractionService(client.Rest);
var commandService = new CommandService(new CommandServiceConfig
{
    CaseSensitiveCommands = false
});

var services = new ServiceCollection()
    .AddSingleton(client)
    .AddSingleton(interactionService)
    .AddSingleton(commandService)
    .AddSingleton<CommandHandler>()
    .BuildServiceProvider();

client.Log += LogAsync;
interactionService.Log += LogAsync;
commandService.Log += LogAsync;

await services.GetRequiredService<CommandHandler>().InitializeAsync();

await client.LoginAsync(TokenType.Bot, token);
await client.StartAsync();

await Task.Delay(Timeout.Infinite);

static Task LogAsync(LogMessage message)
{
    Console.WriteLine($"[{DateTimeOffset.Now:O}] {message.Severity}: {message.Source} - {message.Message}");
    if (message.Exception != null)
    {
        Console.WriteLine(message.Exception);
    }
    return Task.CompletedTask;
}

public sealed class CommandHandler
{
    private readonly DiscordSocketClient _client;
    private readonly InteractionService _interactionService;
    private readonly CommandService _commandService;
    private readonly IServiceProvider _services;

    public CommandHandler(DiscordSocketClient client, InteractionService interactionService, CommandService commandService, IServiceProvider services)
    {
        _client = client;
        _interactionService = interactionService;
        _commandService = commandService;
        _services = services;
    }

    public async Task InitializeAsync()
    {
        await _interactionService.AddModulesAsync(Assembly.GetEntryAssembly(), _services);
        await _commandService.AddModulesAsync(Assembly.GetEntryAssembly(), _services);

        _client.Ready += OnReadyAsync;
        _client.InteractionCreated += OnInteractionCreatedAsync;
        _client.MessageReceived += OnMessageReceivedAsync;
    }

    private async Task OnReadyAsync()
    {
        var guild = Config.Guild;
        if (string.IsNullOrWhiteSpace(guild))
        {
            await _interactionService.RegisterCommandsGloballyAsync(true);
            return;
        }

        if (ulong.TryParse(guild, out var guildId))
        {
            await _interactionService.RegisterCommandsToGuildAsync(guildId, true);
        }
        else
        {
            await _interactionService.RegisterCommandsGloballyAsync(true);
        }
    }

    private async Task OnInteractionCreatedAsync(SocketInteraction interaction)
    {
        var ctx = new SocketInteractionContext(_client, interaction);
        await _interactionService.ExecuteCommandAsync(ctx, _services);
    }

    private async Task OnMessageReceivedAsync(SocketMessage rawMessage)
    {
        if (rawMessage is not SocketUserMessage message) return;
        if (message.Author.IsBot) return;

        var argPos = 0;
        if (!(message.HasCharPrefix('!', ref argPos) || message.HasMentionPrefix(_client.CurrentUser, ref argPos))) return;

        var ctx = new SocketCommandContext(_client, message);
        await _commandService.ExecuteAsync(ctx, argPos, _services);
    }
}

public sealed class DownloadModule : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("download", "Download a website (static assets only)")]
    public async Task DownloadAsync(string url)
    {
        await RespondAsync("please wait while we download  note this doesnt download backend and sometimes isnt always accurate and can miss some functions", ephemeral: true);

        string? outputDir = null;
        string? zipPath = null;

        try
        {
            outputDir = await WebsiteDownloader.DownloadAsync(url);
            zipPath = WebsiteDownloader.CreateZip(outputDir);

            await FollowupWithFileAsync(zipPath, $"Done. Here is your download: `{Path.GetFileName(zipPath)}`", ephemeral: true);
        }
        catch (Exception ex)
        {
            await FollowupAsync($"Failed: {ex.Message}", ephemeral: true);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(outputDir) && Directory.Exists(outputDir))
            {
                try { Directory.Delete(outputDir, true); } catch { }
            }

            if (!string.IsNullOrWhiteSpace(zipPath) && File.Exists(zipPath))
            {
                try { File.Delete(zipPath); } catch { }
            }
        }
    }
}

public static class WebsiteDownloader
{
    private static readonly Regex CssUrlRegex = new Regex(@"url\((?<url>[^)]+)\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CssImportRegex = new Regex(@"@import\s+(?:url\()?['""]?(?<url>[^'""\)]+)['""]?\)?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static async Task<string> DownloadAsync(string inputUrl)
    {
        if (string.IsNullOrWhiteSpace(inputUrl))
        {
            throw new InvalidOperationException("URL is missing.");
        }

        if (!inputUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !inputUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            inputUrl = "https://" + inputUrl;
        }

        if (!Uri.TryCreate(inputUrl, UriKind.Absolute, out var baseUri))
        {
            throw new InvalidOperationException("Invalid URL.");
        }

        string outputDir = $"downloaded_{baseUri.Host}_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
        Directory.CreateDirectory(outputDir);

        using var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };

        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

        HttpResponseMessage mainResponse;
        try
        {
            mainResponse = await client.GetAsync(baseUri);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Request failed: {ex.Message}");
        }

        if (!mainResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Failed: {(int)mainResponse.StatusCode} {mainResponse.ReasonPhrase}");
        }

        byte[] mainBytes = await mainResponse.Content.ReadAsByteArrayAsync();
        string mainHtml = DecodeText(mainBytes, mainResponse.Content.Headers.ContentType);

        var urlToLocal = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var doc = new HtmlDocument();
        doc.LoadHtml(mainHtml);

        var nodesWithSrc = doc.DocumentNode.SelectNodes("//*[@src]") ?? new HtmlNodeCollection(doc.DocumentNode);
        foreach (var node in nodesWithSrc)
        {
            await ProcessAttributeAsync(client, baseUri, outputDir, urlToLocal, node, "src", GuessSubfolder(node.Name));
        }

        var nodesWithHref = doc.DocumentNode.SelectNodes("//*[@href]") ?? new HtmlNodeCollection(doc.DocumentNode);
        foreach (var node in nodesWithHref)
        {
            if (node.Name.Equals("link", StringComparison.OrdinalIgnoreCase))
            {
                var rel = node.GetAttributeValue("rel", "");
                if (rel.Contains("stylesheet", StringComparison.OrdinalIgnoreCase) ||
                    rel.Contains("icon", StringComparison.OrdinalIgnoreCase) ||
                    rel.Contains("shortcut icon", StringComparison.OrdinalIgnoreCase) ||
                    rel.Contains("apple-touch-icon", StringComparison.OrdinalIgnoreCase))
                {
                    string subfolder = rel.Contains("stylesheet", StringComparison.OrdinalIgnoreCase) ? "assets/css" : "assets/img";
                    await ProcessAttributeAsync(client, baseUri, outputDir, urlToLocal, node, "href", subfolder);
                }
            }
        }

        var nodesWithPoster = doc.DocumentNode.SelectNodes("//*[@poster]") ?? new HtmlNodeCollection(doc.DocumentNode);
        foreach (var node in nodesWithPoster)
        {
            await ProcessAttributeAsync(client, baseUri, outputDir, urlToLocal, node, "poster", "assets/media");
        }

        var nodesWithSrcset = doc.DocumentNode.SelectNodes("//*[@srcset]") ?? new HtmlNodeCollection(doc.DocumentNode);
        foreach (var node in nodesWithSrcset)
        {
            string srcset = node.GetAttributeValue("srcset", "");
            if (string.IsNullOrWhiteSpace(srcset))
            {
                continue;
            }

            var parts = srcset.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var rebuilt = new List<string>();
            foreach (var part in parts)
            {
                var spaceIndex = part.IndexOf(' ');
                string urlPart = spaceIndex > 0 ? part.Substring(0, spaceIndex) : part;
                string descriptor = spaceIndex > 0 ? part.Substring(spaceIndex).Trim() : string.Empty;

                var local = await DownloadAssetAsync(client, baseUri, outputDir, urlToLocal, urlPart, "assets/img");
                if (!string.IsNullOrEmpty(local))
                {
                    string rel = ToRelativePath(outputDir, local);
                    rebuilt.Add(string.IsNullOrEmpty(descriptor) ? rel : $"{rel} {descriptor}");
                }
            }

            if (rebuilt.Count > 0)
            {
                node.SetAttributeValue("srcset", string.Join(", ", rebuilt));
            }
        }

        string htmlPath = Path.Combine(outputDir, "index.html");
        string htmlOutput = BeautifyHtml(doc, mainHtml);
        await File.WriteAllTextAsync(htmlPath, htmlOutput, Encoding.UTF8);

        return outputDir;
    }

    public static string CreateZip(string sourceDir)
    {
        string parent = Path.GetDirectoryName(sourceDir) ?? ".";
        string zipName = Path.GetFileName(sourceDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".zip";
        string zipPath = Path.Combine(parent, zipName);

        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        ZipFile.CreateFromDirectory(sourceDir, zipPath, CompressionLevel.Optimal, includeBaseDirectory: true);
        return zipPath;
    }

    private static string GuessSubfolder(string tagName)
    {
        return tagName.ToLowerInvariant() switch
        {
            "script" => "assets/js",
            "img" => "assets/img",
            "source" => "assets/media",
            "video" => "assets/media",
            "audio" => "assets/media",
            _ => "assets/other"
        };
    }

    private static async Task ProcessAttributeAsync(HttpClient client, Uri baseUri, string outputDir,
        Dictionary<string, string> urlToLocal, HtmlNode node, string attributeName, string subfolder)
    {
        string value = node.GetAttributeValue(attributeName, "");
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var local = await DownloadAssetAsync(client, baseUri, outputDir, urlToLocal, value, subfolder);
        if (!string.IsNullOrEmpty(local))
        {
            node.SetAttributeValue(attributeName, ToRelativePath(outputDir, local));
        }
    }

    private static async Task<string?> DownloadAssetAsync(HttpClient client, Uri baseUri, string outputDir,
        Dictionary<string, string> urlToLocal, string rawUrl, string subfolder)
    {
        if (IsSkippableUrl(rawUrl))
        {
            return null;
        }

        if (!Uri.TryCreate(baseUri, rawUrl, out var absoluteUri))
        {
            return null;
        }

        string absolute = absoluteUri.ToString();
        if (urlToLocal.TryGetValue(absolute, out var existing))
        {
            return existing;
        }

        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync(absoluteUri);
        }
        catch
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        byte[] bytes = await response.Content.ReadAsByteArrayAsync();
        string contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;

        string extension = Path.GetExtension(absoluteUri.AbsolutePath);
        if (string.IsNullOrEmpty(extension))
        {
            extension = ExtensionFromContentType(contentType);
        }

        string filename = Path.GetFileName(absoluteUri.AbsolutePath);
        if (string.IsNullOrWhiteSpace(filename))
        {
            filename = "file" + extension;
        }

        filename = SanitizeFileName(filename);
        string folderPath = Path.Combine(outputDir, subfolder);
        Directory.CreateDirectory(folderPath);
        string filePath = EnsureUniquePath(Path.Combine(folderPath, filename));

        if (contentType.Contains("text/css", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".css", StringComparison.OrdinalIgnoreCase))
        {
            string cssText = DecodeText(bytes, response.Content.Headers.ContentType);
            string rewritten = await RewriteCssAsync(client, baseUri, outputDir, urlToLocal, cssText, Path.GetDirectoryName(filePath) ?? outputDir);
            await File.WriteAllTextAsync(filePath, rewritten, Encoding.UTF8);
        }
        else
        {
            await File.WriteAllBytesAsync(filePath, bytes);
        }

        urlToLocal[absolute] = filePath;
        return filePath;
    }

    private static async Task<string> RewriteCssAsync(HttpClient client, Uri baseUri, string outputDir,
        Dictionary<string, string> urlToLocal, string cssText, string cssFolder)
    {
        string rewritten = cssText;

        foreach (Match match in CssImportRegex.Matches(cssText))
        {
            string rawUrl = match.Groups["url"].Value.Trim();
            var local = await DownloadAssetAsync(client, baseUri, outputDir, urlToLocal, rawUrl, "assets/css");
            if (!string.IsNullOrEmpty(local))
            {
                string rel = ToRelativePath(cssFolder, local);
                string replaced = ReplaceFirst(match.Value, rawUrl, rel);
                rewritten = rewritten.Replace(match.Value, replaced);
            }
        }

        foreach (Match match in CssUrlRegex.Matches(cssText))
        {
            string raw = match.Groups["url"].Value.Trim().Trim('"', '\'');
            if (IsSkippableUrl(raw))
            {
                continue;
            }

            var local = await DownloadAssetAsync(client, baseUri, outputDir, urlToLocal, raw, "assets/img");
            if (!string.IsNullOrEmpty(local))
            {
                string rel = ToRelativePath(cssFolder, local);
                rewritten = rewritten.Replace(match.Value, $"url('{rel}')");
            }
        }

        return rewritten;
    }

    private static bool IsSkippableUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return true;
        }

        return url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
               url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
               url.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
               url.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) ||
               url.StartsWith("#", StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return name;
    }

    private static string EnsureUniquePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        string dir = Path.GetDirectoryName(path) ?? ".";
        string filename = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);

        for (int i = 1; i < 10000; i++)
        {
            string candidate = Path.Combine(dir, $"{filename}_{i}{ext}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(dir, Guid.NewGuid().ToString("N") + ext);
    }

    private static string ToRelativePath(string baseDir, string fullPath)
    {
        string rel = Path.GetRelativePath(baseDir, fullPath);
        return rel.Replace('\\', '/');
    }

    private static string ExtensionFromContentType(string contentType)
    {
        return contentType.ToLowerInvariant() switch
        {
            "text/css" => ".css",
            "text/javascript" => ".js",
            "application/javascript" => ".js",
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/apng" => ".apng",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/svg+xml" => ".svg",
            "image/x-icon" => ".ico",
            "image/vnd.microsoft.icon" => ".ico",
            "image/icon" => ".ico",
            _ => string.Empty
        };
    }

    private static string BeautifyHtml(HtmlDocument doc, string originalHtml)
    {
        if (doc.DocumentNode == null)
        {
            return originalHtml;
        }

        string doctypeLine = string.Empty;
        var match = Regex.Match(originalHtml, @"<!doctype[^>]*>", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            doctypeLine = "<!doctype html>";
        }

        var voidTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "area","base","br","col","embed","hr","img","input","link","meta","param","source","track","wbr"
        };

        var sb = new StringBuilder(originalHtml.Length + 1024);

        if (!string.IsNullOrEmpty(doctypeLine))
        {
            sb.AppendLine(doctypeLine);
        }

        foreach (var node in doc.DocumentNode.ChildNodes)
        {
            FormatHtmlNode(node, sb, 0, voidTags);
        }

        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    private static void FormatHtmlNode(HtmlNode node, StringBuilder sb, int indent, HashSet<string> voidTags)
    {
        if (node.NodeType == HtmlNodeType.Text)
        {
            string text = HtmlEntity.DeEntitize(node.InnerText);
            string trimmed = text.Trim();
            if (trimmed.Length == 0)
            {
                return;
            }
            AppendLine(sb, trimmed, indent);
            return;
        }

        if (node.NodeType == HtmlNodeType.Comment)
        {
            return;
        }

        if (node.NodeType != HtmlNodeType.Element)
        {
            return;
        }

        string tagName = node.Name;
        bool isVoid = voidTags.Contains(tagName);

        string openTag = BuildOpeningTag(node);
        AppendLine(sb, openTag, indent);

        if (isVoid)
        {
            return;
        }

        if (tagName.Equals("style", StringComparison.OrdinalIgnoreCase))
        {
            string css = node.InnerHtml ?? string.Empty;
            string prettyCss = BeautifyCss(css);
            if (!string.IsNullOrWhiteSpace(prettyCss))
            {
                foreach (var line in prettyCss.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                {
                    AppendLine(sb, line, indent + 1);
                }
            }
        }
        else if (tagName.Equals("script", StringComparison.OrdinalIgnoreCase))
        {
            string script = node.InnerHtml ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(script))
            {
                foreach (var line in script.Trim().Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
                {
                    AppendLine(sb, line, indent + 1);
                }
            }
        }
        else
        {
            foreach (var child in node.ChildNodes)
            {
                FormatHtmlNode(child, sb, indent + 1, voidTags);
            }
        }

        AppendLine(sb, $"</{tagName}>", indent);
    }

    private static string BuildOpeningTag(HtmlNode node)
    {
        var sb = new StringBuilder();
        sb.Append('<').Append(node.Name);
        foreach (var attr in node.Attributes)
        {
            sb.Append(' ').Append(attr.Name).Append("=\"").Append(attr.Value).Append('\"');
        }
        sb.Append('>');
        return sb.ToString();
    }

    private static void AppendLine(StringBuilder sb, string text, int indent)
    {
        sb.Append(' ', indent * 2);
        sb.AppendLine(text);
    }

    private static string BeautifyCss(string css)
    {
        if (string.IsNullOrWhiteSpace(css))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(css.Length + 256);
        int indent = 0;
        bool inString = false;
        char stringChar = '\0';

        for (int i = 0; i < css.Length; i++)
        {
            char c = css[i];

            if (inString)
            {
                sb.Append(c);
                if (c == stringChar && (i == 0 || css[i - 1] != '\\'))
                {
                    inString = false;
                }
                continue;
            }

            if (c == '"' || c == '\'')
            {
                inString = true;
                stringChar = c;
                sb.Append(c);
                continue;
            }

            switch (c)
            {
                case '{':
                    sb.Append(" {");
                    sb.AppendLine();
                    indent++;
                    sb.Append(' ', indent * 2);
                    break;
                case '}':
                    sb.AppendLine();
                    indent = Math.Max(0, indent - 1);
                    sb.Append(' ', indent * 2);
                    sb.Append('}');
                    sb.AppendLine();
                    if (i + 1 < css.Length && css[i + 1] != '\n')
                    {
                        sb.Append(' ', indent * 2);
                    }
                    break;
                case ';':
                    sb.Append(';');
                    sb.AppendLine();
                    sb.Append(' ', indent * 2);
                    break;
                case '\n':
                case '\r':
                    break;
                default:
                    if (sb.Length > 0 || !char.IsWhiteSpace(c))
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }

        return sb.ToString().Trim();
    }

    private static string ReplaceFirst(string text, string search, string replace)
    {
        int pos = text.IndexOf(search, StringComparison.Ordinal);
        if (pos < 0)
        {
            return text;
        }

        return text.Substring(0, pos) + replace + text.Substring(pos + search.Length);
    }

    private static string DecodeText(byte[] bytes, MediaTypeHeaderValue? contentType)
    {
        try
        {
            string? charset = contentType?.CharSet;
            if (!string.IsNullOrWhiteSpace(charset))
            {
                return Encoding.GetEncoding(charset).GetString(bytes);
            }
        }
        catch
        {
        }

        return Encoding.UTF8.GetString(bytes);
    }
}

static class Config
{
    public const string Token = "bottokenhere";
    public const string Guild = "leaveblankifuwantglobalslashcommands";
}
