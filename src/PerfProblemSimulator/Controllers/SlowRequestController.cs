using NLog;
using PerfProblemSimulator.Models;
using PerfProblemSimulator.Services;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Web.Http;
using System.Web.Http.Description;

namespace PerfProblemSimulator.Controllers
{
    /// <summary>
    /// Controller for slow request simulation to demonstrate CLR Profiler diagnosis.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Educational Note:</strong> This controller simulates slow requests that are ideal
    /// for CLR Profiler analysis. Unlike CPU or memory issues, blocking problems show:
    /// </para>
    /// <list type="bullet">
    /// <item>Low CPU usage (threads are blocked/sleeping, not working)</item>
    /// <item>Normal memory usage</item>
    /// <item>Slow response times</item>
    /// <item>Time spent in Thread.Sleep visible in profiler call stacks</item>
    /// </list>
    /// <para>
    /// <strong>How to Use:</strong>
    /// </para>
    /// <list type="number">
    /// <item>Start the slow request simulation</item>
    /// <item>In Azure Portal: Diagnose and Solve Problems → Collect a .NET Profiler Trace</item>
    /// <item>Or use dotnet-trace: <c>dotnet-trace collect -p {PID} --duration 00:01:00</c></item>
    /// <item>Analyze the trace to find blocking methods by their descriptive names</item>
    /// </list>
    /// </remarks>
    [RoutePrefix("api/slowrequest")]
    public class SlowRequestController : ApiController
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly ISlowRequestService _slowRequestService;

        public SlowRequestController(ISlowRequestService slowRequestService)
        {
            _slowRequestService = slowRequestService ?? throw new ArgumentNullException(nameof(slowRequestService));
        }

        /// <summary>
        /// Starts the slow request simulation.
        /// </summary>
        /// <param name="request">Configuration for the simulation.</param>
        /// <returns>Information about the started simulation.</returns>
        /// <response code="200">Simulation started successfully</response>
        [HttpPost]
        [Route("start")]
        [ResponseType(typeof(SimulationResult))]
        public IHttpActionResult Start([FromBody] SlowRequestRequest request)
        {
            request = request ?? new SlowRequestRequest();

            Logger.Warn(
                "🐌 Starting slow request simulation: Duration={0}s, Interval={1}s",
                request.RequestDurationSeconds,
                request.IntervalSeconds);

            var result = _slowRequestService.Start(request);
            return Ok(result);
        }

        /// <summary>
        /// Stops the slow request simulation.
        /// </summary>
        /// <returns>Summary of the simulation run.</returns>
        /// <response code="200">Simulation stopped</response>
        [HttpPost]
        [Route("stop")]
        [ResponseType(typeof(SimulationResult))]
        public IHttpActionResult Stop()
        {
            Logger.Info("🛑 Stopping slow request simulation");
            var result = _slowRequestService.Stop();
            return Ok(result);
        }

        /// <summary>
        /// Gets the current status of the slow request simulation.
        /// </summary>
        /// <returns>Current simulation status including request counts.</returns>
        /// <response code="200">Current status</response>
        [HttpGet]
        [Route("status")]
        [ResponseType(typeof(SlowRequestStatus))]
        public IHttpActionResult GetStatus()
        {
            var status = _slowRequestService.GetStatus();
            return Ok(status);
        }

        /// <summary>
        /// Gets information about the slow request scenarios.
        /// </summary>
        /// <returns>Description of each scenario and what to look for in CLR Profiler.</returns>
        /// <response code="200">Scenario information</response>
        [HttpGet]
        [Route("scenarios")]
        [ResponseType(typeof(Dictionary<string, ScenarioInfo>))]
        public IHttpActionResult GetScenarios()
        {
            var scenarios = new Dictionary<string, ScenarioInfo>
            {
                ["SimpleSyncOverAsync"] = new ScenarioInfo
                {
                    Name = "Simple Blocking",
                    Description = "Direct Thread.Sleep blocking calls that consume time in profiler",
                    WhatProfilerShows = "Time spent in Thread.Sleep - clearly visible as method self-time",
                    MethodsToLookFor = new[]
                    {
                        "FetchDataSync_BLOCKING_HERE",
                        "ProcessDataSync_BLOCKING_HERE",
                        "SaveDataSync_BLOCKING_HERE"
                    }
                },
                ["NestedSyncOverAsync"] = new ScenarioInfo
                {
                    Name = "Nested Blocking Methods",
                    Description = "Chain of sync methods that each block internally using Thread.Sleep",
                    WhatProfilerShows = "Nested blocking calls - sync methods calling other sync methods that block",
                    MethodsToLookFor = new[]
                    {
                        "ValidateOrderSync_BLOCKS_INTERNALLY",
                        "CheckInventorySync_BLOCKS_INTERNALLY",
                        "ProcessPaymentSync_BLOCKS_INTERNALLY",
                        "SendConfirmationSync_BLOCKS_INTERNALLY"
                    }
                },
                ["DatabasePattern"] = new ScenarioInfo
                {
                    Name = "Database/HTTP Pattern",
                    Description = "Simulated database and HTTP blocking calls",
                    WhatProfilerShows = "Time spent in methods simulating database queries and HTTP calls",
                    MethodsToLookFor = new[]
                    {
                        "GetCustomerFromDatabaseSync_SYNC_BLOCK",
                        "GetOrderHistoryFromDatabaseSync_SYNC_BLOCK",
                        "CheckInventoryServiceSync_SYNC_BLOCK",
                        "GetRecommendationsFromMLServiceSync_SYNC_BLOCK",
                        "BuildResponseSync_SYNC_BLOCK"
                    }
                }
            };

            return Ok(scenarios);
        }

