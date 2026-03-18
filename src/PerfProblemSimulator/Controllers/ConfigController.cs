using System;
using System.Web.Http;
using System.Web.Http.Description;
using PerfProblemSimulator.App_Start;
using PerfProblemSimulator.Services;

namespace PerfProblemSimulator.Controllers
{
    /// <summary>
    /// Provides client-side configuration settings to the dashboard UI.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>PURPOSE:</strong>
    /// This controller exposes non-sensitive configuration to the frontend JavaScript,
    /// allowing runtime customization without rebuilding the application. This is essential
    /// for environments like Azure App Service where environment variables are the primary
    /// configuration mechanism.
    /// </para>
    /// <para>
    /// <strong>FEATURE REQUIREMENTS:</strong>
    /// <list type="bullet">
    /// <item>Return the app title (customizable per deployment)</item>
    /// <item>Return optional page footer HTML (for branding/legal notices)</item>
    /// <item>All values should have sensible defaults if not configured</item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>PORTING TO OTHER LANGUAGES:</strong>
    /// <list type="bullet">
    /// <item>PHP: Use $_ENV or getenv() to read environment variables</item>
    /// <item>Node.js: Use process.env.VARIABLE_NAME</item>
    /// <item>Java: Use System.getenv() or Spring @Value annotations</item>
    /// <item>Python: Use os.environ.get('VARIABLE_NAME', 'default')</item>
    /// <item>Ruby: Use ENV['VARIABLE_NAME'] || 'default'</item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>RELATED FILES:</strong>
    /// <list type="bullet">
    /// <item>Models/ProblemSimulatorOptions.cs - Configuration model</item>
    /// <item>wwwroot/js/dashboard.js - Frontend consumer (fetchConfig function)</item>
    /// <item>All HTML pages - Footer display logic</item>
    /// </list>
    /// </para>
    /// </remarks>
    [RoutePrefix("api/config")]
    public class ConfigController : ApiController
    {
        private readonly IIdleStateService _idleStateService;

        public ConfigController(IIdleStateService idleStateService)
        {
            _idleStateService = idleStateService ?? throw new ArgumentNullException(nameof(idleStateService));
        }

        /// <summary>
        /// Gets client-side configuration settings.
        /// </summary>
        /// <returns>Configuration object with app title and other UI settings.</returns>
        /// <response code="200">Returns the configuration settings.</response>
        [HttpGet]
        [Route("")]
        [ResponseType(typeof(ClientConfig))]
        public IHttpActionResult GetConfig()
        {
            // PAGE_FOOTER is read directly from environment variable
            var pageFooter = Environment.GetEnvironmentVariable("PAGE_FOOTER") ?? "";
            
            return Ok(new ClientConfig
            {
                PageFooter = pageFooter,
                LatencyProbeIntervalMs = ConfigurationHelper.LatencyProbeIntervalMs,
                IdleTimeoutMinutes = _idleStateService.IdleTimeoutMinutes
            });
        }
    }

    /// <summary>
    /// Configuration settings exposed to the client.
    /// </summary>
    public class ClientConfig
    {
        /// <summary>
        /// Custom HTML content for the page footer. Empty string if not configured.
        /// </summary>
        public string PageFooter { get; set; } = "";

        /// <summary>
        /// How often the server sends latency probes in milliseconds.
        /// </summary>
        public int LatencyProbeIntervalMs { get; set; } = 200;

        /// <summary>
        /// How long until the app goes idle (stops probing) in minutes.
        /// </summary>
        public int IdleTimeoutMinutes { get; set; } = 20;
    }
}
