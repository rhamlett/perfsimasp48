using PerfProblemSimulator.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Web.Http;
using System.Web.Http.Description;

namespace PerfProblemSimulator.Controllers
{
    /// <summary>
    /// Administrative endpoints for managing the simulator.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Educational Note:</strong>
    /// </para>
    /// <para>
    /// This controller provides "escape hatch" functionality for resetting the
    /// application state. In production applications, similar administrative
    /// endpoints should be:
    /// </para>
    /// <list type="bullet">
    /// <item>Protected by authentication/authorization</item>
    /// <item>Rate-limited to prevent abuse</item>
    /// <item>Audited/logged for compliance</item>
    /// <item>Potentially disabled in production via feature flags</item>
    /// </list>
    /// </remarks>
    [RoutePrefix("api/admin")]
    public class AdminController : ApiController
    {
        private readonly ISimulationTracker _simulationTracker;
        private readonly IMemoryPressureService _memoryPressureService;

        /// <summary>
        /// Initializes a new instance of the <see cref="AdminController"/> class.
        /// </summary>
        public AdminController(
            ISimulationTracker simulationTracker,
            IMemoryPressureService memoryPressureService)
        {
            _simulationTracker = simulationTracker ?? throw new ArgumentNullException(nameof(simulationTracker));
            _memoryPressureService = memoryPressureService ?? throw new ArgumentNullException(nameof(memoryPressureService));
        }

        /// <summary>
        /// Gets current simulation statistics.
        /// </summary>
        /// <returns>Statistics about active and historical simulations.</returns>
        /// <response code="200">Returns simulation statistics.</response>
        [HttpGet]
        [Route("stats")]
        [ResponseType(typeof(SimulationStats))]
        public IHttpActionResult GetStats()
        {
            var activeSimulations = _simulationTracker.GetActiveSimulations();
            var memoryStatus = _memoryPressureService.GetMemoryStatus();

            int availableWorker, availableIo;
            int maxWorker, maxIo;
            ThreadPool.GetAvailableThreads(out availableWorker, out availableIo);
            ThreadPool.GetMaxThreads(out maxWorker, out maxIo);

            return Ok(new SimulationStats
            {
                ActiveSimulationCount = activeSimulations.Count,
                SimulationsByType = activeSimulations
                    .GroupBy(s => s.Type)
                    .ToDictionary(g => g.Key.ToString(), g => g.Count()),
                MemoryAllocated = new MemoryStats
                {
                    BlockCount = memoryStatus.AllocatedBlocksCount,
                    TotalBytes = memoryStatus.TotalAllocatedBytes,
                    TotalMegabytes = memoryStatus.TotalAllocatedBytes / (1024.0 * 1024.0)
                },
                ThreadPool = new ThreadPoolStats
                {
                    AvailableWorkerThreads = availableWorker,
                    MaxWorkerThreads = maxWorker,
                    UsedWorkerThreads = maxWorker - availableWorker,
                    AvailableIoThreads = availableIo,
                    MaxIoThreads = maxIo,
                    PendingWorkItems = 0 // ThreadPool.PendingWorkItemCount not available in .NET Framework
                },
                ProcessInfo = new ProcessStats
                {
                    ProcessorCount = Environment.ProcessorCount,
                    WorkingSetBytes = Environment.WorkingSet,
                    ManagedHeapBytes = GC.GetTotalMemory(forceFullCollection: false),
                    AzureSku = Environment.GetEnvironmentVariable("WEBSITE_SKU") ?? "Local",
                    ComputeMode = Environment.GetEnvironmentVariable("WEBSITE_COMPUTE_MODE"),
                    ComputerName = Environment.GetEnvironmentVariable("COMPUTERNAME")
                }
            });
        }
    }

    /// <summary>
    /// Current simulation statistics.
    /// </summary>
    public class SimulationStats
    {
        /// <summary>
        /// Total number of active simulations.
        /// </summary>
        public int ActiveSimulationCount { get; set; }

        /// <summary>
        /// Breakdown of simulations by type.
        /// </summary>
        public Dictionary<string, int> SimulationsByType { get; set; } = new Dictionary<string, int>();

        /// <summary>
        /// Memory allocation statistics.
        /// </summary>
        public MemoryStats MemoryAllocated { get; set; }

        /// <summary>
        /// Thread pool statistics.
        /// </summary>
        public ThreadPoolStats ThreadPool { get; set; }

        /// <summary>
        /// Process information.
        /// </summary>
        public ProcessStats ProcessInfo { get; set; }
    }

    /// <summary>
    /// Memory allocation statistics.
    /// </summary>
    public class MemoryStats
    {
        /// <summary>
        /// Number of allocated memory blocks.
        /// </summary>
        public int BlockCount { get; set; }

        /// <summary>
        /// Total allocated bytes.
        /// </summary>
        public long TotalBytes { get; set; }

        /// <summary>
        /// Total allocated megabytes.
        /// </summary>
        public double TotalMegabytes { get; set; }
    }

    /// <summary>
    /// Thread pool statistics.
    /// </summary>
    public class ThreadPoolStats
    {
        /// <summary>
        /// Available worker threads.
        /// </summary>
        public int AvailableWorkerThreads { get; set; }

        /// <summary>
        /// Maximum worker threads.
        /// </summary>
        public int MaxWorkerThreads { get; set; }

        /// <summary>
        /// Currently used worker threads.
        /// </summary>
        public int UsedWorkerThreads { get; set; }

        /// <summary>
        /// Available I/O completion threads.
        /// </summary>
        public int AvailableIoThreads { get; set; }

        /// <summary>
        /// Maximum I/O completion threads.
        /// </summary>
        public int MaxIoThreads { get; set; }

        /// <summary>
        /// Number of pending work items in the queue.
        /// </summary>
        public long PendingWorkItems { get; set; }
    }

    /// <summary>
    /// Process information statistics.
    /// </summary>
    public class ProcessStats
    {
        /// <summary>
        /// Number of processors available.
        /// </summary>
        public int ProcessorCount { get; set; }

        /// <summary>
        /// Process working set in bytes.
        /// </summary>
        public long WorkingSetBytes { get; set; }

        /// <summary>
        /// Managed heap size in bytes.
        /// </summary>
        public long ManagedHeapBytes { get; set; }

        /// <summary>
        /// The Azure SKU (Pricing Tier) if running in Azure App Service (e.g., P0V3, Standard, Basic).
        /// </summary>
        public string AzureSku { get; set; }

        /// <summary>
        /// The compute mode (e.g., Dedicated, Shared).
        /// </summary>
        public string ComputeMode { get; set; }

        /// <summary>
        /// The computer/worker name (from COMPUTERNAME environment variable).
        /// </summary>
        public string ComputerName { get; set; }
    }
}
