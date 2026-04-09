using System;
using System.Web;
using NLog;
using PerfProblemSimulator.App_Start;

namespace PerfProblemSimulator
{
    /// <summary>
    /// ASP.NET Application lifecycle event handler.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>PORTING NOTE:</strong>
    /// In ASP.NET Core, this functionality is handled by:
    /// - Program.cs / Startup.cs for application initialization
    /// - IHostApplicationLifetime for lifecycle events
    /// </para>
    /// <para>
    /// For .NET Framework 4.8 with OWIN, most configuration happens in Startup.cs,
    /// but Global.asax still handles application-level events like error logging.
    /// </para>
    /// </remarks>
    public class Global : HttpApplication
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Application start event - fires once when the application starts.
        /// </summary>
        protected void Application_Start(object sender, EventArgs e)
        {
            Logger.Info("=============================================================================");
            Logger.Info("Performance Problem Simulator - ASP.NET Framework 4.8");
            Logger.Info("Application starting...");
            Logger.Info("=============================================================================");

            // Initialize Application Insights BEFORE OWIN startup and before the first
            // HTTP request. The IIS HTTP modules (ApplicationInsightsHttpModule,
            // TelemetryCorrelationHttpModule) can fire as soon as IIS begins routing
            // requests, so the connection string must already be on
            // TelemetryConfiguration.Active by this point.
            AppInsightsConfig.Initialize();

            // Note: Most configuration is done in Startup.cs (OWIN)
            // This event fires before OWIN startup
        }

        /// <summary>
        /// Application end event - fires once when the application shuts down.
        /// </summary>
        protected void Application_End(object sender, EventArgs e)
        {
            Logger.Info("Performance Problem Simulator shutting down...");
            
            // Flush pending Application Insights telemetry and dispose modules
            AppInsightsConfig.Shutdown();
            
            // Flush any pending log messages
            LogManager.Shutdown();
        }

        /// <summary>
        /// Application error event - fires when an unhandled exception occurs.
        /// </summary>
        protected void Application_Error(object sender, EventArgs e)
        {
            var exception = Server.GetLastError();
            Logger.Error(exception, "Unhandled application error");

            // Exception tracking to Application Insights is handled automatically
            // by the App Service codeless agent.
        }

        /// <summary>
        /// Session start event - fires when a new session is created.
        /// </summary>
        protected void Session_Start(object sender, EventArgs e)
        {
            // Not typically needed for API applications
        }

        /// <summary>
        /// Session end event - fires when a session expires.
        /// </summary>
        protected void Session_End(object sender, EventArgs e)
        {
            // Not typically needed for API applications
        }
    }
}
