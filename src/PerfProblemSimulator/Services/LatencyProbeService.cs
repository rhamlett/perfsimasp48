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
        private volatile bool _forceUrlRecheck;

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

            // Subscribe to wake-up events to force immediate URL recheck
            _idleStateService.WakingUp += OnWakingUp;

            // Apply safety limit: minimum 100ms (10 probes/sec max)
            var configuredInterval = ConfigurationHelper.LatencyProbeIntervalMs;
            _probeIntervalMs = Math.Max(100, configuredInterval);
        }

        /// <summary>
        /// Called when the application wakes up from idle state.
        /// Forces an immediate URL recheck on the next probe cycle.
        /// </summary>
        private void OnWakingUp(object sender, EventArgs e)
        {
            _forceUrlRecheck = true;
            Logger.Debug("Waking from idle - will recheck probe URL on next cycle");
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

            // Track current URL and HttpClient - will be recreated if URL changes
            string currentBaseUrl = null;
            HttpClient httpClient = null;
            DateTime lastUrlCheck = DateTime.MinValue;
            const int UrlRecheckSeconds = 30; // Recheck URL every 30s when on localhost
            bool wasIdle = false; // Track if we were idle in previous iteration

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        // Check if application is idle - don't send probes when idle
                        if (_idleStateService.IsIdle)
                        {
                            if (!wasIdle)
                            {
                                wasIdle = true;
                            }
                            // Wait on the wake signal with a timeout - this allows immediate wake-up
                            // when the signal is set, rather than sleeping for the full duration
                            _idleStateService.WakeSignal.Wait(1000, cancellationToken);
                            continue;
                        }

                        // Check if we just woke up from idle
                        if (wasIdle)
                        {
                            wasIdle = false;
                            _forceUrlRecheck = true; // Force URL recheck on wake
                        }

                        // Resolve URL on first run, when waking from idle, or periodically if on localhost
                        var now = DateTime.UtcNow;
                        var forceRecheck = _forceUrlRecheck;
                        if (forceRecheck) _forceUrlRecheck = false;
                        
                        var shouldRecheckUrl = currentBaseUrl == null 
                            || forceRecheck
                            || (IsLocalhostUrl(currentBaseUrl) && (now - lastUrlCheck).TotalSeconds >= UrlRecheckSeconds);

                        if (shouldRecheckUrl)
                        {
                            lastUrlCheck = now;
                            var newBaseUrl = GetProbeBaseUrl();

                            if (currentBaseUrl != newBaseUrl)
                            {
                                // URL changed - recreate HttpClient
                                if (httpClient != null)
                                {
                                    httpClient.Dispose();
                                }

                                currentBaseUrl = newBaseUrl;
                                httpClient = new HttpClient
                                {
                                    BaseAddress = new Uri(currentBaseUrl),
                                    Timeout = TimeSpan.FromMilliseconds(RequestTimeoutMs)
                                };
                                httpClient.DefaultRequestHeaders.Add("User-Agent", "LatencyProbe/1.0");

                                Logger.Info("Latency probe targeting: {0}/api/health/probe", currentBaseUrl);
                            }
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
            finally
            {
                if (httpClient != null)
                {
                    httpClient.Dispose();
                }
            }
        }

        /// <summary>
        /// Checks if the URL is a localhost URL.
        /// </summary>
        private static bool IsLocalhostUrl(string url)
        {
            return url != null && (url.Contains("localhost") || url.Contains("127.0.0.1"));
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
        /// <remarks>
        /// Priority order:
        /// 1. WEBSITE_HOSTNAME - Full Azure App Service hostname (e.g., myapp.azurewebsites.net)
        /// 2. WEBSITE_SITE_NAME - Construct URL from site name (e.g., myapp → https://myapp.azurewebsites.net)
        /// 3. CONTAINER_APP_HOSTNAME - Azure Container Apps hostname
        /// 4. Localhost fallback for local development only
        /// 
        /// IMPORTANT: In Azure, probes MUST go through the public URL to be visible in AppLens.
        /// Localhost requests bypass the Azure frontend and won't appear in diagnostics.
        /// </remarks>
        private string GetProbeBaseUrl()
        {
            // Check if running in Azure App Service via WEBSITE_HOSTNAME
            var websiteHostname = Environment.GetEnvironmentVariable("WEBSITE_HOSTNAME");
            if (!string.IsNullOrEmpty(websiteHostname))
            {
                var url = "https://" + websiteHostname;
                Logger.Info("Using Azure App Service URL for probes: {0} (from WEBSITE_HOSTNAME)", url);
                return url;
            }

            // Fallback: construct URL from WEBSITE_SITE_NAME if available
            var websiteSiteName = Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME");
            if (!string.IsNullOrEmpty(websiteSiteName))
            {
                var url = "https://" + websiteSiteName + ".azurewebsites.net";
                Logger.Info("Using constructed Azure App Service URL for probes: {0} (from WEBSITE_SITE_NAME)", url);
                return url;
            }

            // Check if running in Azure Container Apps
            var containerHostname = Environment.GetEnvironmentVariable("CONTAINER_APP_HOSTNAME");
            if (!string.IsNullOrEmpty(containerHostname))
            {
                var url = "https://" + containerHostname;
                Logger.Info("Using Azure Container Apps URL for probes: {0}", url);
                return url;
            }

            // Local development fallback - warn that probes won't be visible in AppLens
            Logger.Warn("No Azure environment detected (WEBSITE_HOSTNAME, WEBSITE_SITE_NAME, CONTAINER_APP_HOSTNAME all empty). " +
                "Using localhost:5000 - probes will NOT be visible in AppLens or Azure diagnostics.");
            return "http://localhost:5000";
        }

        /// <summary>
        /// Disposes resources.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Unsubscribe from events
            _idleStateService.WakingUp -= OnWakingUp;

            Stop();
            if (_cts != null)
            {
                _cts.Dispose();
            }
            GC.SuppressFinalize(this);
        }
    }
}
