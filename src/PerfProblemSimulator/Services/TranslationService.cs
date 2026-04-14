using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using PerfProblemSimulator.App_Start;

namespace PerfProblemSimulator.Services
{
    /// <summary>
    /// Translates UI strings from English to a target language using Azure Cognitive Services Translator.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>HOW IT WORKS:</strong>
    /// <list type="number">
    /// <item>Reads the master en.json file from wwwroot/locales/</item>
    /// <item>Computes a SHA256 hash of the English source</item>
    /// <item>Checks if a cached translation file exists with a matching hash</item>
    /// <item>If not, calls Azure Translator API to translate all strings</item>
    /// <item>Writes the translated file to wwwroot/locales/{lang}.json</item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>NEVER-TRANSLATE TERMS:</strong>
    /// Technical terms listed in no-translate.json are wrapped in
    /// &lt;span class="notranslate"&gt; tags before sending to the API,
    /// then the tags are stripped from the translated output.
    /// </para>
    /// <para>
    /// <strong>PORTING NOTE from ASP.NET Core:</strong>
    /// - Uses HttpClient directly instead of IHttpClientFactory
    /// - Uses NLog instead of ILogger&lt;T&gt;
    /// - Uses ConfigurationHelper instead of IOptions&lt;T&gt;
    /// - Uses Newtonsoft.Json instead of System.Text.Json
    /// - No [GeneratedRegex] source generators — uses compiled Regex instances
    /// - No SHA256.HashData() — uses SHA256.Create().ComputeHash()
    /// </para>
    /// </remarks>
    public class TranslationService : ITranslationService
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private static readonly HttpClient HttpClient = new HttpClient();
        private readonly string _wwwrootPath;
        private readonly string _sourceWwwrootPath;

        /// <summary>
        /// Maximum number of text elements per Azure Translator API call.
        /// The API supports up to 1,000 elements per request.
        /// </summary>
        private const int MaxBatchSize = 100;

        /// <summary>
        /// Maximum total characters per Azure Translator API request.
        /// The API enforces a 50,000-character limit across all elements in a single request.
        /// </summary>
        private const int MaxBatchChars = 49_000; // Leave margin below the 50K hard limit

        public TranslationService()
        {
            var rootPath = AppDomain.CurrentDomain.BaseDirectory;
            _wwwrootPath = Path.Combine(rootPath, "wwwroot");
            if (!Directory.Exists(_wwwrootPath))
            {
                _wwwrootPath = Path.Combine(rootPath, "bin", "wwwroot");
            }

            // Find the source project's wwwroot so translated files are written
            // to the source tree (for git). In Azure, this is null (no source tree).
            _sourceWwwrootPath = FindSourceWwwrootPath();
            if (_sourceWwwrootPath != null)
            {
                Logger.Info("Source wwwroot found at: {0}", _sourceWwwrootPath);
            }
        }

        /// <summary>
        /// Walks up from the build output directory to find the source project's wwwroot.
        /// Returns null when deployed to Azure (no source tree present).
        /// </summary>
        private static string FindSourceWwwrootPath()
        {
            try
            {
                var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
                while (dir != null)
                {
                    var candidate = Path.Combine(dir.FullName, "src", "PerfProblemSimulator", "wwwroot");
                    if (Directory.Exists(candidate))
                        return candidate;
                    dir = dir.Parent;
                }
            }
            catch
            {
                // Ignore — fall back to runtime-only writes
            }

            return null;
        }

