// =============================================================================
// Performance Problem Simulator - .NET Framework 4.8 Version
// =============================================================================
// Self-hosted OWIN application for local development and debugging.
// =============================================================================

using System;
using Microsoft.Owin.Hosting;

namespace PerfProblemSimulator
{
    /// <summary>
    /// Entry point for self-hosted OWIN application.
    /// </summary>
    public class Program
    {
        /// <summary>
        /// Main entry point - starts the OWIN self-host server.
        /// </summary>
        public static void Main(string[] args)
        {
            var baseUrl = "http://localhost:5000";
            
            // Check for command line port override
            if (args.Length > 0 && int.TryParse(args[0], out int port))
            {
                baseUrl = $"http://localhost:{port}";
            }

            Console.WriteLine("Starting Performance Problem Simulator...");
            Console.WriteLine($"Base URL: {baseUrl}");
            
            using (WebApp.Start<Startup>(baseUrl))
            {
                Console.WriteLine();
                Console.WriteLine("=================================================");
                Console.WriteLine("Performance Problem Simulator - ASP.NET 4.8");
                Console.WriteLine("=================================================");
                Console.WriteLine($"Now listening on: {baseUrl}");
                Console.WriteLine();
                Console.WriteLine($"Dashboard:     {baseUrl}/");
                Console.WriteLine($"API Docs:      {baseUrl}/swagger");
                Console.WriteLine($"Health Check:  {baseUrl}/api/health");
                Console.WriteLine();
                Console.WriteLine("Press Enter to stop the server...");
                Console.ReadLine();
            }
        }
    }
}
