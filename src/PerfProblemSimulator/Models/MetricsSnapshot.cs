using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace PerfProblemSimulator.Models
{
    /// <summary>
    /// Lightweight metrics snapshot for real-time SignalR updates.
    /// </summary>
/// <remarks>
/// <para>
/// <strong>Educational Note:</strong> This is a struct for maximum
/// efficiency when broadcasting to many SignalR clients. Value types avoid heap allocations.
/// </para>
/// </remarks>
[JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
public struct MetricsSnapshot
{
    /// <summary>
    /// When this snapshot was captured.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// Current CPU usage percentage (0-100).
    /// </summary>
    public double CpuPercent { get; set; }

    /// <summary>
    /// Process working set in megabytes.
    /// </summary>
    public double WorkingSetMb { get; set; }

    /// <summary>
    /// GC heap size in megabytes.
    /// </summary>
    public double GcHeapMb { get; set; }

    /// <summary>
    /// Total available memory on the machine in megabytes.
    /// </summary>
    /// <remarks>
    /// This value represents the total physical memory available.
    /// Used by the dashboard to calculate dynamic warning thresholds for memory usage.
    /// </remarks>
    public double TotalAvailableMemoryMb { get; set; }

    /// <summary>
    /// Current thread pool thread count.
    /// </summary>
    public int ThreadPoolThreads { get; set; }

    /// <summary>
    /// Thread pool saturation percentage (0-100).
    /// </summary>
    /// <remarks>
    /// Calculated as (ActiveThreads / MaxThreads) * 100.
    /// Note: ThreadPool.PendingWorkItemCount is not available in .NET Framework 4.8,
    /// so we show saturation % instead which indicates how close we are to running out of threads.
    /// </remarks>
    public double ThreadPoolSaturationPercent { get; set; }

    /// <summary>
    /// Number of currently active simulations.
    /// </summary>
    public int ActiveSimulationCount { get; set; }

    /// <summary>
    /// The process ID of the running application.
    /// </summary>
    /// <remarks>
    /// Used by the dashboard to detect application restarts. When the process ID
    /// changes between metrics updates, it indicates the application crashed and
    /// was restarted (e.g., due to OOM, StackOverflow, or Azure auto-restart).
    /// </remarks>
    public int ProcessId { get; set; }
}
}
