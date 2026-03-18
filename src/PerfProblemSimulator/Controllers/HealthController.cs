using PerfProblemSimulator.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Web.Http;
using System.Web.Http.Description;

namespace PerfProblemSimulator.Controllers
{
    /// <summary>
    /// Controller for application health checks.
    /// Provides endpoints that remain responsive even under stress conditions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Educational Note:</strong> Health endpoints are critical for load balancers,
    /// orchestrators (like Kubernetes), and monitoring systems. Azure App Service uses
    /// health check endpoints to determine if an instance should receive traffic.
    /// </para>
    /// <para>
    /// This controller is designed to remain responsive even during performance problem
    /// simulations. It provides both a simple liveness probe (/api/health) and a more
    /// detailed status endpoint (/api/health/status) that includes information about
    /// active simulations.
    /// </para>
    /// </remarks>
    [RoutePrefix("api/health")]
    public class HealthController : ApiController
    {
        private readonly ISimulationTracker _simulationTracker;

        /// <summary>
        /// Initializes a new instance of the <see cref="HealthController"/> class.
        /// </summary>
        /// <param name="simulationTracker">Service for tracking active simulations.</param>
        public HealthController(ISimulationTracker simulationTracker)
        {
            _simulationTracker = simulationTracker ?? throw new ArgumentNullException(nameof(simulationTracker));
        }

        /// <summary>
        /// Simple liveness probe endpoint.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Returns a simple "Healthy" response to indicate the application is running.
        /// This endpoint should always respond quickly, regardless of system load.
        /// </para>
        /// <para>
        /// <strong>Azure App Service Usage:</strong> Configure this as the health probe path
        /// in your App Service configuration to enable automatic instance replacement
        /// when the application becomes unresponsive.
        /// </para>
        /// </remarks>
        /// <response code="200">Application is healthy and responding to requests.</response>
        [HttpGet]
        [Route("")]
        [ResponseType(typeof(HealthResponse))]
        public IHttpActionResult Get()
        {
            return Ok(new HealthResponse
            {
                Status = "Healthy",
                Timestamp = DateTimeOffset.UtcNow
            });
        }

        /// <summary>
        /// Detailed status endpoint including active simulation information.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Provides more detailed health information including the count and types
        /// of any currently running simulations. Useful for monitoring dashboards
        /// that need to understand the current state of the simulator.
        /// </para>
        /// </remarks>
        /// <response code="200">Returns detailed health status.</response>
        [HttpGet]
        [Route("status")]
        [ResponseType(typeof(DetailedHealthResponse))]
        public IHttpActionResult GetStatus()
        {
            var activeSimulations = _simulationTracker.GetActiveSimulations();

            return Ok(new DetailedHealthResponse
            {
                Status = "Healthy",
                Timestamp = DateTimeOffset.UtcNow,
                ActiveSimulationCount = activeSimulations.Count,
                ActiveSimulations = activeSimulations
                    .Select(s => new ActiveSimulationSummary
                    {
                        Id = s.Id,
                        Type = s.Type.ToString(),
                        StartedAt = s.StartedAt,
                        RunningDurationSeconds = (int)(DateTimeOffset.UtcNow - s.StartedAt).TotalSeconds
                    })
                    .ToList()
            });
        }

        /// <summary>
        /// Lightweight probe endpoint for latency measurement.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>Educational Note:</strong> This endpoint is designed for measuring
        /// request processing latency. It does minimal work - just returns a timestamp.
        /// During thread pool starvation, even this simple endpoint will show increased
        /// latency because the request must wait for a thread pool thread to process it.
        /// </para>
        /// <para>
        /// Compare the response time of this endpoint before and during thread pool
        /// starvation to see the dramatic impact on user-perceived performance.
        /// </para>
        /// </remarks>
        /// <response code="200">Returns probe timestamp for latency calculation.</response>
        [HttpGet]
        [Route("probe")]
        [ResponseType(typeof(ProbeResponse))]
        public IHttpActionResult Probe()
        {
            int workerThreads, completionPortThreads;
            ThreadPool.GetAvailableThreads(out workerThreads, out completionPortThreads);
            int maxWorkerThreads, maxCompletionPortThreads;
            ThreadPool.GetMaxThreads(out maxWorkerThreads, out maxCompletionPortThreads);
            _ = completionPortThreads; // Silence IDE warning
            _ = maxCompletionPortThreads; // Silence IDE warning

            return Ok(new ProbeResponse
            {
                ServerTimestamp = DateTimeOffset.UtcNow,
                ThreadPoolThreads = maxWorkerThreads - workerThreads,
                PendingWorkItems = 0 // Not available in .NET Framework 4.8
            });
        }

