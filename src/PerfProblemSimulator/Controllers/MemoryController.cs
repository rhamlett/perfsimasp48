using NLog;
using PerfProblemSimulator.Models;
using PerfProblemSimulator.Services;
using System;
using System.Net;
using System.Web.Http;
using System.Web.Http.Description;

namespace PerfProblemSimulator.Controllers
{
    /// <summary>
    /// Controller for memory pressure simulation endpoints.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>⚠️ WARNING: This controller allocates and holds memory intentionally ⚠️</strong>
    /// </para>
    /// <para>
    /// This controller provides endpoints to allocate and release memory for educational
    /// and diagnostic purposes. It should only be used in controlled environments for:
    /// <list type="bullet">
    /// <item>Learning how to diagnose memory issues</item>
    /// <item>Testing memory alerts and auto-scaling</item>
    /// <item>Understanding garbage collection behavior</item>
    /// <item>Practicing with memory profiling tools</item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>Key Concepts Demonstrated:</strong>
    /// <list type="bullet">
    /// <item><term>Large Object Heap (LOH)</term><description>Objects > 85KB go to LOH</description></item>
    /// <item><term>Pinned objects</term><description>GC cannot move pinned memory</description></item>
    /// <item><term>Working Set vs Private Bytes</term><description>Different memory metrics</description></item>
    /// <item><term>GC generations</term><description>Gen0, Gen1, Gen2 collection patterns</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    [RoutePrefix("api/memory")]
    public class MemoryController : ApiController
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly IMemoryPressureService _memoryPressureService;

        /// <summary>
        /// Initializes a new instance of the <see cref="MemoryController"/> class.
        /// </summary>
        /// <param name="memoryPressureService">Service for memory allocation/release.</param>
        public MemoryController(IMemoryPressureService memoryPressureService)
        {
            _memoryPressureService = memoryPressureService ?? throw new ArgumentNullException(nameof(memoryPressureService));
        }

        /// <summary>
        /// Allocates and holds a block of memory to create memory pressure.
        /// </summary>
        /// <param name="request">
        /// Optional request body specifying the size. If not provided, defaults are used.
        /// </param>
        /// <returns>
        /// Information about the allocation, including actual size and total allocated.
        /// </returns>
        /// <remarks>
        /// <para>
        /// <strong>What This Does:</strong>
        /// </para>
        /// <para>
        /// This endpoint allocates a large byte array and holds a reference to it,
        /// preventing the garbage collector from reclaiming it. The memory is pinned
        /// on the Large Object Heap (LOH), which can cause heap fragmentation.
        /// </para>
        /// <para>
        /// Allocations are cumulative - calling this endpoint multiple times will
        /// accumulate memory until the configured limit is reached.
        /// </para>
        /// <para>
        /// <strong>How to Observe:</strong>
        /// <list type="bullet">
        /// <item><term>Task Manager</term><description>Watch Working Set increase</description></item>
        /// <item><term>dotnet-counters</term><description><c>dotnet-counters monitor -p {PID}</c></description></item>
        /// <item><term>Azure Portal</term><description>App Service → Metrics → Memory Working Set</description></item>
        /// <item><term>Application Insights</term><description>Performance → Memory usage</description></item>
        /// </list>
        /// </para>
        /// </remarks>
        /// <response code="200">Memory allocated successfully.</response>
        /// <response code="400">Invalid request parameters.</response>
        /// <response code="403">Problem endpoints are disabled via environment variable.</response>
        [HttpPost]
        [Route("allocate-memory")]
        [ResponseType(typeof(SimulationResult))]
        public IHttpActionResult AllocateMemory([FromBody] MemoryAllocationRequest request)
        {
            var sizeMegabytes = request != null ? request.SizeMegabytes : 100;
            var clientIp = GetClientIpAddress();

            Logger.Info("Received memory allocation request: SizeMegabytes={0}, ClientIP={1}",
                sizeMegabytes,
                clientIp);

            try
            {
                var result = _memoryPressureService.AllocateMemory(sizeMegabytes);

                Logger.Info("Memory allocation result: SimulationId={0}, Status={1}",
                    result.SimulationId,
                    result.Status);

                return Ok(result);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to allocate memory");
                return Content(
                    HttpStatusCode.InternalServerError,
                    ErrorResponse.SimulationError("Failed to allocate memory. See server logs for details."));
            }
        }

        /// <summary>
        /// Releases all allocated memory blocks.
        /// </summary>
        /// <param name="request">
        /// Optional request body specifying whether to force garbage collection.
        /// </param>
        /// <returns>
        /// Details about the released memory.
        /// </returns>
        /// <remarks>
        /// <para>
        /// <strong>What This Does:</strong>
        /// </para>
        /// <para>
        /// This endpoint releases all references to allocated memory blocks, making them
        /// eligible for garbage collection. Optionally, it can force an immediate GC.
        /// </para>
        /// <para>
        /// <strong>Important Note:</strong> Even after forcing GC, the Working Set may not
        /// immediately decrease because:
        /// <list type="bullet">
        /// <item>The runtime may keep memory committed for future allocations</item>
        /// <item>The OS manages when physical pages are actually released</item>
        /// <item>Memory pressure thresholds affect when memory is returned</item>
        /// </list>
        /// </para>
        /// </remarks>
        /// <response code="200">Memory released successfully.</response>
        /// <response code="403">Problem endpoints are disabled via environment variable.</response>
        [HttpPost]
        [Route("release-memory")]
        [ResponseType(typeof(MemoryReleaseResult))]
        public IHttpActionResult ReleaseMemory([FromBody] ReleaseMemoryRequest request)
        {
            var forceGc = request != null ? request.ForceGarbageCollection : true;
            var clientIp = GetClientIpAddress();

            Logger.Info("Received memory release request: ForceGC={0}, ClientIP={1}",
                forceGc,
                clientIp);

            try
            {
                var result = _memoryPressureService.ReleaseAllMemory(forceGc);

                Logger.Info("Memory release result: ReleasedBlocks={0}, ReleasedMB={1}",
                    result.ReleasedBlockCount,
                    result.ReleasedMegabytes);

                return Ok(result);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to release memory");
                return Content(
                    HttpStatusCode.InternalServerError,
                    ErrorResponse.SimulationError("Failed to release memory. See server logs for details."));
            }
        }

        /// <summary>
        /// Gets the current memory allocation status.
        /// </summary>
        /// <returns>
        /// Current memory allocation statistics.
        /// </returns>
        /// <response code="200">Returns memory status.</response>
        [HttpGet]
        [Route("status")]
        [ResponseType(typeof(MemoryStatus))]
        public IHttpActionResult GetStatus()
        {
            var status = _memoryPressureService.GetMemoryStatus();
            return Ok(status);
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

    /// <summary>
    /// Request model for releasing allocated memory.
    /// </summary>
    public class ReleaseMemoryRequest
    {
        /// <summary>
        /// Whether to force garbage collection after releasing references.
        /// </summary>
        /// <remarks>
        /// Default: true. Forcing GC helps demonstrate immediate memory reclamation,
        /// but in production, you generally shouldn't force GC.
        /// </remarks>
        public bool ForceGarbageCollection { get; set; } = true;
    }
}
