using System;
using Microsoft.ApplicationInsights.DependencyCollector;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.ApplicationInsights.Extensibility.PerfCounterCollector;
using NLog;

namespace PerfProblemSimulator.App_Start
{
    /// <summary>
    /// Initializes Application Insights with full telemetry:
    /// automatic HTTP request tracking (via web.config HTTP module),
    /// outbound dependency tracking, exception tracking, and performance counters.
    /// Connection string is read from the APPLICATIONINSIGHTS_CONNECTION_STRING environment variable.
    /// </summary>
    public static class AppInsightsConfig
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private static DependencyTrackingTelemetryModule _dependencyModule;
        private static PerformanceCollectorModule _perfModule;

        /// <summary>
        /// Initializes Application Insights telemetry. Call once at application startup
        /// before any HTTP requests are processed.
        /// </summary>
        public static void Initialize()
        {
            var connectionString = Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING");

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                Logger.Info("Application Insights not configured (APPLICATIONINSIGHTS_CONNECTION_STRING not set). " +
                            "Auto-tracking for requests, dependencies, and exceptions is disabled.");
                return;
            }

            try
            {
                // Configure the shared/active TelemetryConfiguration used by auto-collectors and HTTP modules.
                // The Web HTTP module (registered in web.config) uses TelemetryConfiguration.Active
                // to report request and exception telemetry.
                var config = TelemetryConfiguration.Active;
                config.ConnectionString = connectionString;

                // Disable adaptive sampling — user requires 100% telemetry capture.
                // By default, the Web SDK enables adaptive sampling which drops telemetry
                // under load. Setting no sampling processor ensures every request is recorded.
                // (TelemetryConfiguration.Active has no sampling processors by default
                //  when configured in code, so nothing to remove.)

                // Initialize outbound dependency tracking (HTTP calls, SQL, etc.)
                _dependencyModule = new DependencyTrackingTelemetryModule();
                _dependencyModule.Initialize(config);

                // Initialize performance counter collection
                _perfModule = new PerformanceCollectorModule();
                _perfModule.Initialize(config);

                Logger.Info("Application Insights fully initialized — request tracking, dependency tracking, " +
                            "exception tracking, and performance counters are active.");
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to initialize Application Insights. Auto-tracking will be unavailable.");
            }
        }

        /// <summary>
        /// Shuts down Application Insights modules and flushes pending telemetry.
        /// Call during application shutdown.
        /// </summary>
        public static void Shutdown()
        {
            try
            {
                _dependencyModule?.Dispose();
                _perfModule?.Dispose();
                TelemetryConfiguration.Active?.TelemetryChannel?.Flush();
                Logger.Info("Application Insights telemetry flushed and shut down.");
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Error during Application Insights shutdown.");
            }
        }
    }
}
