using Swashbuckle.Application;
using System.Web.Http;

namespace PerfProblemSimulator.App_Start
{
    /// <summary>
    /// Swagger/OpenAPI documentation configuration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>PORTING NOTE from ASP.NET Core:</strong>
    /// This replaces builder.Services.AddSwaggerGen() in Program.cs.
    /// 
    /// The Swashbuckle.Core package works slightly differently from
    /// Swashbuckle.AspNetCore but provides similar functionality.
    /// </para>
    /// </remarks>
    public static class SwaggerConfig
    {
        /// <summary>
        /// Registers Swagger configuration with Web API.
        /// </summary>
        public static void Register(HttpConfiguration config)
        {
            config
                .EnableSwagger(c =>
                {
                    c.SingleApiVersion("v1", "Performance Problem Simulator API")
                        .Description(@"An educational tool for demonstrating and diagnosing Azure App Service performance problems.

⚠️ WARNING: This API intentionally creates performance problems. Use only in controlled environments.

## Simulation Types
- **CPU**: Triggers high CPU usage through parallel spin loops
- **Memory**: Allocates and holds memory to create memory pressure
- **ThreadBlock**: Simulates thread pool starvation via sync-over-async patterns
- **SlowRequest**: Generates long-running blocking requests for CLR Profiler training
- **Crash**: Triggers intentional crashes (OOM, StackOverflow) for Azure crash monitoring

## Safety Features
- Problem endpoints can be disabled via DISABLE_PROBLEM_ENDPOINTS environment variable
- Health endpoints remain responsive even under stress
- Real-time dashboard shows metrics and active simulations");

                    // Include XML comments if the file exists (may not be present in all deployment scenarios)
                    var xmlPath = GetXmlCommentsPath();
                    if (!string.IsNullOrEmpty(xmlPath) && System.IO.File.Exists(xmlPath))
                    {
                        c.IncludeXmlComments(xmlPath);
                    }

                    // Use camelCase for JSON property names
                    c.DescribeAllEnumsAsStrings();
                })
                .EnableSwaggerUi(c =>
                {
                    c.DocumentTitle("Performance Problem Simulator - ASP.NET 4.8");
                });
        }

        private static string GetXmlCommentsPath()
        {
            var baseDirectory = System.AppDomain.CurrentDomain.BaseDirectory;
            
            // Try multiple possible locations for the XML documentation file
            var possiblePaths = new[]
            {
                // Azure App Service / IIS deployment (XML alongside DLLs in bin)
                System.IO.Path.Combine(baseDirectory, "bin", "PerfProblemSimulator.xml"),
                // Alternative deployment structure (XML in root)
                System.IO.Path.Combine(baseDirectory, "PerfProblemSimulator.xml"),
                // Same directory as the executing assembly
                System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(typeof(SwaggerConfig).Assembly.Location) ?? baseDirectory,
                    "PerfProblemSimulator.xml")
            };
            
            foreach (var path in possiblePaths)
            {
                if (System.IO.File.Exists(path))
                {
                    return path;
                }
            }
            
            // Return null if not found - caller should check for existence
            return null;
        }
    }
}
