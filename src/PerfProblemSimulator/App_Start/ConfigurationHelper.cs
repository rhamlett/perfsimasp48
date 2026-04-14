using System;
using System.Configuration;
using PerfProblemSimulator.Models;

namespace PerfProblemSimulator.App_Start
{
    /// <summary>
    /// Helper class for reading application configuration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>PORTING NOTE from ASP.NET Core:</strong>
    /// This replaces IOptions&lt;ProblemSimulatorOptions&gt; pattern.
    /// 
    /// In ASP.NET Core, we used:
    /// - builder.Configuration.GetSection("ProblemSimulator")
    /// - IOptions&lt;T&gt; injection
    /// 
    /// In .NET Framework 4.8, we use:
    /// - ConfigurationManager.AppSettings
    /// - Static helper class
    /// </para>
    /// </remarks>
    public static class ConfigurationHelper
    {
        /// <summary>
        /// Gets the configuration options as a ProblemSimulatorOptions object.
        /// This provides compatibility with code that expects the IOptions pattern.
        /// </summary>
        public static ProblemSimulatorOptions Options
        {
            get
            {
                return new ProblemSimulatorOptions
                {
                    MetricsCollectionIntervalMs = MetricsCollectionIntervalMs,
                    LatencyProbeIntervalMs = LatencyProbeIntervalMs,
                    DisableProblemEndpoints = DisableProblemEndpoints,
                    UiLanguage = UiLanguage,
                    TranslatorApiKey = TranslatorApiKey,
                    TranslatorEndpoint = TranslatorEndpoint,
                    TranslatorRegion = TranslatorRegion
                };
            }
        }

        /// <summary>
        /// Gets the metrics collection interval in milliseconds.
        /// </summary>
        public static int MetricsCollectionIntervalMs
        {
            get
            {
                var value = ConfigurationManager.AppSettings["ProblemSimulator:MetricsCollectionIntervalMs"];
                return int.TryParse(value, out var result) ? result : 250;
            }
        }

        /// <summary>
        /// Gets the latency probe interval in milliseconds.
        /// </summary>
        /// <remarks>
        /// Can be overridden by HEALTH_PROBE_RATE environment variable.
        /// </remarks>
        public static int LatencyProbeIntervalMs
        {
            get
            {
                // First check environment variable
                var envValue = Environment.GetEnvironmentVariable("HEALTH_PROBE_RATE");
                if (!string.IsNullOrEmpty(envValue) && int.TryParse(envValue, out var envResult))
                {
                    return Math.Max(100, envResult); // Minimum 100ms safety limit
                }

                // Then check app settings
                var value = ConfigurationManager.AppSettings["ProblemSimulator:LatencyProbeIntervalMs"];
                if (int.TryParse(value, out var result))
                {
                    return Math.Max(100, result); // Minimum 100ms safety limit
                }

                return 200; // Default
            }
        }

        /// <summary>
        /// Gets whether problem endpoints are disabled.
        /// </summary>
        public static bool DisableProblemEndpoints
        {
            get
            {
                // Check environment variable first
                var envValue = Environment.GetEnvironmentVariable("DISABLE_PROBLEM_ENDPOINTS");
                if (!string.IsNullOrEmpty(envValue))
                {
                    return envValue.Equals("true", StringComparison.OrdinalIgnoreCase);
                }

                // Then check app settings
                var value = ConfigurationManager.AppSettings["DisableProblemEndpoints"];
                return value?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;
            }
        }

        /// <summary>
        /// Gets the idle timeout in minutes.
        /// </summary>
        public static int IdleTimeoutMinutes
        {
            get
            {
                // Check environment variable first
                var envValue = Environment.GetEnvironmentVariable("IDLE_TIMEOUT_MINUTES");
                if (!string.IsNullOrEmpty(envValue) && int.TryParse(envValue, out var envResult))
                {
                    return envResult;
                }

                // Then check app settings
                var value = ConfigurationManager.AppSettings["IdleTimeoutMinutes"];
                return int.TryParse(value, out var result) ? result : 20;
            }
        }

        /// <summary>
        /// Gets the base URL for the application.
        /// </summary>
        /// <remarks>
        /// In Azure App Service, use WEBSITE_HOSTNAME environment variable.
        /// For local development, returns localhost.
        /// </remarks>
        public static string GetBaseUrl()
        {
            // Check for Azure App Service environment
            var websiteHostname = Environment.GetEnvironmentVariable("WEBSITE_HOSTNAME");
            if (!string.IsNullOrEmpty(websiteHostname))
            {
                return $"https://{websiteHostname}";
            }

            // For local development, use HTTP
            return "http://localhost";
        }

        /// <summary>
        /// Gets the App Service SKU if running in Azure.
        /// </summary>
        public static string GetAppServiceSku()
        {
            return Environment.GetEnvironmentVariable("WEBSITE_SKU");
        }

        /// <summary>
        /// Gets the App Service instance ID if running in Azure.
        /// </summary>
        public static string GetInstanceId()
        {
            return Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID");
        }

        /// <summary>
        /// Gets the UI language code (e.g., "en", "es", "ja").
        /// </summary>
        /// <remarks>
        /// Can be configured via environment variable: <c>UI_LANGUAGE</c>
        /// </remarks>
        public static string UiLanguage
        {
            get
            {
                var envValue = Environment.GetEnvironmentVariable("UI_LANGUAGE");
                if (!string.IsNullOrEmpty(envValue))
                {
                    return envValue.Trim().ToLowerInvariant();
                }

                var value = ConfigurationManager.AppSettings["UiLanguage"];
                return !string.IsNullOrEmpty(value) ? value.Trim().ToLowerInvariant() : "en";
            }
        }

        /// <summary>
        /// Gets the Azure Cognitive Services Translator API key.
        /// </summary>
        /// <remarks>
        /// Can be configured via environment variable: <c>TRANSLATOR_API_KEY</c>
        /// </remarks>
        public static string TranslatorApiKey
        {
            get
            {
                var envValue = Environment.GetEnvironmentVariable("TRANSLATOR_API_KEY");
                if (!string.IsNullOrEmpty(envValue))
                {
                    return envValue.Trim();
                }

                return ConfigurationManager.AppSettings["TranslatorApiKey"] ?? "";
            }
        }

        /// <summary>
        /// Gets the Azure Cognitive Services Translator endpoint URL.
        /// </summary>
        /// <remarks>
        /// Can be configured via environment variable: <c>TRANSLATOR_ENDPOINT</c>
        /// </remarks>
        public static string TranslatorEndpoint
        {
            get
            {
                var envValue = Environment.GetEnvironmentVariable("TRANSLATOR_ENDPOINT");
                if (!string.IsNullOrEmpty(envValue))
                {
                    return envValue.Trim().TrimEnd('/');
                }

                var value = ConfigurationManager.AppSettings["TranslatorEndpoint"];
                return !string.IsNullOrEmpty(value)
                    ? value.Trim().TrimEnd('/')
                    : "https://api.cognitive.microsofttranslator.com";
            }
        }

        /// <summary>
        /// Gets the Azure Cognitive Services Translator region.
        /// </summary>
        /// <remarks>
        /// Can be configured via environment variable: <c>TRANSLATOR_REGION</c>
        /// </remarks>
        public static string TranslatorRegion
        {
            get
            {
                var envValue = Environment.GetEnvironmentVariable("TRANSLATOR_REGION");
                if (!string.IsNullOrEmpty(envValue))
                {
                    return envValue.Trim();
                }

                return ConfigurationManager.AppSettings["TranslatorRegion"] ?? "eastus";
            }
        }
    }
}
