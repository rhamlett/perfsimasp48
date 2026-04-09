using System;
using System.Collections.Generic;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using NLog;
using PerfProblemSimulator.Models;

namespace PerfProblemSimulator.Services
{
    /// <summary>
    /// Tracks simulation events to Application Insights.
    /// Auto-configures from APPLICATIONINSIGHTS_CONNECTION_STRING environment variable.
    /// No-ops gracefully if Application Insights is not configured.
    /// </summary>
    public class SimulationTelemetry : ISimulationTelemetry
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly TelemetryClient _telemetryClient;
        private readonly bool _isEnabled;

        /// <summary>
        /// Initializes the telemetry service.
        /// Uses the shared TelemetryConfiguration.Active (configured by AppInsightsConfig)
        /// Reads APPLICATIONINSIGHTS_CONNECTION_STRING and creates a TelemetryClient.
        /// </summary>
        public SimulationTelemetry()
        {
            var connectionString = Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING");

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                Logger.Info("Application Insights not configured (APPLICATIONINSIGHTS_CONNECTION_STRING not set). Telemetry disabled.");
                _isEnabled = false;
                _telemetryClient = null;
                return;
            }

            try
            {
                var config = new TelemetryConfiguration
                {
                    ConnectionString = connectionString
                };
                _telemetryClient = new TelemetryClient(config);
                _isEnabled = true;
                Logger.Info("Application Insights telemetry enabled. SimulationStarted/SimulationEnded events will be tracked.");
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to initialize Application Insights. Telemetry disabled.");
                _isEnabled = false;
                _telemetryClient = null;
            }
        }

        /// <inheritdoc />
        public bool IsEnabled => _isEnabled;

        /// <inheritdoc />
        public void TrackSimulationStarted(Guid simulationId, SimulationType simulationType, IDictionary<string, object> parameters = null)
        {
            if (!_isEnabled || _telemetryClient == null)
                return;

            try
            {
                var properties = new Dictionary<string, string>
                {
                    ["SimulationId"] = simulationId.ToString(),
                    ["SimulationType"] = simulationType.ToString()
                };

                if (parameters != null)
                {
                    foreach (var kvp in parameters)
                    {
                        properties[$"Param_{kvp.Key}"] = kvp.Value?.ToString() ?? "(null)";
                    }
                }

                _telemetryClient.TrackEvent("SimulationStarted", properties);
                _telemetryClient.Flush();

                Logger.Debug("Tracked SimulationStarted event for {0} ({1})", simulationId, simulationType);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to track SimulationStarted event");
            }
        }

        /// <inheritdoc />
        public void TrackSimulationEnded(Guid simulationId, SimulationType simulationType, string status)
        {
            if (!_isEnabled || _telemetryClient == null)
                return;

            try
            {
                var properties = new Dictionary<string, string>
                {
                    ["SimulationId"] = simulationId.ToString(),
                    ["SimulationType"] = simulationType.ToString(),
                    ["Status"] = status
                };

                _telemetryClient.TrackEvent("SimulationEnded", properties);
                _telemetryClient.Flush();

                Logger.Debug("Tracked SimulationEnded event for {0} ({1}) - {2}", simulationId, simulationType, status);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to track SimulationEnded event");
            }
        }
    }
}
