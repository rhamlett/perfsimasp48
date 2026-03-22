using System;
using System.Collections.Generic;
using PerfProblemSimulator.Models;

namespace PerfProblemSimulator.Services
{
    /// <summary>
    /// Interface for tracking simulation events to Application Insights.
    /// </summary>
    public interface ISimulationTelemetry
    {
        /// <summary>
        /// Gets whether Application Insights telemetry is enabled.
        /// </summary>
        bool IsEnabled { get; }

        /// <summary>
        /// Tracks a simulation started event.
        /// </summary>
        /// <param name="simulationId">The unique simulation ID.</param>
        /// <param name="simulationType">The type of simulation.</param>
        /// <param name="parameters">Optional parameters to include in the event.</param>
        void TrackSimulationStarted(Guid simulationId, SimulationType simulationType, IDictionary<string, object> parameters = null);

        /// <summary>
        /// Tracks a simulation ended event.
        /// </summary>
        /// <param name="simulationId">The unique simulation ID.</param>
        /// <param name="simulationType">The type of simulation.</param>
        /// <param name="status">The final status (Completed, Cancelled, Failed).</param>
        void TrackSimulationEnded(Guid simulationId, SimulationType simulationType, string status);
    }
}