        /// <summary>
        /// HTTP endpoint that simulates a slow blocking request.
        /// </summary>
        /// <param name="durationSeconds">How long the request should take (default: 25 seconds).</param>
        /// <param name="scenario">The scenario name for logging (optional).</param>
        /// <returns>Result after the blocking delay completes.</returns>
        /// <response code="200">Request completed after blocking</response>
        [HttpGet]
        [Route("execute-slow")]
        [ResponseType(typeof(SlowRequestResult))]
        public IHttpActionResult ExecuteSlowRequest(int durationSeconds = 25, string scenario = null)
        {
            var startTime = DateTimeOffset.UtcNow;
            
            Logger.Warn("🐌 HTTP slow request started: {0}s, Scenario: {1}", 
                durationSeconds, scenario ?? "Direct");
            
            // Execute the appropriate blocking pattern based on the scenario
            switch (scenario)
            {
                case "SimpleSyncOverAsync":
                    FetchDataSync_BLOCKING_HERE(durationSeconds);
                    break;
                case "NestedSyncOverAsync":
                    ValidateOrderSync_BLOCKS_INTERNALLY(durationSeconds);
                    break;
                case "DatabasePattern":
                    GetCustomerFromDatabaseSync_SYNC_BLOCK(durationSeconds);
                    break;
                default:
                    // Default fallback
                    Task.Delay(durationSeconds * 1000).Wait();
                    break;
            }
            
            var elapsed = DateTimeOffset.UtcNow - startTime;
            
            Logger.Warn("🐌 HTTP slow request completed: {0}s", elapsed.TotalSeconds);
            
            return Ok(new SlowRequestResult
            {
                Message = $"Slow request completed after {elapsed.TotalSeconds:F1} seconds",
                DurationSeconds = elapsed.TotalSeconds,
                Scenario = scenario ?? "Direct",
                StartedAt = startTime,
                CompletedAt = DateTimeOffset.UtcNow
            });
        }

        // =================================================================================
        // SCENARIO 1: Simple Blocking
        // =================================================================================
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void FetchDataSync_BLOCKING_HERE(int duration)
        {
            Task.Delay(duration * 1000).Wait();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ProcessDataSync_BLOCKING_HERE() { } 

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void SaveDataSync_BLOCKING_HERE() { }

        // =================================================================================
        // SCENARIO 2: Nested Blocking
        // =================================================================================
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ValidateOrderSync_BLOCKS_INTERNALLY(int duration)
        {
            CheckInventorySync_BLOCKS_INTERNALLY(duration);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void CheckInventorySync_BLOCKS_INTERNALLY(int duration)
        {
            ProcessPaymentSync_BLOCKS_INTERNALLY(duration);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ProcessPaymentSync_BLOCKS_INTERNALLY(int duration)
        {
            Task.Delay(duration * 1000).Wait();
        }

        // =================================================================================
        // SCENARIO 3: Database Pattern
        // =================================================================================
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void GetCustomerFromDatabaseSync_SYNC_BLOCK(int duration)
        {
            int partDuration = duration / 3;
            Task.Delay(partDuration * 1000).Wait();
            GetOrderHistoryFromDatabaseSync_SYNC_BLOCK(partDuration);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void GetOrderHistoryFromDatabaseSync_SYNC_BLOCK(int duration)
        {
            Task.Delay(duration * 1000).Wait();
            BuildResponseSync_SYNC_BLOCK(duration);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void BuildResponseSync_SYNC_BLOCK(int duration)
        {
            Task.Delay(duration * 1000).Wait();
        }
    }

    /// <summary>
    /// Result from a slow request execution.
    /// </summary>
    public class SlowRequestResult
    {
        public string Message { get; set; } = "";
        public double DurationSeconds { get; set; }
        public string Scenario { get; set; } = "";
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset CompletedAt { get; set; }
    }

    /// <summary>
    /// Information about a slow request scenario.
    /// </summary>
    public class ScenarioInfo
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string WhatProfilerShows { get; set; } = "";
        public string[] MethodsToLookFor { get; set; } = new string[0];
    }
}