        /// <summary>
        /// Writes a file to the runtime wwwroot and also to the source project wwwroot
        /// (if found) so translated files appear in the source tree for git.
        /// </summary>
        private void WriteToWwwrootAndSource(string runtimeFilePath, string content)
        {
            // Always write to runtime wwwroot (so the running app serves it)
            File.WriteAllText(runtimeFilePath, content, Encoding.UTF8);

            // Also write to source project wwwroot if available
            if (_sourceWwwrootPath != null)
            {
                // Compute the relative path from runtime wwwroot to the file
                var relativePath = runtimeFilePath.Substring(_wwwrootPath.Length).TrimStart(Path.DirectorySeparatorChar, '/');
                var sourcePath = Path.Combine(_sourceWwwrootPath, relativePath);
                var sourceDir = Path.GetDirectoryName(sourcePath);
                if (sourceDir != null && !Directory.Exists(sourceDir))
                    Directory.CreateDirectory(sourceDir);
                File.WriteAllText(sourcePath, content, Encoding.UTF8);
                Logger.Info("Also written to source project: {0}", sourcePath);
            }
        }

        /// <summary>
        /// When the hash check passes (translation is up to date), ensures the
        /// source project wwwroot also has a copy of the file. This handles the
        /// case where the file exists only in the build output from a prior run.
        /// </summary>
        private void EnsureSourceCopy(string runtimeFilePath, string content = null)
        {
            if (_sourceWwwrootPath == null) return;

            var relativePath = runtimeFilePath.Substring(_wwwrootPath.Length)
                .TrimStart(Path.DirectorySeparatorChar, '/');
            var sourcePath = Path.Combine(_sourceWwwrootPath, relativePath);

            if (File.Exists(sourcePath)) return;

            // Read from the runtime file if content not provided
            if (content == null)
                content = File.ReadAllText(runtimeFilePath, Encoding.UTF8);

            var sourceDir = Path.GetDirectoryName(sourcePath);
            if (sourceDir != null && !Directory.Exists(sourceDir))
                Directory.CreateDirectory(sourceDir);

            File.WriteAllText(sourcePath, content, Encoding.UTF8);
            Logger.Info("Copied cached translation to source project: {0}", sourcePath);
        }

        public async Task<bool> EnsureTranslationAsync(string targetLanguage, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(targetLanguage) || targetLanguage.Equals("en", StringComparison.OrdinalIgnoreCase))
            {
                return true; // English is the source language, no translation needed
            }

            var localesPath = Path.Combine(_wwwrootPath, "locales");
            var enFilePath = Path.Combine(localesPath, "en.json");
            var targetFilePath = Path.Combine(localesPath, $"{targetLanguage}.json");

            // Read and parse the English source
            if (!File.Exists(enFilePath))
            {
                Logger.Error("English source file not found: {0}", enFilePath);
                return false;
            }

            var enContent = File.ReadAllText(enFilePath, Encoding.UTF8);
            var sourceHash = ComputeHash(enContent);

            // Check if a cached translation already exists with matching hash
            if (File.Exists(targetFilePath))
            {
                try
                {
                    var existingContent = File.ReadAllText(targetFilePath, Encoding.UTF8);
                    var existingObj = JObject.Parse(existingContent);
                    var meta = existingObj["_meta"] as JObject;
                    if (meta != null)
                    {
                        var hashValue = meta["source_hash"]?.ToString();
                        if (hashValue == sourceHash)
                        {
                            Logger.Info("Translation for {0} is up to date (hash: {1})",
                                targetLanguage, sourceHash.Substring(0, 8));
                            EnsureSourceCopy(targetFilePath, existingContent);
                            return true;
                        }
                    }

                    Logger.Info("Translation for {0} exists but source has changed, re-translating",
                        targetLanguage);
                }
                catch (JsonException)
                {
                    Logger.Warn("Existing translation file for {0} is invalid, re-translating", targetLanguage);
                }
            }

            // Get translator configuration
            var translatorKey = ConfigurationHelper.TranslatorApiKey;
            var translatorEndpoint = ConfigurationHelper.TranslatorEndpoint;
            var translatorRegion = ConfigurationHelper.TranslatorRegion;

