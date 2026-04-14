using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Owin;
using NLog;
using PerfProblemSimulator.App_Start;

namespace PerfProblemSimulator.Services
{
    /// <summary>
    /// OWIN middleware that serves translated HTML documents when available.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For a request to "documentation.html" when UI_LANGUAGE is "es",
    /// this middleware checks if "documentation.es.html" exists in wwwroot.
    /// If it does, the request path is rewritten to serve the translated version.
    /// If not, the original English file is served as-is.
    /// </para>
    /// <para>
    /// This middleware runs before UseStaticFiles so the rewritten path
    /// is picked up by the static file handler.
    /// </para>
    /// <para>
    /// <strong>PORTING NOTE from ASP.NET Core:</strong>
    /// In ASP.NET Core, this uses RequestDelegate + HttpContext middleware pattern.
    /// In .NET Framework 4.8, this inherits from OwinMiddleware and is registered
    /// via app.Use&lt;TranslatedHtmlMiddleware&gt;().
    /// </para>
    /// </remarks>
    public class TranslatedHtmlMiddleware : OwinMiddleware
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly string _wwwrootPath;
        private readonly string _uiLanguage;

        public TranslatedHtmlMiddleware(OwinMiddleware next) : base(next)
        {
            _uiLanguage = ConfigurationHelper.UiLanguage;

            var rootPath = AppDomain.CurrentDomain.BaseDirectory;
            _wwwrootPath = Path.Combine(rootPath, "wwwroot");
            if (!Directory.Exists(_wwwrootPath))
            {
                _wwwrootPath = Path.Combine(rootPath, "bin", "wwwroot");
            }
        }

        /// <summary>
        /// OWIN middleware invoke method.
        /// </summary>
        public override Task Invoke(IOwinContext context)
        {
            // Only rewrite if language is not English
            if (!_uiLanguage.Equals("en", StringComparison.OrdinalIgnoreCase))
            {
                var requestPath = context.Request.Path.Value ?? "";

                // Only intercept .html file requests (not API, hubs, etc.)
                if (requestPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                {
                    // Build the translated file name: documentation.html → documentation.es.html
                    var relativePath = requestPath.TrimStart('/');
                    var nameWithoutExt = Path.GetFileNameWithoutExtension(relativePath);
                    var dir = Path.GetDirectoryName(relativePath) ?? "";
                    var translatedFileName = $"{nameWithoutExt}.{_uiLanguage}.html";
                    var translatedRelativePath = string.IsNullOrEmpty(dir)
                        ? translatedFileName
                        : Path.Combine(dir, translatedFileName);

                    var translatedFullPath = Path.Combine(_wwwrootPath, translatedRelativePath);

                    if (File.Exists(translatedFullPath))
                    {
                        // Rewrite the request path to serve the translated file
                        context.Request.Path = new PathString("/" + translatedRelativePath.Replace('\\', '/'));
                    }
                }
            }

            return Next.Invoke(context);
        }
    }
}