        /// <summary>
        /// Returns build information including the build timestamp.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>Educational Note:</strong> The build timestamp is embedded in the assembly
        /// during compilation via MSBuild. This provides an accurate record of when the
        /// deployed code was built, useful for debugging and deployment verification.
        /// </para>
        /// </remarks>
        /// <response code="200">Returns build information.</response>
        [HttpGet]
        [Route("build")]
        [ResponseType(typeof(BuildInfoResponse))]
        public IHttpActionResult GetBuildInfo()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var buildTimestamp = assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "BuildTimestamp")?.Value;

            return Ok(new BuildInfoResponse
            {
                BuildTimestamp = buildTimestamp,
                AssemblyVersion = assembly.GetName().Version?.ToString()
            });
        }
    }

    /// <summary>
    /// Simple health check response.
    /// </summary>
    public class HealthResponse
    {
        /// <summary>
        /// Health status. Always "Healthy" if the endpoint responds.
        /// </summary>
        public string Status { get; set; } = string.Empty;

        /// <summary>
        /// When this health check was performed.
        /// </summary>
        public DateTimeOffset Timestamp { get; set; }
    }

    /// <summary>
    /// Detailed health check response including simulation state.
    /// </summary>
    public class DetailedHealthResponse : HealthResponse
    {
        /// <summary>
        /// Number of currently active simulations.
        /// </summary>
        public int ActiveSimulationCount { get; set; }

        /// <summary>
        /// Summary of each active simulation.
        /// </summary>
        public List<ActiveSimulationSummary> ActiveSimulations { get; set; } = new List<ActiveSimulationSummary>();
    }

    /// <summary>
    /// Summary information about an active simulation.
    /// </summary>
    public class ActiveSimulationSummary
    {
        /// <summary>
        /// Unique identifier for the simulation.
        /// </summary>
        public Guid Id { get; set; }

        /// <summary>
        /// Type of simulation (Cpu, Memory, ThreadBlock).
        /// </summary>
        public string Type { get; set; } = string.Empty;

        /// <summary>
        /// When the simulation started.
        /// </summary>
        public DateTimeOffset StartedAt { get; set; }

        /// <summary>
        /// How long the simulation has been running in seconds.
        /// </summary>
        public int RunningDurationSeconds { get; set; }
    }

    /// <summary>
    /// Response from the latency probe endpoint.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Educational Note:</strong> This response includes thread pool information
    /// to help correlate latency with thread pool state. During starvation, you'll see
    /// high pending work items and latency increasing together.
    /// </para>
    /// </remarks>
    public class ProbeResponse
    {
        /// <summary>
        /// Server timestamp when the probe was processed.
        /// </summary>
        public DateTimeOffset ServerTimestamp { get; set; }

        /// <summary>
        /// Current number of thread pool threads.
        /// </summary>
        public int ThreadPoolThreads { get; set; }

        /// <summary>
        /// Number of work items waiting in the thread pool queue.
        /// </summary>
        public long PendingWorkItems { get; set; }
    }

    /// <summary>
    /// Response containing build information.
    /// </summary>
    public class BuildInfoResponse
    {
        /// <summary>
        /// UTC timestamp when the application was built (ISO 8601 format).
        /// </summary>
        public string BuildTimestamp { get; set; }

        /// <summary>
        /// Assembly version of the application.
        /// </summary>
        public string AssemblyVersion { get; set; }
    }
}
