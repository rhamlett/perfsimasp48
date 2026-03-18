using NLog;
using PerfProblemSimulator.Models;
using PerfProblemSimulator.Services;
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;
using System.Web.Http.Description;

namespace PerfProblemSimulator.Controllers
{
    /// <summary>
    /// Controller for CPU stress simulation endpoints.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>⚠️ WARNING: This controller triggers intentional performance problems ⚠️</strong>
    /// </para>
    /// <para>
    /// This controller provides endpoints to trigger high CPU usage for educational and
    /// diagnostic purposes. It should only be used in controlled environments for:
    /// <list type="bullet">
    /// <item>Learning how to diagnose high CPU issues</item>
    /// <item>Testing monitoring and alerting systems</item>
    /// <item>Practicing with diagnostic tools (dotnet-counters, dotnet-trace, etc.)</item>
    /// <item>Validating auto-scaling configurations</item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>Safety Features:</strong>
    /// <list type="bullet">
    /// <item>Duration is capped to a configurable maximum (default: 300 seconds)</item>
    /// <item>Can be disabled entirely via DISABLE_PROBLEM_ENDPOINTS environment variable</item>
    /// <item>All operations are logged for audit purposes</item>
    /// <item>Simulations can be cancelled via the admin endpoint or by restarting the app</item>
    /// </list>
    /// </para>
    /// </remarks>
    [RoutePrefix("api/cpu")]
    public class CpuController : ApiController
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly ICpuStressService _cpuStressService;
        private readonly ISimulationTracker _simulationTracker;

        /// <summary>
        /// Initializes a new instance of the <see cref="CpuController"/> class.
        /// </summary>
        /// <param name="cpuStressService">Service for triggering CPU stress.</param>
        /// <param name="simulationTracker">Service for tracking and cancelling simulations.</param>
        public CpuController(
            ICpuStressService cpuStressService,
            ISimulationTracker simulationTracker)
        {
            _cpuStressService = cpuStressService ?? throw new ArgumentNullException(nameof(cpuStressService));
            _simulationTracker = simulationTracker ?? throw new ArgumentNullException(nameof(simulationTracker));
        }

        /// <summary>
        /// Triggers a high CPU usage simulation.
        /// </summary>
        /// <param name="request">
        /// Optional request body specifying the duration. If not provided, defaults are used.
        /// </param>
        /// <returns>
        /// Information about the started simulation, including its ID and actual parameters.
        /// </returns>
        /// <remarks>
        /// <para>
        /// <strong>What This Does:</strong>
        /// </para>
        /// <para>
        /// This endpoint starts a CPU stress operation that will consume all available CPU cores
        /// for the specified duration. The stress is created using parallel spin loops - a classic
        /// anti-pattern that wastes CPU cycles doing nothing useful.
        /// </para>
        /// <para>
        /// <strong>How to Observe:</strong>
        /// <list type="bullet">
        /// <item><term>Task Manager</term><description>Watch the CPU graph spike to near 100%</description></item>
        /// <item><term>dotnet-counters</term><description><c>dotnet-counters monitor -p {PID}</c></description></item>
        /// <item><term>Azure Portal</term><description>App Service → Metrics → CPU Percentage</description></item>
        /// <item><term>Application Insights</term><description>Performance → Server CPU</description></item>
        /// </list>
        /// </para>
        /// <para>
        /// <strong>Request Example:</strong>
        /// <code>
        /// POST /api/cpu/trigger-high-cpu
        /// Content-Type: application/json
        ///
        /// { "durationSeconds": 60 }
        /// </code>
        /// </para>
        /// </remarks>
        /// <response code="200">CPU stress simulation started successfully.</response>
        /// <response code="400">Invalid request parameters.</response>
        /// <response code="403">Problem endpoints are disabled via environment variable.</response>
        [HttpPost]
        [Route("trigger-high-cpu")]
        [ResponseType(typeof(SimulationResult))]
        public async Task<IHttpActionResult> TriggerHighCpu([FromBody] CpuStressRequest request)
        {
            // Use defaults if no request body provided
            var durationSeconds = request?.DurationSeconds ?? 30;
            var level = request?.Level ?? "high";

            // Log the incoming request (FR-010: Request logging)
            Logger.Info(
                "Received CPU stress request: DurationSeconds={0}, Level={1}",
                durationSeconds,
                level);

            try
            {
                var result = await _cpuStressService.TriggerCpuStressAsync(durationSeconds, CancellationToken.None, level);

                Logger.Info(
                    "Started CPU stress simulation {0} for {1}s @ {2}",
                    result.SimulationId,
                    result.ActualParameters?["DurationSeconds"],
                    result.ActualParameters?["Level"]);

                return Ok(result);
            }
            catch (OperationCanceledException)
            {
                Logger.Info("CPU stress request was cancelled by client");
                return StatusCode((HttpStatusCode)499); // Client Closed Request
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to start CPU stress simulation");
                return Content(
                    HttpStatusCode.InternalServerError,
                    ErrorResponse.SimulationError("Failed to start CPU stress simulation. See server logs for details."));
            }
        }

        /// <summary>
        /// Stops all active CPU stress simulations.
        /// </summary>
        /// <returns>Number of simulations that were stopped.</returns>
        /// <response code="200">CPU stress simulations stopped.</response>
        [HttpPost]
        [Route("stop")]
        public IHttpActionResult Stop()
        {
            Logger.Info("Stopping all CPU stress simulations");
            
            var cancelled = _simulationTracker.CancelByType(SimulationType.Cpu);
            
            Logger.Info("Stopped {0} CPU stress simulations", cancelled);
            
            return Ok(new 
            { 
                message = $"Stopped {cancelled} CPU stress simulation(s)",
                cancelledCount = cancelled
            });
        }
    }
}
