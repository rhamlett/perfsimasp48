using NLog;
using PerfProblemSimulator.Models;
using PerfProblemSimulator.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Web.Http;
using System.Web.Http.Description;

namespace PerfProblemSimulator.Controllers
{
    /// <summary>
    /// Controller for triggering intentional application crashes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Educational Note:</strong> This controller provides endpoints to intentionally
    /// crash the application in various ways. This is useful for learning:
    /// </para>
    /// <list type="bullet">
    /// <item>How to configure Azure App Service crash monitoring</item>
    /// <item>How to collect and analyze crash dumps</item>
    /// <item>Understanding different types of fatal errors in .NET</item>
    /// <item>Practicing crash dump analysis with WinDbg or Visual Studio</item>
    /// </list>
    /// <para>
    /// <strong>WARNING:</strong> These endpoints will terminate the application!
    /// Azure App Service will automatically restart the application after a crash.
    /// </para>
    /// </remarks>
    [RoutePrefix("api/crash")]
    public class CrashController : ApiController
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly ICrashService _crashService;

        public CrashController(ICrashService crashService)
        {
            _crashService = crashService ?? throw new ArgumentNullException(nameof(crashService));
        }

        /// <summary>
        /// Triggers an application crash of the specified type.
        /// </summary>
        /// <param name="request">The crash configuration.</param>
        /// <returns>Confirmation that the crash has been scheduled (response sent before crash occurs).</returns>
        /// <response code="200">Crash has been scheduled</response>
        /// <response code="400">Invalid crash configuration</response>
        /// <response code="503">Problem endpoints are disabled</response>
        [HttpPost]
        [Route("trigger")]
        [ResponseType(typeof(SimulationResult))]
        public IHttpActionResult TriggerCrash([FromBody] CrashRequest request)
        {
            request = request ?? new CrashRequest();

            // Validate delay
            if (request.DelaySeconds < 0 || request.DelaySeconds > 60)
            {
                return BadRequest("DelaySeconds must be between 0 and 60");
            }

            Logger.Fatal(
                "🚨 CRASH REQUESTED: Type={0}, Delay={1}s, Synchronous={2}",
                request.CrashType, request.DelaySeconds, request.Synchronous);

            // If synchronous, crash immediately (no response will be sent)
            if (request.Synchronous)
            {
                _crashService.TriggerCrash(request.CrashType, 0, request.Message, synchronous: true);
                // This line is never reached - the process crashes above
                return InternalServerError(new Exception("Crash failed"));
            }

            // Trigger the crash asynchronously
            _crashService.TriggerCrash(request.CrashType, request.DelaySeconds, request.Message, synchronous: false);

            var crashMessage = $"💥 {request.CrashType} crash scheduled! " +
                          (request.DelaySeconds > 0 
                              ? $"Crash will occur in {request.DelaySeconds} seconds." 
                              : "Crash will occur in ~100ms (after this response is sent).");

            return Ok(new SimulationResult
            {
                SimulationId = Guid.NewGuid(),
                Type = SimulationType.Crash,
                Status = "Scheduled",
                Message = crashMessage,
                ActualParameters = new Dictionary<string, object>
                {
                    ["CrashType"] = request.CrashType.ToString(),
                    ["DelaySeconds"] = request.DelaySeconds,
                    ["Synchronous"] = request.Synchronous
                }
            });
        }

        /// <summary>
        /// Triggers a synchronous crash optimized for Azure Crash Monitoring.
        /// </summary>
        /// <param name="crashType">Type of crash (default: FailFast)</param>
        /// <returns>No response - the process crashes before responding.</returns>
        /// <response code="500">Never returned - process crashes before response</response>
        [HttpGet]
        [HttpPost]
        [Route("now")]
        public IHttpActionResult CrashNow(CrashType crashType = CrashType.FailFast)
        {
            Logger.Fatal("🚨💥 IMMEDIATE CRASH REQUESTED: Type={0} - NO RESPONSE WILL BE SENT", crashType);
            
            _crashService.TriggerCrash(crashType, 0, "Immediate crash via /api/crash/now endpoint", synchronous: true);
            
            // Never reached
            return InternalServerError(new Exception("Crash failed"));
        }

        /// <summary>
        /// Gets information about available crash types.
        /// </summary>
        /// <returns>A dictionary of crash types and their descriptions.</returns>
        /// <response code="200">List of crash types and descriptions</response>
        [HttpGet]
        [Route("types")]
        [ResponseType(typeof(Dictionary<string, CrashTypeInfo>))]
        public IHttpActionResult GetCrashTypes()
        {
            var descriptions = _crashService.GetCrashTypeDescriptions();
            
            var result = descriptions.ToDictionary(
                kvp => kvp.Key.ToString(),
                kvp => new CrashTypeInfo
                {
                    Name = kvp.Key.ToString(),
                    Value = (int)kvp.Key,
                    Description = kvp.Value,
                    RecommendedForAzure = kvp.Key == CrashType.FailFast || kvp.Key == CrashType.StackOverflow
                });

            return Ok(result);
        }

        /// <summary>
        /// Triggers a FailFast crash (most common for Azure crash monitoring).
        /// </summary>
        /// <param name="delaySeconds">Optional delay before crash (0-60 seconds).</param>
        /// <returns>Confirmation that the crash has been scheduled.</returns>
        /// <response code="200">Crash has been scheduled</response>
        /// <response code="503">Problem endpoints are disabled</response>
        [HttpPost]
        [Route("failfast")]
        [ResponseType(typeof(SimulationResult))]
        public IHttpActionResult TriggerFailFast(int delaySeconds = 0)
        {
            return TriggerCrash(new CrashRequest
            {
                CrashType = CrashType.FailFast,
                DelaySeconds = Math.Max(0, Math.Min(60, delaySeconds)),
                Message = "FailFast triggered via /api/crash/failfast endpoint"
            });
        }

        /// <summary>
        /// Triggers a StackOverflow crash.
        /// </summary>
        /// <param name="delaySeconds">Optional delay before crash (0-60 seconds).</param>
        /// <returns>Confirmation that the crash has been scheduled.</returns>
        /// <response code="200">Crash has been scheduled</response>
        /// <response code="503">Problem endpoints are disabled</response>
        [HttpPost]
        [Route("stackoverflow")]
        [ResponseType(typeof(SimulationResult))]
        public IHttpActionResult TriggerStackOverflow(int delaySeconds = 0)
        {
            return TriggerCrash(new CrashRequest
            {
                CrashType = CrashType.StackOverflow,
                DelaySeconds = Math.Max(0, Math.Min(60, delaySeconds))
            });
        }
    }

    /// <summary>
    /// Information about a crash type.
    /// </summary>
    public class CrashTypeInfo
    {
        /// <summary>
        /// Name of the crash type.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Numeric value for the crash type enum.
        /// </summary>
        public int Value { get; set; }

        /// <summary>
        /// Detailed description of what this crash type does.
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Whether this crash type is recommended for Azure crash monitoring.
        /// </summary>
        public bool RecommendedForAzure { get; set; }
    }
}