            if (string.IsNullOrWhiteSpace(translatorKey))
            {
                Logger.Warn(
                    "UI_LANGUAGE is set to '{0}' but TranslatorApiKey is not configured. " +
                    "Translation cannot proceed. Set TRANSLATOR_API_KEY environment variable or " +
                    "TranslatorApiKey in AppSettings to enable auto-translation.",
                    targetLanguage);
                return false;
            }

            // Parse English strings (skip _meta)
            var enObj = JObject.Parse(enContent);
            var sourceStrings = new Dictionary<string, string>();
            foreach (var prop in enObj.Properties())
            {
                if (prop.Name == "_meta") continue;
                if (prop.Value.Type == JTokenType.String)
                {
                    sourceStrings[prop.Name] = prop.Value.ToString();
                }
            }

            if (sourceStrings.Count == 0)
            {
                Logger.Warn("No translatable strings found in en.json");
                return false;
            }

            // Load no-translate terms
            var noTranslateTerms = LoadNoTranslateTerms(localesPath);

            Logger.Info("Translating {0} strings to {1} ({2} protected terms)...",
                sourceStrings.Count, targetLanguage, noTranslateTerms.Count);

            // Translate in batches
            var translatedStrings = new Dictionary<string, string>();
            var keys = sourceStrings.Keys.ToList();

            for (var i = 0; i < keys.Count; i += MaxBatchSize)
            {
                var batchKeys = keys.Skip(i).Take(MaxBatchSize).ToList();
                var batchTexts = batchKeys.Select(k => WrapNoTranslateTerms(sourceStrings[k], noTranslateTerms)).ToList();

                var translations = await TranslateBatchAsync(
                    batchTexts, targetLanguage, translatorKey, translatorEndpoint, translatorRegion, cancellationToken);

                if (translations == null)
                {
                    Logger.Error("Translation API call failed for batch starting at index {0}", i);
                    return false;
                }

                for (var j = 0; j < batchKeys.Count; j++)
                {
                    translatedStrings[batchKeys[j]] = StripNoTranslateTags(translations[j]);
                }
            }

            // Build output JSON
            var output = new JObject
            {
                ["_meta"] = new JObject
                {
                    ["source_hash"] = sourceHash,
                    ["source_lang"] = "en",
                    ["target_lang"] = targetLanguage,
                    ["generated"] = DateTime.UtcNow.ToString("o"),
                    ["generator"] = "Azure Cognitive Services Translator"
                }
            };

            foreach (var kvp in translatedStrings)
            {
                output[kvp.Key] = kvp.Value;
            }

            var outputJson = output.ToString(Formatting.Indented);
            WriteToWwwrootAndSource(targetFilePath, outputJson);

            Logger.Info("Translation complete: {0} strings written to {1}",
                translatedStrings.Count, targetFilePath);

