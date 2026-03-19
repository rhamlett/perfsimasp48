// =============================================================================
// Local Development Host for Performance Problem Simulator
// =============================================================================
// This console app hosts the main library using OWIN self-host for local debugging.
// The main PerfProblemSimulator project (Library) deploys to Azure App Service.
// =============================================================================

using System;
using Microsoft.Owin.Hosting;

namespace PerfProblemSimulator.LocalHost
{
    class Program
    {
        static void Main(string[] args)
        {
            var baseUrl = "http://localhost:5000";
            
            if (args.Length > 0 && int.TryParse(args[0], out int port))
            {
                baseUrl = $"http://localhost:{port}";
            }

            Console.WriteLine("Starting Performance Problem Simulator (Local Host)...");
            Console.WriteLine($"Base URL: {baseUrl}");
            
            using (WebApp.Start<Startup>(baseUrl))
            {
                Console.WriteLine();
                Console.WriteLine("=================================================");
                Console.WriteLine("Performance Problem Simulator - Local Development");
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
