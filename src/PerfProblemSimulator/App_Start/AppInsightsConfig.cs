using System;
using Microsoft.ApplicationInsights.Extensibility;
using NLog;

namespace PerfProblemSimulator.App_Start
{
    /// <summary>
    /// Sets the Application Insights connection string on <see cref="TelemetryConfiguration.Active"/>.
    /// All telemetry modules (request, dependency, exception, perf counters) are declared in
    /// ApplicationInsights.config and auto-loaded by the SDK when this configuration is first accessed.
    /// Connection string is read from the APPLICATIONINSIGHTS_CONNECTION_STRING environment variable.
    /// </summary>
    public static class AppInsightsConfig
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Sets the connection string on <see cref="TelemetryConfiguration.Active"/>.
        /// Must be called in Application_Start (before OWIN and before any HTTP module fires)
        /// so the config file modules initialize with the correct endpoint.
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
                // Accessing TelemetryConfiguration.Active for the first time triggers the SDK
                // to read ApplicationInsights.config, which declares all telemetry modules,
                // initializers, processors, and the server telemetry channel.
                // We only need to inject the connection string (kept out of the config file
                // so it is never committed to source control).
                TelemetryConfiguration.Active.ConnectionString = connectionString;

                Logger.Info("Application Insights connection string set. " +
                            "Modules from ApplicationInsights.config are active (request, dependency, exception, perf counters).");
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to initialize Application Insights. Auto-tracking will be unavailable.");
            }
        }

        /// <summary>
        /// Flushes pending telemetry. Call during application shutdown.
        /// </summary>
        public static void Shutdown()
        {
            try
            {
                TelemetryConfiguration.Active?.TelemetryChannel?.Flush();
                Logger.Info("Application Insights telemetry flushed.");
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Error during Application Insights shutdown.");
            }
        }
    }
}
