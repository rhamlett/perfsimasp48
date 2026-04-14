using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using PerfProblemSimulator.App_Start;

namespace PerfProblemSimulator.Services
{
    /// <summary>
    /// Checks the UI_LANGUAGE setting on startup and generates a translated locale file
    /// if needed. Runs once during application startup before the first request is served.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>FLOW:</strong>
    /// <list type="number">
    /// <item>Read UI_LANGUAGE environment variable (default: "en")</item>
    /// <item>If "en", do nothing — English is the source language</item>
    /// <item>Call TranslationService.EnsureTranslationAsync() to check/generate the locale file</item>
    /// <item>If translation fails (no API key, API error), log a warning — app runs in English</item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>PORTING NOTE from ASP.NET Core:</strong>
    /// In ASP.NET Core, this implements IHostedService and runs automatically.
    /// In .NET Framework 4.8, this is a regular class whose RunAsync() method is called
    /// from Startup.StartBackgroundServices().
    /// </para>
    /// </remarks>
    public class TranslationStartupService
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly ITranslationService _translationService;
        private readonly string _wwwrootPath;

        /// <summary>
        /// HTML documents in wwwroot that should be translated at startup.
        /// </summary>
        private static readonly string[] TranslatableDocuments = new[]
        {
            "documentation.html",
            "azure-monitoring-guide.html",
            "azure-load-testing.html",
            "azure-deployment.html"
        };

        public TranslationStartupService(ITranslationService translationService)
        {
            _translationService = translationService;

            var rootPath = AppDomain.CurrentDomain.BaseDirectory;
            _wwwrootPath = Path.Combine(rootPath, "wwwroot");
            if (!Directory.Exists(_wwwrootPath))
            {
                _wwwrootPath = Path.Combine(rootPath, "bin", "wwwroot");
            }
        }

        /// <summary>
        /// Runs the translation startup logic. Called from Startup.StartBackgroundServices().
        /// </summary>
        public async Task RunAsync(CancellationToken cancellationToken = default)
        {
            var uiLanguage = ConfigurationHelper.UiLanguage;

            if (uiLanguage == "en")
            {
                Logger.Info("UI language is English (default), no translation needed");
                return;
            }

            // Validate ISO 639-1 code (2-3 lowercase letters)
            if (uiLanguage.Length < 2 || uiLanguage.Length > 3 || !uiLanguage.All(char.IsLetter))
            {
                Logger.Warn(
                    "Invalid UI_LANGUAGE value '{0}'. Expected an ISO 639-1 code (e.g., 'es', 'fr', 'ja'). Defaulting to English.",
                    uiLanguage);
                return;
            }

            Logger.Info("UI language set to '{0}', checking for translations...", uiLanguage);

            // Translate dashboard UI strings (en.json → {lang}.json)
            var success = await _translationService.EnsureTranslationAsync(uiLanguage, cancellationToken);

            if (success)
            {
                Logger.Info("UI translation for '{0}' is ready", uiLanguage);
            }
            else
            {
                Logger.Warn(
                    "Failed to ensure UI string translation for '{0}'. " +
                    "The dashboard will fall back to English.",
                    uiLanguage);
            }

            // Translate HTML documentation pages (with inter-document delay to avoid rate limiting)
            var docSuccessCount = 0;
            var isFirstDoc = true;
            foreach (var docFile in TranslatableDocuments)
            {
                var sourcePath = Path.Combine(_wwwrootPath, docFile);
                if (!File.Exists(sourcePath))
                {
                    Logger.Debug("Document {0} not found, skipping translation", docFile);
                    continue;
                }

                // Pause between documents to stay within API rate limits
                if (!isFirstDoc)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                }
                isFirstDoc = false;

                var docSuccess = await _translationService.EnsureDocumentTranslationAsync(
                    sourcePath, uiLanguage, cancellationToken);

                if (docSuccess)
                    docSuccessCount++;
                else
                    Logger.Warn("Failed to translate document {0} to '{1}'", docFile, uiLanguage);
            }

            Logger.Info("Document translation complete: {0}/{1} pages translated to '{2}'",
                docSuccessCount, TranslatableDocuments.Length, uiLanguage);
        }
    }
}
