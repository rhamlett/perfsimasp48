using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using PerfProblemSimulator.Models;

namespace PerfProblemSimulator.Services
{
    public class ThreadBlockService : IThreadBlockService
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private readonly ISimulationTracker _simulationTracker;
        private readonly ISimulationTelemetry _telemetry;
        private const int DefaultDelayMs = 1000;
        private const int DefaultConcurrentRequests = 10;
        private const int MinimumDelayMs = 100;

        public ThreadBlockService(ISimulationTracker simulationTracker, ISimulationTelemetry telemetry)
        {
            if (simulationTracker == null) throw new ArgumentNullException("simulationTracker");
            _simulationTracker = simulationTracker;
            _telemetry = telemetry;
        }

        public Task<SimulationResult> TriggerSyncOverAsyncAsync(int delayMilliseconds, int concurrentRequests, CancellationToken cancellationToken)
        {
            var actualDelay = delayMilliseconds <= 0 ? DefaultDelayMs : Math.Max(MinimumDelayMs, delayMilliseconds);
            var actualConcurrent = concurrentRequests <= 0 ? DefaultConcurrentRequests : concurrentRequests;

            var simulationId = Guid.NewGuid();
            
            // Set Activity tag for Application Insights correlation (if enabled via Azure portal)
            Activity.Current?.SetTag("SimulationId", simulationId.ToString());
            
            var startedAt = DateTimeOffset.UtcNow;
            var estimatedEndAt = startedAt.AddMilliseconds(actualDelay);

            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            int workerThreads, ioThreads;
            ThreadPool.GetAvailableThreads(out workerThreads, out ioThreads);
            int minWorker, minIo;
            ThreadPool.GetMinThreads(out minWorker, out minIo);
            int maxWorker, maxIo;
            ThreadPool.GetMaxThreads(out maxWorker, out maxIo);

            var parameters = new Dictionary<string, object>
            {
                ["DelayMilliseconds"] = actualDelay,
                ["ConcurrentRequests"] = actualConcurrent,
                ["ThreadPoolAvailableWorkers"] = workerThreads,
                ["ThreadPoolMinWorkers"] = minWorker,
                ["ThreadPoolMaxWorkers"] = maxWorker
            };

            _simulationTracker.RegisterSimulation(simulationId, SimulationType.ThreadBlock, parameters, cts);

            // Track simulation start in Application Insights (if configured)
            _telemetry?.TrackSimulationStarted(simulationId, SimulationType.ThreadBlock, parameters);

            Logger.Warn("Starting sync-over-async simulation {0}: {1} concurrent requests, each blocking for {2}ms. Thread pool has {3}/{4} workers available.",
                simulationId, actualConcurrent, actualDelay, workerThreads, maxWorker);

            Task.Run(() => ExecuteThreadBlocking(simulationId, actualDelay, actualConcurrent, cts.Token), cts.Token);

            var result = new SimulationResult
            {
                SimulationId = simulationId,
                Type = SimulationType.ThreadBlock,
                Status = "Started",
                Message = string.Format("Started {0} concurrent sync-over-async blocking operations, each waiting {1}ms. Thread pool currently has {2} available worker threads.",
                    actualConcurrent, actualDelay, workerThreads),
                ActualParameters = parameters,
                StartedAt = startedAt,
                EstimatedEndAt = estimatedEndAt
            };

            return Task.FromResult(result);
        }

        private void ExecuteThreadBlocking(Guid simulationId, int delayMs, int concurrentRequests, CancellationToken cancellationToken)
        {
            try
            {
                Logger.Info("Simulation {0}: Spawning {1} blocking operations", simulationId, concurrentRequests);

                var tasks = new Task[concurrentRequests];

                for (int i = 0; i < concurrentRequests; i++)
                {
                    var requestNumber = i + 1;

                    tasks[i] = Task.Run(() =>
                    {
                        if (cancellationToken.IsCancellationRequested) return;

                        Logger.Debug("Simulation {0}: Request {1} blocking thread for {2}ms", simulationId, requestNumber, delayMs);

                        try
                        {
                            Task.Delay(delayMs, cancellationToken).Wait();
                        }
                        catch (AggregateException ex)
                        {
                            if (ex.InnerException is OperationCanceledException) { }
                        }

                        Logger.Debug("Simulation {0}: Request {1} unblocked", simulationId, requestNumber);
                    }, cancellationToken);
                }

                try
                {
                    Task.WaitAll(tasks, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    Logger.Info("Simulation {0} was cancelled", simulationId);
                    _telemetry?.TrackSimulationEnded(simulationId, SimulationType.ThreadBlock, "Cancelled");
                }

                Logger.Info("Simulation {0}: All {1} blocking operations completed", simulationId, concurrentRequests);
                _telemetry?.TrackSimulationEnded(simulationId, SimulationType.ThreadBlock, "Completed");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Simulation {0} failed with error", simulationId);
                _telemetry?.TrackSimulationEnded(simulationId, SimulationType.ThreadBlock, "Failed");
            }
            finally
            {
                _simulationTracker.UnregisterSimulation(simulationId);
            }
        }
    }
}