            return true;
        }

        /// <summary>
        /// Calls the Azure Translator API to translate a batch of strings.
        /// Retries up to 3 times on 429 (rate limit) with exponential backoff.
        /// </summary>
        private async Task<List<string>> TranslateBatchAsync(
            List<string> texts,
            string targetLanguage,
            string apiKey,
            string endpoint,
            string region,
            CancellationToken cancellationToken)
        {
            var requestBody = texts.Select(t => new { Text = t }).ToList();
            var requestJson = JsonConvert.SerializeObject(requestBody);

            var requestUrl = $"{endpoint}/translate?api-version=3.0&from=en&to={Uri.EscapeDataString(targetLanguage)}&textType=html";

            var retryDelays = new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60) };

            for (var attempt = 0; attempt <= retryDelays.Length; attempt++)
            {
                try
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Post, requestUrl))
                    {
                        request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");
                        request.Headers.Add("Ocp-Apim-Subscription-Key", apiKey);
                        request.Headers.Add("Ocp-Apim-Subscription-Region", region);

                        using (var response = await HttpClient.SendAsync(request, cancellationToken))
                        {
                            if (!response.IsSuccessStatusCode)
                            {
                                var errorBody = await response.Content.ReadAsStringAsync();
                                var statusCode = (int)response.StatusCode;

                                // Retry on 429 (rate limit)
                                if (statusCode == 429 && attempt < retryDelays.Length)
                                {
                                    var delay = retryDelays[attempt];
                                    if (response.Headers.RetryAfter?.Delta != null)
                                    {
                                        delay = response.Headers.RetryAfter.Delta.Value;
                                    }

                                    Logger.Warn(
                                        "Translator API rate limited (attempt {0}/{1}). Retrying in {2}s...",
                                        attempt + 1, retryDelays.Length + 1, delay.TotalSeconds);

                                    await Task.Delay(delay, cancellationToken);
                                    continue;
                                }

                                Logger.Error("Azure Translator API returned {0}: {1}",
                                    response.StatusCode, errorBody);
                                return null;
                            }

                            var responseJson = await response.Content.ReadAsStringAsync();
                            var responseArray = JArray.Parse(responseJson);

                            var results = new List<string>();
                            foreach (var item in responseArray)
                            {
                                var translations = item["translations"];
                                var firstTranslation = translations[0]["text"].ToString();
                                results.Add(firstTranslation);
                            }

                            return results;
                        }
                    }
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    Logger.Error(ex, "Failed to call Azure Translator API");
                    return null;
                }
            }

            return null;
        }

        /// <summary>
        /// Loads the no-translate terms list from no-translate.json.
        /// </summary>
        private static List<string> LoadNoTranslateTerms(string localesPath)
        {
            var noTranslatePath = Path.Combine(localesPath, "no-translate.json");
            if (!File.Exists(noTranslatePath))
            {
                return new List<string>();
            }

            try
            {
                var content = File.ReadAllText(noTranslatePath, Encoding.UTF8);
                var obj = JObject.Parse(content);
                var termsArray = obj["terms"] as JArray;
                if (termsArray != null)
                {
                    return termsArray
                        .Select(t => t.ToString())
                        .Where(t => !string.IsNullOrEmpty(t))
                        .OrderByDescending(t => t.Length) // Longest first to avoid partial matches
                        .ToList();
                }
            }
            catch (Exception)
            {
                // Ignore malformed file
            }

            return new List<string>();
        }

        /// <summary>
        /// Wraps no-translate terms and {placeholder} tokens in notranslate spans.
        /// Terms are processed longest-first to avoid partial matches.
        /// </summary>
        private static string WrapNoTranslateTerms(string text, List<string> terms)
        {
            // Wrap {placeholder} tokens first
            text = PlaceholderRegex.Replace(text, "<span class=\"notranslate\">$0</span>");

            if (terms.Count == 0) return text;

            foreach (var term in terms)
            {
                var pattern = $@"(?<![a-zA-Z]){Regex.Escape(term)}(?![a-zA-Z])";
                text = Regex.Replace(text, pattern, $"<span class=\"notranslate\">{term}</span>");
            }

            return text;
        }

        /// <summary>Matches {placeholder} tokens used by the i18n system.</summary>
        private static readonly Regex PlaceholderRegex = new Regex(
            @"\{[a-zA-Z_][a-zA-Z0-9_]*\}",
            RegexOptions.Compiled);

        /// <summary>
        /// Strips notranslate span tags from translated text.
        /// Handles both straight quotes and HTML-entity quotes (&amp;quot;).
        /// </summary>
        private static string StripNoTranslateTags(string text)
        {
            return NotranslateSpanRegex.Replace(text, "$1");
        }

        private static readonly Regex NotranslateSpanRegex = new Regex(
            @"<span\s+class\s*=\s*(?:""|&quot;|')notranslate(?:""|&quot;|')>(.*?)</span>",
            RegexOptions.Compiled | RegexOptions.Singleline);

        /// <summary>
        /// Computes a SHA256 hash of the input string (first 16 hex chars for brevity).
        /// </summary>
        private static string ComputeHash(string input)
        {
            using (var sha256 = SHA256.Create())
            {
                var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
                return BitConverter.ToString(hashBytes).Replace("-", "").Substring(0, 16).ToLowerInvariant();
            }
        }

        /// <summary>
        /// Gets the path for a translated HTML document.
        /// For "documentation.html" with language "es", returns "documentation.es.html".
        /// </summary>
        private static string GetTranslatedHtmlPath(string sourceHtmlPath, string targetLanguage)
        {
            var dir = Path.GetDirectoryName(sourceHtmlPath);
            var nameWithoutExt = Path.GetFileNameWithoutExtension(sourceHtmlPath);
            var ext = Path.GetExtension(sourceHtmlPath);
            return Path.Combine(dir, $"{nameWithoutExt}.{targetLanguage}{ext}");
        }

        public async Task<bool> EnsureDocumentTranslationAsync(
            string sourceHtmlPath, string targetLanguage, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(targetLanguage) || targetLanguage.Equals("en", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!File.Exists(sourceHtmlPath))
            {
                Logger.Error("HTML source file not found: {0}", sourceHtmlPath);
                return false;
            }

            var sourceContent = File.ReadAllText(sourceHtmlPath, Encoding.UTF8);
            var sourceHash = ComputeHash(sourceContent);
            var targetPath = GetTranslatedHtmlPath(sourceHtmlPath, targetLanguage);
            var sourceFileName = Path.GetFileName(sourceHtmlPath);

            // Check cache: look for hash comment at the top of the translated file
            if (File.Exists(targetPath))
            {
                var firstLine = File.ReadLines(targetPath).FirstOrDefault() ?? "";
                if (firstLine.Contains($"source_hash:{sourceHash}"))
                {
                    Logger.Info("Document translation for {0} ({1}) is up to date (hash: {2})",
                        sourceFileName, targetLanguage, sourceHash.Substring(0, 8));
                    EnsureSourceCopy(targetPath);
                    return true;
                }

                Logger.Info("Document translation for {0} ({1}) exists but source changed, re-translating",
                    sourceFileName, targetLanguage);
            }

            // Get translator configuration
            var translatorKey = ConfigurationHelper.TranslatorApiKey;
            var translatorEndpoint = ConfigurationHelper.TranslatorEndpoint;
            var translatorRegion = ConfigurationHelper.TranslatorRegion;

            if (string.IsNullOrWhiteSpace(translatorKey))
            {
                Logger.Warn("Cannot translate {0} to '{1}' — TranslatorApiKey is not configured.",
                    sourceFileName, targetLanguage);
                return false;
            }

            // Load no-translate terms
            var localesPath = Path.Combine(_wwwrootPath, "locales");
            var noTranslateTerms = LoadNoTranslateTerms(localesPath);

            // Extract translatable text segments from the HTML
            var segments = ExtractTranslatableSegments(sourceContent);
            var translatableSegments = segments.Where(s => s.IsTranslatable && !string.IsNullOrWhiteSpace(s.Text)).ToList();

            if (translatableSegments.Count == 0)
            {
                Logger.Warn("No translatable text found in {0}", sourceFileName);
                return false;
            }

            Logger.Info("Translating document {0} to {1}: {2} text segments...",
                sourceFileName, targetLanguage, translatableSegments.Count);

            // Translate in batches
            var batchIndex = 0;
            var i = 0;
            while (i < translatableSegments.Count)
            {
                var batch = new List<HtmlSegment>();
                var batchTexts = new List<string>();
                var batchCharCount = 0;

                while (i < translatableSegments.Count && batch.Count < MaxBatchSize)
                {
                    var wrapped = WrapNoTranslateTerms(translatableSegments[i].Text, noTranslateTerms);
                    if (batchCharCount + wrapped.Length > MaxBatchChars && batch.Count > 0)
                        break;

                    batch.Add(translatableSegments[i]);
                    batchTexts.Add(wrapped);
                    batchCharCount += wrapped.Length;
                    i++;
                }

                var translations = await TranslateBatchAsync(
                    batchTexts, targetLanguage, translatorKey, translatorEndpoint, translatorRegion, cancellationToken);

                if (translations == null)
                {
                    Logger.Error("Document translation API call failed for {0} at batch {1}",
                        sourceFileName, batchIndex);
                    return false;
                }

                for (var j = 0; j < batch.Count; j++)
                {
                    batch[j].TranslatedText = StripNoTranslateTags(translations[j]);
                }

                batchIndex++;

                // Pause between batches to avoid hitting the text API rate limit (429)
                if (i < translatableSegments.Count)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                }
            }

            // Reassemble the translated HTML
            var sb = new StringBuilder();
            sb.AppendLine($"<!-- source_hash:{sourceHash} lang:{targetLanguage} generated:{DateTime.UtcNow:o} -->");
            foreach (var segment in segments)
            {
                sb.Append(segment.IsTranslatable && segment.TranslatedText != null
                    ? segment.TranslatedText
                    : segment.Text);
            }

            WriteToWwwrootAndSource(targetPath, sb.ToString());

            Logger.Info("Document translation complete: {0} → {1} ({2} segments)",
                sourceFileName, Path.GetFileName(targetPath), translatableSegments.Count);

            return true;
        }

        /// <summary>
        /// Splits an HTML document into a list of segments: translatable text and non-translatable
        /// markup (tags, code blocks, scripts, styles, SVGs). Preserves document structure exactly.
        /// </summary>
        private static List<HtmlSegment> ExtractTranslatableSegments(string html)
        {
            var segments = new List<HtmlSegment>();
            var parts = HtmlTagRegex.Split(html);

            // Track whether we're inside a no-translate element (code, pre, script, style, svg)
            var noTranslateDepth = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var insideNoTranslate = false;

            foreach (var part in parts)
            {
                if (string.IsNullOrEmpty(part)) continue;

                if (part.StartsWith("<"))
                {
                    // This is an HTML tag — never translate it
                    segments.Add(new HtmlSegment { Text = part, IsTranslatable = false });

                    // Track entering/exiting no-translate elements
                    var openMatch = NoTranslateElementOpenRegex.Match(part);
                    if (openMatch.Success)
                    {
                        var tagName = openMatch.Groups[1].Value.ToLowerInvariant();
                        if (!noTranslateDepth.ContainsKey(tagName))
                            noTranslateDepth[tagName] = 0;
                        noTranslateDepth[tagName]++;
                        insideNoTranslate = true;
                    }
                    else if (part.StartsWith("</"))
                    {
                        // Closing tag — extract tag name
                        var closingTag = part.Substring(2).TrimEnd('>', ' ').ToLowerInvariant();
                        if (noTranslateDepth.ContainsKey(closingTag))
                        {
                            noTranslateDepth[closingTag]--;
                            if (noTranslateDepth[closingTag] <= 0)
                                noTranslateDepth.Remove(closingTag);
                            insideNoTranslate = noTranslateDepth.Values.Any(v => v > 0);
                        }
                    }
                }
                else
                {
                    // This is text content
                    var shouldTranslate = !insideNoTranslate && part.Trim().Length > 0;
                    segments.Add(new HtmlSegment { Text = part, IsTranslatable = shouldTranslate });
                }
            }

            return segments;
        }

        private static readonly Regex HtmlTagRegex = new Regex(
            @"(<[^>]+>)",
            RegexOptions.Compiled);

        private static readonly Regex NoTranslateElementOpenRegex = new Regex(
            @"<(code|pre|script|style|svg)[\s>]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Represents a segment of an HTML document — either translatable text or non-translatable markup.
        /// </summary>
        private class HtmlSegment
        {
            public string Text { get; set; }
            public bool IsTranslatable { get; set; }
            public string TranslatedText { get; set; }
        }
    }
}
