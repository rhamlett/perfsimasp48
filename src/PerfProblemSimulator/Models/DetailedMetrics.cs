using System;
using System.Collections.Generic;

namespace PerfProblemSimulator.Models
{
    /// <summary>
    /// Detailed CPU metrics for the health status endpoint.
    /// </summary>
/// <remarks>
/// <para>
/// <strong>PURPOSE:</strong>
/// Provides CPU-specific metrics for the /api/metrics/health endpoint.
/// These values help diagnose whether the application is CPU-bound or I/O-bound.
/// </para>
/// <para>
/// <strong>PORTING TO OTHER LANGUAGES:</strong>
/// <list type="bullet">
/// <item>PHP: Use sys_getloadavg() on Linux or exec('wmic cpu') on Windows</item>
/// <item>Node.js: Use os.cpus() and calculate usage from idle/total times</item>
/// <item>Java: Use OperatingSystemMXBean.getProcessCpuLoad()</item>
/// <item>Python: Use psutil.cpu_percent() and psutil.cpu_count()</item>
/// </list>
/// </para>
/// </remarks>
public class CpuMetrics
{
    /// <summary>
    /// Current CPU usage percentage (0-100).
    /// </summary>
    public double UsagePercent { get; set; }

    /// <summary>
    /// Number of logical processors available.
    /// </summary>
    public int ProcessorCount { get; set; }
}

/// <summary>
/// Detailed memory metrics for the health status endpoint.
/// </summary>
public class MemoryMetrics
{
    /// <summary>
    /// Process working set in bytes.
    /// </summary>
    public long WorkingSetBytes { get; set; }

    /// <summary>
    /// Managed GC heap size in bytes.
    /// </summary>
    public long GcHeapBytes { get; set; }

    /// <summary>
    /// Number of intentionally allocated memory blocks.
    /// </summary>
    public int AllocatedBlocksCount { get; set; }

    /// <summary>
    /// Total size of intentionally allocated blocks in bytes.
    /// </summary>
    public long AllocatedBlocksTotalBytes { get; set; }
}

/// <summary>
/// Detailed thread pool metrics for the health status endpoint.
/// </summary>
public class ThreadPoolMetrics
{
    /// <summary>
    /// Current number of thread pool threads.
    /// </summary>
    public int ThreadCount { get; set; }

    /// <summary>
    /// Number of work items waiting in the thread pool queue.
    /// </summary>
    public long PendingWorkItems { get; set; }

    /// <summary>
    /// Total work items completed since application start.
    /// </summary>
    public long CompletedWorkItems { get; set; }

    /// <summary>
    /// Number of available worker threads.
    /// </summary>
    public int AvailableWorkerThreads { get; set; }

    /// <summary>
    /// Maximum worker threads in the pool.
    /// </summary>
    public int MaxWorkerThreads { get; set; }

    /// <summary>
    /// Number of available I/O completion threads.
    /// </summary>
    public int AvailableIoThreads { get; set; }

    /// <summary>
    /// Maximum I/O completion threads in the pool.
    /// </summary>
    public int MaxIoThreads { get; set; }
}

/// <summary>
/// Comprehensive application health status.
/// </summary>
public class ApplicationHealthStatus
{
    /// <summary>
    /// When this status was captured.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// CPU-related metrics.
    /// </summary>
    public CpuMetrics Cpu { get; set; }

    /// <summary>
    /// Memory-related metrics.
    /// </summary>
    public MemoryMetrics Memory { get; set; }

    /// <summary>
    /// Thread pool statistics.
    /// </summary>
    public ThreadPoolMetrics ThreadPool { get; set; }

    /// <summary>
    /// Summary of currently active simulations.
    /// </summary>
    public List<SimulationSummary> ActiveSimulations { get; set; } = new List<SimulationSummary>();

    /// <summary>
    /// Overall health assessment.
    /// </summary>
    public bool IsHealthy { get; set; }

    /// <summary>
    /// Any warning messages about current state.
    /// </summary>
    public List<string> Warnings { get; set; } = new List<string>();
}

/// <summary>
/// Summary of an active simulation for metrics reporting.
/// </summary>
public class SimulationSummary
{
    /// <summary>
    /// Simulation identifier.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Type of simulation.
    /// </summary>
    public SimulationType Type { get; set; }

    /// <summary>
    /// When the simulation started.
    /// </summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>
    /// How long the simulation has been running.
    /// </summary>
    public TimeSpan RunningDuration { get; set; }
}
}
