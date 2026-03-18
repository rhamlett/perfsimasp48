using Microsoft.AspNet.SignalR;
using NLog;
using PerfProblemSimulator.App_Start;
using PerfProblemSimulator.Hubs;
using PerfProblemSimulator.Models;
using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PerfProblemSimulator.Services
{
    /// <summary>
    /// Background service that measures request latency by probing an internal endpoint.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>PURPOSE:</strong> Demonstrates how thread pool starvation affects request
    /// processing latency. Runs on a DEDICATED THREAD to ensure it can measure latency
    /// even during severe starvation - the probe must not be affected by the problem it detects.
    /// </para>
    /// </remarks>
    public class LatencyProbeService : IDisposable
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly ISimulationTracker _simulationTracker;
        private readonly IIdleStateService _idleStateService;
        private readonly int _probeIntervalMs;

        private Thread _probeThread;
        private CancellationTokenSource _cts;
        private bool _disposed;
        private string _baseUrl;

        /// <summary>
        /// Request timeout in milliseconds.
        /// </summary>
        private const int RequestTimeoutMs = 30000;

        /// <summary>
        /// Initializes a new instance of the <see cref="LatencyProbeService"/> class.
        /// </summary>
        public LatencyProbeService(
            ISimulationTracker simulationTracker,
            IIdleStateService idleStateService)
        {
            if (simulationTracker == null) throw new ArgumentNullException("simulationTracker");
            if (idleStateService == null) throw new ArgumentNullException("idleStateService");

            _simulationTracker = simulationTracker;
            _idleStateService = idleStateService;

            // Apply safety limit: minimum 100ms (10 probes/sec max)
            var configuredInterval = ConfigurationHelper.LatencyProbeIntervalMs;
            _probeIntervalMs = Math.Max(100, configuredInterval);
        }

        /// <summary>
        /// Starts the latency probe service.
        /// </summary>
        public void Start()
        {
            _cts = new CancellationTokenSource();

            // Get the server's actual listening address
            _baseUrl = GetProbeBaseUrl();

            // Create a dedicated thread (not from thread pool) for reliable probing
            _probeThread = new Thread(ProbeLoop)
            {
                Name = "LatencyProbeThread",
                IsBackground = true
            };
            _probeThread.Start(_cts.Token);

            Logger.Info(
                "Latency probe service started. Interval: {0}ms, Timeout: {1}ms, Target: {2}",
                _probeIntervalMs,
                RequestTimeoutMs,
                _baseUrl);
        }

        /// <summary>
        /// Stops the latency probe service.
        /// </summary>
        public void Stop()
        {
            if (_cts != null)
            {
                _cts.Cancel();
            }

            // Give the thread a moment to exit gracefully
            if (_probeThread != null)
            {
                _probeThread.Join(TimeSpan.FromSeconds(2));
            }

            Logger.Info("Latency probe service stopped");
        }

        /// <summary>
        /// Main probe loop running on a dedicated thread.
        /// </summary>
        private void ProbeLoop(object state)
        {
            var cancellationToken = (CancellationToken)state;

            // Wait for the server to fully start and accept connections
            Thread.Sleep(5000);

            // Get the base URL
            var baseUrl = _baseUrl ?? GetProbeBaseUrl();
            Logger.Info("Latency probe targeting: {0}/api/health/probe", baseUrl);

            // Create HttpClient for probes
            using (var httpClient = new HttpClient())
            {
                httpClient.BaseAddress = new Uri(baseUrl);
                httpClient.Timeout = TimeSpan.FromMilliseconds(RequestTimeoutMs);
                httpClient.DefaultRequestHeaders.Add("User-Agent", "LatencyProbe/1.0");

                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        // Check if application is idle - don't send probes when idle
                        if (_idleStateService.IsIdle)
                        {
                            Thread.Sleep(1000);
                            continue;
                        }

                        // Check if Slow Request simulation is running
                        var isSlowRequestActive = _simulationTracker.GetActiveCountByType(SimulationType.SlowRequest) > 0;

                        if (isSlowRequestActive)
                        {
                            // Run sparsely during slow request simulation
                            Thread.Sleep(5000);
                        }

                        var result = MeasureLatency(httpClient, cancellationToken);

                        // Broadcast to all connected clients
                        BroadcastLatency(result);

                        // Normal interval wait
                        if (!isSlowRequestActive)
                        {
                            Thread.Sleep(_probeIntervalMs);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, "Error in latency probe loop");
                        Thread.Sleep(_probeIntervalMs);
                    }
                }
            }
        }

        /// <summary>
        /// Measures latency to the probe endpoint.
        /// </summary>
        private LatencyMeasurement MeasureLatency(HttpClient httpClient, CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            var timestamp = DateTimeOffset.UtcNow;
            var isTimeout = false;
            var isError = false;
            string errorMessage = null;

            try
            {
                // Use synchronous HTTP call since we're on a dedicated thread
                var response = httpClient.GetAsync("/api/health/probe", cancellationToken)
                    .ConfigureAwait(false)
                    .GetAwaiter()
                    .GetResult();

                stopwatch.Stop();

                if (!response.IsSuccessStatusCode)
                {
                    isError = true;
                    errorMessage = string.Format("HTTP {0}", (int)response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                stopwatch.Stop();

                if (ex is TaskCanceledException || 
                   (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    isTimeout = true;
                    Logger.Warn("Probe request timed out/cancelled");
                }
                else
                {
                    isError = true;
                    errorMessage = ex.Message;
                    Logger.Warn(ex, "Probe request failed");
                }
            }

            // Flag as timeout if elapsed time exceeds threshold
            if (!isTimeout && stopwatch.ElapsedMilliseconds >= RequestTimeoutMs)
            {
                isTimeout = true;
                Logger.Warn("Probe request exceeded timeout threshold: {0}ms >= {1}ms", 
                    stopwatch.ElapsedMilliseconds, RequestTimeoutMs);
            }

            return new LatencyMeasurement
            {
                Timestamp = timestamp,
                LatencyMs = stopwatch.ElapsedMilliseconds,
                IsTimeout = isTimeout,
                IsError = isError,
                ErrorMessage = errorMessage
            };
        }

        /// <summary>
        /// Broadcasts latency measurement to all connected SignalR clients.
        /// </summary>
        private void BroadcastLatency(LatencyMeasurement measurement)
        {
            try
            {
                var hubContext = GlobalHost.ConnectionManager.GetHubContext<MetricsHub>();
                hubContext.Clients.All.receiveLatency(measurement);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error broadcasting latency measurement");
            }
        }

        /// <summary>
        /// Gets the base URL for the probe endpoint.
        /// </summary>
        private string GetProbeBaseUrl()
        {
            // Check if running in Azure App Service
            var websiteHostname = Environment.GetEnvironmentVariable("WEBSITE_HOSTNAME");
            if (!string.IsNullOrEmpty(websiteHostname))
            {
                Logger.Info("Detected Azure App Service environment: {0}", websiteHostname);
                return "https://" + websiteHostname;
            }

            // Check if running in a container
            var containerHostname = Environment.GetEnvironmentVariable("CONTAINER_APP_HOSTNAME");
            if (!string.IsNullOrEmpty(containerHostname))
            {
                Logger.Info("Detected Container Apps environment: {0}", containerHostname);
                return "https://" + containerHostname;
            }

            // Default to localhost:5000 for self-hosted local development (matches Program.cs)
            return "http://localhost:5000";
        }

        /// <summary>
        /// Disposes resources.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Stop();
            if (_cts != null)
            {
                _cts.Dispose();
            }
            GC.SuppressFinalize(this);
        }
    }
}
