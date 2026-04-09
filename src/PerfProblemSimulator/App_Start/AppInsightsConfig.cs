using System;
using NLog;

namespace PerfProblemSimulator.App_Start
{
    /// <summary>
    /// Logs Application Insights configuration status at startup.
    /// 
    /// Auto-collection of HTTP requests, dependencies, and exceptions is handled
    /// by the Azure App Service codeless agent (enabled via Portal → App Service →
    /// Settings → Application Insights). The agent sets the
    /// APPLICATIONINSIGHTS_CONNECTION_STRING environment variable and installs a
    /// CLR profiler that instruments the app without any SDK modules.
    /// 
    /// Custom simulation events (SimulationStarted / SimulationEnded) are sent via
    /// <see cref="Services.SimulationTelemetry"/> using the base
    /// Microsoft.ApplicationInsights TelemetryClient, which reads the same
    /// connection string.
    /// </summary>
    public static class AppInsightsConfig
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Logs whether Application Insights is configured.
        /// Call once in Application_Start for visibility.
        /// </summary>
        public static void Initialize()
        {
            var connectionString = Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING");

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                Logger.Info("Application Insights not configured (APPLICATIONINSIGHTS_CONNECTION_STRING not set). " +
                            "Telemetry is disabled.");
            }
            else
            {
                Logger.Info("Application Insights connection string found. " +
                            "Auto-collection is handled by the App Service codeless agent. " +
                            "Custom simulation events will be sent via TelemetryClient.");
            }
        }

        /// <summary>
        /// Placeholder for shutdown logging. The codeless agent manages its own lifecycle.
        /// </summary>
        public static void Shutdown()
        {
            Logger.Info("Application shutting down. Application Insights agent will flush automatically.");
        }
    }
}
