using Microsoft.AspNet.SignalR;
using Microsoft.Owin;
using Microsoft.Owin.Cors;
using Microsoft.Owin.FileSystems;
using Microsoft.Owin.StaticFiles;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using NLog;
using Owin;
using PerfProblemSimulator.App_Start;
using System;
using System.IO;
using System.Web.Http;
using Unity;

[assembly: OwinStartup(typeof(PerfProblemSimulator.Startup))]

namespace PerfProblemSimulator
{
    /// <summary>
    /// OWIN Startup class - configures the HTTP pipeline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>PORTING NOTE from ASP.NET Core:</strong>
    /// This class replaces Program.cs / builder pattern from .NET Core.
    /// 
    /// Key differences:
    /// - ASP.NET Core: var builder = WebApplication.CreateBuilder(args);
    /// - .NET Framework 4.8: OWIN IAppBuilder in Startup.Configuration()
    /// 
    /// Middleware order matters just like in ASP.NET Core!
    /// </para>
    /// </remarks>
    public class Startup
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Configures the OWIN application pipeline.
        /// </summary>
        /// <param name="app">The OWIN app builder.</param>
        public void Configuration(IAppBuilder app)
        {
            Logger.Info("Configuring OWIN pipeline...");

            // -----------------------------------------------------------------------------
            // CORS Configuration (must be before SignalR and Web API)
            // -----------------------------------------------------------------------------
            // Allow any origin for development. In production, restrict this.
            app.UseCors(CorsOptions.AllowAll);

            // -----------------------------------------------------------------------------
            // Static Files (wwwroot)
            // -----------------------------------------------------------------------------
            ConfigureStaticFiles(app);

            // -----------------------------------------------------------------------------
            // SignalR Configuration
            // -----------------------------------------------------------------------------
            ConfigureSignalR(app);

            // -----------------------------------------------------------------------------
            // Web API Configuration
            // -----------------------------------------------------------------------------
            ConfigureWebApi(app);

            // -----------------------------------------------------------------------------
            // Start Background Services
            // -----------------------------------------------------------------------------
            StartBackgroundServices();

            Logger.Info("OWIN pipeline configured successfully");
            Logger.Info("Problem endpoints are {0}",
                ConfigurationHelper.DisableProblemEndpoints ? "DISABLED" : "ENABLED");
        }

        /// <summary>
        /// Configures static file serving from wwwroot.
        /// </summary>
        private void ConfigureStaticFiles(IAppBuilder app)
        {
            // Get the wwwroot path
            var rootPath = AppDomain.CurrentDomain.BaseDirectory;
            var wwwrootPath = Path.Combine(rootPath, "wwwroot");

            if (!Directory.Exists(wwwrootPath))
            {
                // Try bin folder (for development)
                wwwrootPath = Path.Combine(rootPath, "bin", "wwwroot");
            }

            if (Directory.Exists(wwwrootPath))
            {
                var physicalFileSystem = new PhysicalFileSystem(wwwrootPath);

                var options = new FileServerOptions
                {
                    EnableDefaultFiles = true,
                    FileSystem = physicalFileSystem
                };

                options.DefaultFilesOptions.DefaultFileNames.Clear();
                options.DefaultFilesOptions.DefaultFileNames.Add("index.html");

                app.UseFileServer(options);
                Logger.Info("Static files configured from: {0}", wwwrootPath);
            }
            else
            {
                Logger.Warn("wwwroot folder not found at: {0}", wwwrootPath);
            }
        }

        /// <summary>
        /// Configures SignalR hub mapping.
        /// </summary>
        private void ConfigureSignalR(IAppBuilder app)
        {
            // NOTE: Do NOT override SignalR's default JSON serializer with camelCase.
            // SignalR's internal protocol messages (negotiate, connect, etc.) expect 
            // PascalCase property names like "ProtocolVersion", not "protocolVersion".
            // Hub method payloads can use any serialization, but protocol messages cannot.

            // Configure SignalR timeouts to prevent disconnection during idle periods
            // DisconnectTimeout: How long server waits after connection is lost before firing OnDisconnected
            // KeepAlive: How often server sends keep-alive to client (must be <= DisconnectTimeout/3)
            // ConnectionTimeout: How long to wait during initial connection negotiation
            GlobalHost.Configuration.DisconnectTimeout = TimeSpan.FromHours(2);
            GlobalHost.Configuration.KeepAlive = TimeSpan.FromMinutes(30);
            GlobalHost.Configuration.ConnectionTimeout = TimeSpan.FromMinutes(2);

            // Configure SignalR
            var hubConfiguration = new HubConfiguration
            {
                EnableDetailedErrors = true, // Enable for debugging; disable in production
                EnableJSONP = false
            };

            // Set up dependency injection for SignalR
            UnityConfig.ConfigureSignalR(GlobalHost.DependencyResolver);

            // Map SignalR hubs to /hubs/metrics path (matching ASP.NET Core route)
            app.Map("/hubs/metrics", map =>
            {
                map.RunSignalR(hubConfiguration);
            });

            Logger.Info("SignalR configured at /hubs/metrics with extended timeouts (DisconnectTimeout: 2h, KeepAlive: 30m)");
        }

        /// <summary>
        /// Configures Web API controllers and routes.
        /// </summary>
        private void ConfigureWebApi(IAppBuilder app)
        {
            var config = new HttpConfiguration();

            // Configure JSON serialization to match ASP.NET Core behavior
            var jsonSettings = config.Formatters.JsonFormatter.SerializerSettings;
            jsonSettings.ContractResolver = new CamelCasePropertyNamesContractResolver();
            jsonSettings.NullValueHandling = NullValueHandling.Ignore;
            jsonSettings.Converters.Add(new Newtonsoft.Json.Converters.StringEnumConverter());

            // Configure attribute routing
            config.MapHttpAttributeRoutes();

            // Configure conventional routing as fallback
            config.Routes.MapHttpRoute(
                name: "DefaultApi",
                routeTemplate: "api/{controller}/{id}",
                defaults: new { id = RouteParameter.Optional }
            );

            // Configure dependency injection
            UnityConfig.ConfigureWebApi(config);

            // Configure Swagger
            SwaggerConfig.Register(config);

            // Use Web API in OWIN pipeline
            app.UseWebApi(config);

            Logger.Info("Web API configured");
        }

        /// <summary>
        /// Starts background services that need to run throughout the application lifetime.
        /// </summary>
        private void StartBackgroundServices()
        {
            // Start the metrics collector
            var metricsCollector = UnityConfig.Container.Resolve<Services.IMetricsCollector>();
            if (metricsCollector is Services.MetricsCollector collector)
            {
                collector.Start();
                Logger.Info("MetricsCollector started");
            }

            // Start the metrics broadcast service
            var broadcastService = UnityConfig.Container.Resolve<Services.MetricsBroadcastService>();
            broadcastService.Start();
            Logger.Info("MetricsBroadcastService started");

            // Start the latency probe service
            var probeService = UnityConfig.Container.Resolve<Services.LatencyProbeService>();
            probeService.Start();
            Logger.Info("LatencyProbeService started");

            Logger.Info("All background services started");
        }
    }
}
