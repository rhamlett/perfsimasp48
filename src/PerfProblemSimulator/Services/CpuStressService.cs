using NLog;
using PerfProblemSimulator.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace PerfProblemSimulator.Services
{
    /// <summary>
    /// Service that creates high CPU usage through parallel spin loops.
    /// </summary>
/// <remarks>
/// <para>
/// <strong>⚠️ EDUCATIONAL PURPOSE ONLY ⚠️</strong>
/// </para>
/// <para>
/// This service intentionally implements an anti-pattern to demonstrate what high CPU usage
/// looks like and how to diagnose it. In production code, you would NEVER do this.
/// </para>
/// <para>
/// <strong>ALGORITHM:</strong>
/// 1. Create N parallel tasks where N = number of CPU cores
/// 2. Each task runs a spin loop (while true) for the specified duration
/// 3. Spin loop just increments a counter - pure busy-waiting, no sleep
/// 4. Result: 100% CPU usage across all cores
/// </para>
/// <para>
/// <strong>Why This Is Bad:</strong>
/// <list type="bullet">
/// <item>
/// <term>Spin loops waste resources</term>
/// <description>
/// The CPU is doing nothing useful - just incrementing counters and checking conditions.
/// This prevents other threads and processes from using those CPU cycles.
/// </description>
/// </item>
/// <item>
/// <term>Multi-core saturation</term>
/// <description>
/// Using <c>Parallel.For</c> with <c>Environment.ProcessorCount</c> iterations ensures
/// all CPU cores are consumed, making the entire system sluggish.
/// </description>
/// </item>
/// <item>
/// <term>No useful work</term>
/// <description>
/// Unlike legitimate CPU-intensive operations (compression, encryption, calculations),
/// this spin loop produces no output. It's pure waste.
/// </description>
/// </item>
/// </list>
/// </para>
/// <para>
/// <strong>PORTING TO OTHER LANGUAGES:</strong>
/// The goal is to saturate all CPU cores with busy-waiting:
/// <list type="bullet">
/// <item>PHP: Multiple pcntl_fork() processes each running while(true) loops</item>
/// <item>Node.js: Use worker_threads to spawn CPU-count workers, each with busy loop</item>
/// <item>Java: ExecutorService with CPU-count threads, each running while(!cancelled)</item>
/// <item>Python: multiprocessing.Pool (threads won't work due to GIL) with busy loops</item>
/// <item>Ruby: Process.fork for each CPU core (threads limited by GIL)</item>
/// </list>
/// Key: Use process-level parallelism for languages with GIL (Python/Ruby).
/// </para>
/// <para>
/// <strong>Real-World Causes of High CPU:</strong>
/// <list type="bullet">
/// <item>Inefficient algorithms (O(n²) when O(n) is possible)</item>
/// <item>Infinite loops due to bugs</item>
/// <item>Excessive regular expression backtracking</item>
/// <item>Unoptimized LINQ queries with large datasets</item>
/// <item>Busy-waiting instead of using async/await or events</item>
/// </list>
/// </para>
/// <para>
/// <strong>Diagnosis Tools:</strong>
/// <list type="bullet">
/// <item>dotnet-counters: <c>dotnet-counters monitor -p {PID} --counters System.Runtime</c></item>
/// <item>dotnet-trace: <c>dotnet-trace collect -p {PID}</c></item>
/// <item>Application Insights: CPU metrics and profiler</item>
/// <item>Azure App Service Diagnostics: CPU usage blade</item>
/// </list>
/// </para>
/// <para>
/// <strong>RELATED FILES:</strong>
/// ICpuStressService.cs (interface), CpuController.cs (API endpoint), SimulationTracker.cs
/// </para>
/// </remarks>
public class CpuStressService : ICpuStressService
    {
        private readonly ISimulationTracker _simulationTracker;
        private readonly ISimulationTelemetry _telemetry;
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Default duration in seconds when not specified or invalid.
        /// </summary>
        private const int DefaultDurationSeconds = 30;

        /// <summary>
        /// Initializes a new instance of the <see cref="CpuStressService"/> class.
        /// </summary>
        /// <param name="simulationTracker">Service for tracking active simulations.</param>
        /// <param name="telemetry">Service for tracking simulation events to Application Insights.</param>
        public CpuStressService(ISimulationTracker simulationTracker, ISimulationTelemetry telemetry)
        {
            if (simulationTracker == null) throw new ArgumentNullException(nameof(simulationTracker));
            _simulationTracker = simulationTracker;
            _telemetry = telemetry;
        }

    /// <inheritdoc />
    public Task<SimulationResult> TriggerCpuStressAsync(int durationSeconds, CancellationToken cancellationToken, string level = "high")
    {
        // ==========================================================================
        // STEP 1: Validate the duration (no upper limits - app is meant to break)
        // ==========================================================================
        var actualDuration = durationSeconds <= 0
            ? DefaultDurationSeconds
            : durationSeconds;

        // Normalize level to lowercase and default to "high" if invalid
            string normalizedLevel;
            switch (level.ToLowerInvariant())
            {
                case "moderate":
                    normalizedLevel = "moderate";
                    break;
                default:
                    normalizedLevel = "high";
                    break;
            }

        var simulationId = Guid.NewGuid();
        
        // Set Activity tag for Application Insights correlation (if enabled via Azure portal)
        Activity.Current?.SetTag("SimulationId", simulationId.ToString());
        
        var startedAt = DateTimeOffset.UtcNow;
        var estimatedEndAt = startedAt.AddSeconds(actualDuration);
        var processorCount = Environment.ProcessorCount;

        // ==========================================================================
        // STEP 2: Create a linked cancellation token
        // ==========================================================================
        // We combine the caller's cancellation token with our own, so we can cancel
        // from either the external request or internal timeout.
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var parameters = new Dictionary<string, object>
        {
            ["DurationSeconds"] = actualDuration,
            ["ProcessorCount"] = processorCount,
            ["Level"] = normalizedLevel
        };

        // Register this simulation with the tracker
        _simulationTracker.RegisterSimulation(simulationId, SimulationType.Cpu, parameters, cts);

        // Track simulation start in Application Insights (if configured)
        _telemetry?.TrackSimulationStarted(simulationId, SimulationType.Cpu, parameters);

        Logger.Info(
            "Starting CPU stress simulation {0}: {1}s @ {2} across {3} cores",
            simulationId,
            actualDuration,
            normalizedLevel,
            processorCount);

        // ==========================================================================
        // STEP 3: Start the CPU stress in the background
        // ==========================================================================
        // We use Task.Run to offload the CPU-intensive work to the thread pool,
        // allowing this method to return immediately with the simulation metadata.
        // This is important because the caller (HTTP request) shouldn't be blocked
        // waiting for the entire duration.

        _ = Task.Run(() => ExecuteCpuStress(simulationId, actualDuration, normalizedLevel, cts.Token), cts.Token);

        // ==========================================================================
        // STEP 4: Return the result immediately
        // ==========================================================================
        // The caller gets back the simulation ID and can use it to track progress
        // or cancel the simulation early.

        var result = new SimulationResult
        {
            SimulationId = simulationId,
            Type = SimulationType.Cpu,
            Status = "Started",
            Message = $"CPU stress started on {processorCount} cores for {actualDuration} seconds ({normalizedLevel} intensity). " +
                      "Observe CPU metrics in Task Manager, dotnet-counters, or Application Insights. " +
                      "High CPU like this is typically caused by spin loops, inefficient algorithms, or infinite loops.",
            ActualParameters = parameters,
            StartedAt = startedAt,
            EstimatedEndAt = estimatedEndAt
        };

        return Task.FromResult(result);
    }

    /// <summary>
    /// Executes the actual CPU stress operation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>⚠️ THIS IS AN ANTI-PATTERN - FOR EDUCATIONAL PURPOSES ONLY ⚠️</strong>
    /// </para>
    /// <para>
    /// This method uses dedicated threads with spin loops to consume available CPU.
    /// For "high" level, it runs a tight loop for ~100% usage.
    /// For "moderate" level, it uses a duty cycle (work/sleep) to simulate ~65% load.
    /// </para>
    /// <para>
    /// <strong>Why Dedicated Threads Instead of Parallel.For?</strong>
    /// Using <c>Parallel.For</c> would consume thread pool threads, which can interfere with
    /// ASP.NET Core request handling and SignalR. By using dedicated threads, we ensure the
    /// thread pool remains available for the dashboard and metrics collection.
    /// </para>
    /// </remarks>
    private void ExecuteCpuStress(Guid simulationId, int durationSeconds, string level, CancellationToken cancellationToken)
    {
        // Convert level to internal percentage: moderate = 65%, high = 100%
        var targetPercentage = level == "moderate" ? 65 : 100;
        
        try
        {
            // Calculate the end time using Stopwatch for high precision
            var endTime = Stopwatch.GetTimestamp() + (durationSeconds * Stopwatch.Frequency);
            var processorCount = Environment.ProcessorCount;

            // ==========================================================================
            // THE ANTI-PATTERN: Dedicated thread spin loops
            // ==========================================================================
            // This code intentionally creates one dedicated thread per CPU core.
            
            var threads = new Thread[processorCount];
            
            for (var i = 0; i < processorCount; i++)
            {
                var threadIndex = i;
                threads[i] = new Thread(() =>
                {
                    if (targetPercentage >= 99)
                    {
                        // 100% Load: Tight spin loop (simplest, most effective for maxing out core)
                        while (Stopwatch.GetTimestamp() < endTime && !cancellationToken.IsCancellationRequested)
                        {
                            // Intentionally empty - this is a spin loop
                        }
                    }
                    else
                    {
                        // Partial Load: Duty Cycle
                        // Work for X ms, Sleep for Y ms
                        // Using a small window (e.g., 200ms) keeps usage relatively smooth
                        // while being large enough to reduce the impact of Thread.Sleep inaccuracy.
                        const int windowMs = 200;
                        var workMs = windowMs * targetPercentage / 100;
                        var sleepMs = windowMs - workMs;

                        // Stagger start times to desynchronize the duty cycles across cores.
                        // This prevents "spiky" aggregate CPU usage where all cores sleep simultaneously.
                        // We distribute the start times evenly across the window.
                        if (processorCount > 1)
                        {
                            var startDelay = windowMs * threadIndex / processorCount;
                            Thread.Sleep(startDelay);
                        }

                        while (Stopwatch.GetTimestamp() < endTime && !cancellationToken.IsCancellationRequested)
                        {
                            var cycleStart = Stopwatch.GetTimestamp();
                            var workTicks = workMs * Stopwatch.Frequency / 1000;
                            
                            // Spin for work portion
                            while (Stopwatch.GetTimestamp() - cycleStart < workTicks)
                            {
                                // Spin
                            }

                            // Sleep for remainder of window
                            // Only sleep if the duration is significant enough (> 20ms) to ensure
                            // we don't undershoot due to timer resolution (15.6ms usually).
                            if (sleepMs > 20)
                            {
                                Thread.Sleep(sleepMs);
                            }
                        }
                    }
                })
                {
                    Name = $"CpuStress-{simulationId:N}-{threadIndex}",
                    IsBackground = true,
                    Priority = ThreadPriority.Normal
                };
            }

            // Start all threads
            foreach (var thread in threads)
            {
                thread.Start();
            }

            // Wait for all threads to complete
            foreach (var thread in threads)
            {
                thread.Join();
            }

            Logger.Info(
                "CPU stress simulation {0} completed normally after {1}s",
                simulationId,
                durationSeconds);
            
            // Track simulation end in Application Insights
            _telemetry?.TrackSimulationEnded(simulationId, SimulationType.Cpu, "Completed");
        }
        catch (OperationCanceledException)
        {
            Logger.Info(
                "CPU stress simulation {0} was cancelled",
                simulationId);
            
            // Track simulation cancellation in Application Insights
            _telemetry?.TrackSimulationEnded(simulationId, SimulationType.Cpu, "Cancelled");
        }
        catch (Exception ex)
        {
            Logger.Error(ex,
                "CPU stress simulation {0} failed with error",
                simulationId);
            
            // Track simulation failure in Application Insights
            _telemetry?.TrackSimulationEnded(simulationId, SimulationType.Cpu, "Failed");
        }
        finally
        {
            // Always unregister the simulation when done
            _simulationTracker.UnregisterSimulation(simulationId);
        }
    }
    }
}
