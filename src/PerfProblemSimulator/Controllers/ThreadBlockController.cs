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
    /// Controller for thread pool starvation simulation endpoints.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>⚠️ WARNING: This controller demonstrates a dangerous anti-pattern ⚠️</strong>
    /// </para>
    /// <para>
    /// This controller triggers sync-over-async thread blocking, which causes thread pool
    /// starvation. This is one of the most common and difficult-to-diagnose performance
    /// problems in ASP.NET applications.
    /// </para>
    /// <para>
    /// <strong>Symptoms of Thread Pool Starvation:</strong>
    /// <list type="bullet">
    /// <item>All endpoints become slow simultaneously</item>
    /// <item>CPU usage is LOW (threads are waiting, not working)</item>
    /// <item>Request queue grows continuously</item>
    /// <item>Health checks start failing</item>
    /// <item>Application appears "hung" but isn't crashed</item>
    /// </list>
    /// </para>
    /// </remarks>
    [RoutePrefix("api/threadblock")]
    public class ThreadBlockController : ApiController
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly IThreadBlockService _threadBlockService;
        private readonly ISimulationTracker _simulationTracker;

        /// <summary>
        /// Initializes a new instance of the <see cref="ThreadBlockController"/> class.
        /// </summary>
        public ThreadBlockController(
            IThreadBlockService threadBlockService,
            ISimulationTracker simulationTracker)
        {
            _threadBlockService = threadBlockService ?? throw new ArgumentNullException(nameof(threadBlockService));
            _simulationTracker = simulationTracker ?? throw new ArgumentNullException(nameof(simulationTracker));
        }

        /// <summary>
        /// Triggers sync-over-async thread pool starvation.
        /// </summary>
        /// <param name="request">
        /// Optional request body specifying delay and concurrency. If not provided, defaults are used.
        /// </param>
        /// <param name="cancellationToken">Cancellation token from the HTTP request.</param>
        /// <returns>
        /// Information about the started simulation.
        /// </returns>
        /// <remarks>
        /// <para>
        /// <strong>What This Does:</strong>
        /// </para>
        /// <para>
        /// This endpoint spawns multiple concurrent operations, each of which BLOCKS a thread pool
        /// thread using the sync-over-async anti-pattern (<c>Task.Delay(ms).Result</c>).
        /// </para>
        /// <para>
        /// While these threads are blocked waiting, they cannot handle any other requests.
        /// If enough threads are blocked, new requests will queue up waiting for threads,
        /// causing the entire application to appear slow or hung.
        /// </para>
        /// <para>
        /// <strong>How to Observe:</strong>
        /// <list type="bullet">
        /// <item><term>dotnet-counters</term><description>
        /// <c>dotnet-counters monitor -p {PID} --counters System.Runtime[threadpool-thread-count,threadpool-queue-length]</c>
        /// </description></item>
        /// <item><term>Try other endpoints</term><description>
        /// While this is running, try calling /api/health - it will be slow!
        /// </description></item>
        /// <item><term>Azure Portal</term><description>
        /// Look at Request Queue Length in App Service metrics
        /// </description></item>
        /// </list>
        /// </para>
        /// </remarks>
        /// <response code="200">Thread blocking simulation started successfully.</response>
        /// <response code="400">Invalid request parameters.</response>
        /// <response code="403">Problem endpoints are disabled via environment variable.</response>
        [HttpPost]
        [Route("trigger-sync-over-async")]
        [ResponseType(typeof(SimulationResult))]
        public async Task<IHttpActionResult> TriggerSyncOverAsync(
            [FromBody] ThreadBlockRequest request,
            CancellationToken cancellationToken)
        {
            var delayMs = request != null ? request.DelayMilliseconds : 1000;
            var concurrentRequests = request != null ? request.ConcurrentRequests : 10;
            var clientIp = GetClientIpAddress();

            Logger.Warn("⚠️ Received thread blocking request: DelayMs={0}, ConcurrentRequests={1}, ClientIP={2}",
                delayMs,
                concurrentRequests,
                clientIp);

            try
            {
                var result = await _threadBlockService.TriggerSyncOverAsyncAsync(
                    delayMs,
                    concurrentRequests,
                    cancellationToken);

                Logger.Warn("⚠️ Started thread blocking simulation {0} - THIS WILL CAUSE THREAD STARVATION",
                    (object)result.SimulationId);

                return Ok(result);
            }
            catch (OperationCanceledException)
            {
                Logger.Info("Thread blocking request was cancelled by client");
                return StatusCode((HttpStatusCode)499); // Client Closed Request
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to start thread blocking simulation");
                return Content(
                    HttpStatusCode.InternalServerError,
                    ErrorResponse.SimulationError("Failed to start thread blocking simulation."));
            }
        }

        /// <summary>
        /// Stops all active thread pool starvation simulations.
        /// </summary>
        /// <returns>Number of simulations that were stopped.</returns>
        /// <remarks>
        /// <para>
        /// This endpoint cancels all active thread blocking simulations. Already-blocked threads
        /// will eventually complete their current delay, but no new blocking operations will start.
        /// </para>
        /// </remarks>
        /// <response code="200">Thread blocking simulations stopped.</response>
        [HttpPost]
        [Route("stop")]
        [ResponseType(typeof(object))]
        public IHttpActionResult Stop()
        {
            Logger.Info("Stopping all thread blocking simulations");
            
            var cancelled = _simulationTracker.CancelByType(SimulationType.ThreadBlock);
            
            Logger.Info("Stopped {0} thread blocking simulations", cancelled);
            
            return Ok(new 
            { 
                message = $"Stopped {cancelled} thread blocking simulation(s)",
                cancelledCount = cancelled
            });
        }

        private string GetClientIpAddress()
        {
            if (Request.Properties.ContainsKey("MS_OwinContext"))
            {
                var owinContext = Request.Properties["MS_OwinContext"] as Microsoft.Owin.OwinContext;
                return owinContext?.Request?.RemoteIpAddress ?? "unknown";
            }
            return "unknown";
        }
    }
}
