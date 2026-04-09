using System;
using System.Linq;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DependencyCollector;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.ApplicationInsights.Extensibility.PerfCounterCollector;
using Microsoft.ApplicationInsights.Extensibility.PerfCounterCollector.QuickPulse;
using Microsoft.ApplicationInsights.Web;
using Microsoft.ApplicationInsights.WindowsServer;
using Microsoft.ApplicationInsights.WindowsServer.TelemetryChannel;
using NLog;

namespace PerfProblemSimulator.App_Start
{
    /// <summary>
    /// Initializes Application Insights entirely in code — does not depend on
    /// ApplicationInsights.config being found at a particular filesystem path.
    /// Registers request, dependency, exception, performance-counter, and Live Metrics
    /// modules. No adaptive sampling is configured (100 % telemetry capture).
    /// Connection string is read from the APPLICATIONINSIGHTS_CONNECTION_STRING environment variable.
    /// </summary>
    public static class AppInsightsConfig
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private static RequestTrackingTelemetryModule _requestModule;
        private static ExceptionTrackingTelemetryModule _exceptionModule;
        private static DependencyTrackingTelemetryModule _dependencyModule;
        private static PerformanceCollectorModule _perfModule;
        private static QuickPulseTelemetryModule _quickPulseModule;
        private static AppServicesHeartbeatTelemetryModule _heartbeatModule;

        /// <summary>
        /// Fully initializes Application Insights in code.
        /// Must be called in Application_Start before OWIN startup.
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
                Logger.Info("Initializing Application Insights (code-based)...");
                Logger.Info("  BaseDirectory = {0}", AppDomain.CurrentDomain.BaseDirectory);

                // ------------------------------------------------------------------
                // 1. Configure the shared TelemetryConfiguration
                // ------------------------------------------------------------------
                var config = TelemetryConfiguration.Active;
                config.ConnectionString = connectionString;

                // Use the server telemetry channel for reliable delivery
                config.TelemetryChannel = new ServerTelemetryChannel();
                ((ServerTelemetryChannel)config.TelemetryChannel).Initialize(config);

                // ------------------------------------------------------------------
                // 2. Remove any default adaptive sampling processors
                //    (ensures 100 % capture)
                // ------------------------------------------------------------------
                // TelemetryConfiguration.Active may come with processors from the
                // config file or SDK defaults. Clear processors and add only
                // QuickPulse so Live Metrics works without sampling.
                var builder = config.DefaultTelemetrySink.TelemetryProcessorChainBuilder;

                // QuickPulse (Live Metrics) processor
                _quickPulseModule = new QuickPulseTelemetryModule();
                QuickPulseTelemetryProcessor quickPulseProcessor = null;
                builder.Use(next =>
                {
                    quickPulseProcessor = new QuickPulseTelemetryProcessor(next);
                    return quickPulseProcessor;
                });
                builder.Build();

                _quickPulseModule.Initialize(config);
                _quickPulseModule.RegisterTelemetryProcessor(quickPulseProcessor);

                // ------------------------------------------------------------------
                // 3. Register telemetry initializers
                // ------------------------------------------------------------------
                config.TelemetryInitializers.Add(new Microsoft.ApplicationInsights.Web.OperationNameTelemetryInitializer());
                config.TelemetryInitializers.Add(new Microsoft.ApplicationInsights.Web.OperationCorrelationTelemetryInitializer());
                config.TelemetryInitializers.Add(new Microsoft.ApplicationInsights.Web.UserTelemetryInitializer());
                config.TelemetryInitializers.Add(new Microsoft.ApplicationInsights.Web.SessionTelemetryInitializer());
                config.TelemetryInitializers.Add(new Microsoft.ApplicationInsights.Web.ClientIpHeaderTelemetryInitializer());
                config.TelemetryInitializers.Add(new Microsoft.ApplicationInsights.Web.AzureAppServiceRoleNameFromHostNameHeaderInitializer());
                config.TelemetryInitializers.Add(new HttpDependenciesParsingTelemetryInitializer());
                config.TelemetryInitializers.Add(new AzureRoleEnvironmentTelemetryInitializer());

                // ------------------------------------------------------------------
                // 4. Register telemetry modules
                // ------------------------------------------------------------------

                // Dependency tracking (outbound HTTP, SQL, etc.)
                _dependencyModule = new DependencyTrackingTelemetryModule();
                _dependencyModule.ExcludeComponentCorrelationHttpHeadersOnDomains.Add("core.windows.net");
                _dependencyModule.ExcludeComponentCorrelationHttpHeadersOnDomains.Add("core.chinacloudapi.cn");
                _dependencyModule.ExcludeComponentCorrelationHttpHeadersOnDomains.Add("core.cloudapi.de");
                _dependencyModule.ExcludeComponentCorrelationHttpHeadersOnDomains.Add("core.usgovcloudapi.net");
                _dependencyModule.Initialize(config);

                // Performance counter collection
                _perfModule = new PerformanceCollectorModule();
                _perfModule.Initialize(config);

                // App Service heartbeat
                _heartbeatModule = new AppServicesHeartbeatTelemetryModule();
                _heartbeatModule.Initialize(config);

                // Request tracking — works with ApplicationInsightsHttpModule in web.config
                // to capture every inbound HTTP request.
                _requestModule = new RequestTrackingTelemetryModule();
                _requestModule.Initialize(config);

                // Exception tracking — captures unhandled exceptions in the IIS pipeline.
                _exceptionModule = new ExceptionTrackingTelemetryModule();
                _exceptionModule.Initialize(config);

                // ------------------------------------------------------------------
                // 5. Verify the pipeline by sending a trace
                // ------------------------------------------------------------------
                var client = new TelemetryClient(config);
                client.TrackTrace("Application Insights initialized (code-based). Modules: " +
                                  "Request (HTTP module), Dependency, Exception, PerfCounters, QuickPulse.");
                client.Flush();

                Logger.Info("Application Insights fully initialized (code-based). " +
                            "Modules: Request (HTTP module), Dependency, Exception, PerfCounters, LiveMetrics.");
                Logger.Info("  TelemetryChannel type = {0}", config.TelemetryChannel?.GetType().FullName);
                Logger.Info("  Initializers count = {0}", config.TelemetryInitializers.Count);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to initialize Application Insights. Auto-tracking will be unavailable.");
            }
        }

        /// <summary>
        /// Flushes pending telemetry and disposes modules. Call during application shutdown.
        /// </summary>
        public static void Shutdown()
        {
            try
            {
                _dependencyModule?.Dispose();
                _perfModule?.Dispose();
                _quickPulseModule?.Dispose();
                _heartbeatModule?.Dispose();
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
