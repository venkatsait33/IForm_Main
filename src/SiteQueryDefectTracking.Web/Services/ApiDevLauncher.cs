using System.Diagnostics;

namespace SiteQueryDefectTracking.Web.Services;

public static class ApiDevLauncher
{
    public static async Task EnsureRunningAsync(IConfiguration configuration, ILogger logger)
    {
        var baseUrl = configuration["Api:BaseUrl"] ?? "http://localhost:5170";
        if (!IsLocalUrl(baseUrl)) return;
        if (SkipAutoStart()) return;
        var port = GetPort(baseUrl);

        if (port == GetSelfPort())
        {
            logger.LogWarning(
                "Api:BaseUrl ({BaseUrl}) points at the Web app's own port. API calls will be routed to the Web app itself and fail; set Api:BaseUrl to the API project's port (e.g. http://localhost:5170).",
                baseUrl);
            return;
        }

        // Port-only detection: no HTTP calls, so no HttpClient timeout exceptions spam the debugger.
        if (IsPortListening(port)) return;

        var apiProject = FindApiProject();
        if (apiProject is null)
        {
            logger.LogWarning("API not running at {BaseUrl} and the API project could not be located; skipping auto-start.", baseUrl);
            return;
        }

        logger.LogInformation("API not running at {BaseUrl}. Starting LocalDB and launching the API first...", baseUrl);
        StartLocalDb();
        StartApi(apiProject, logger);

        var deadline = DateTime.UtcNow.AddSeconds(180);
        while (DateTime.UtcNow < deadline)
        {
            if (IsPortListening(port))
            {
                // The API seeds before Kestrel binds, so the listener being up means it is ready.
                await Task.Delay(800);
                logger.LogInformation("API is now running at {BaseUrl}.", baseUrl);
                return;
            }
            await Task.Delay(1000);
        }

        logger.LogWarning("API did not respond at {BaseUrl} within 180 seconds; starting the Web app anyway.", baseUrl);
    }

    private static bool IsLocalUrl(string baseUrl)
        => baseUrl.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase)
        || baseUrl.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase);

    private static bool SkipAutoStart()
    {
        var value = Environment.GetEnvironmentVariable("SQD_SKIP_API_AUTOSTART");
        return value is "1" or "true" or "TRUE";
    }

    private static int GetPort(string baseUrl)
    {
        try { return new Uri(baseUrl).Port; } catch { return 5170; }
    }

    private static int GetSelfPort()
    {
        var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        if (string.IsNullOrWhiteSpace(urls)) return 0;
        foreach (var item in urls.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { return new Uri(item.Trim()).Port; } catch { }
        }
        return 0;
    }

    private static bool IsPortListening(int port)
    {
        try
        {
            foreach (var ep in System.Net.NetworkInformation.IPGlobalProperties
                         .GetIPGlobalProperties().GetActiveTcpListeners())
            {
                if (ep.Port == port) return true;
            }
        }
        catch
        {
        }
        return false;
    }

    private static string? FindApiProject()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, "src", "SiteQueryDefectTracking.Api", "SiteQueryDefectTracking.Api.csproj");
                if (File.Exists(candidate)) return candidate;

                candidate = Path.Combine(dir.FullName, "SiteQueryDefectTracking.Api.csproj");
                if (File.Exists(candidate)) return candidate;

                dir = dir.Parent;
            }
        }
        return null;
    }

    private static void StartLocalDb()
    {
        try
        {
            var psi = new ProcessStartInfo("sqllocaldb", "start MSSQLLocalDB")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            process?.WaitForExit(10000);
        }
        catch
        {
        }
    }

    private static void StartApi(string apiProject, ILogger logger)
    {
        var apiDir = Path.GetDirectoryName(apiProject)!;
        var dotnet = FindDotNet();

        var buildOk = TryBuildApi(dotnet, apiProject, logger);

        var psi = new ProcessStartInfo
        {
            FileName = dotnet,
            WorkingDirectory = apiDir,
            UseShellExecute = false,
            CreateNoWindow = false
        };
        psi.EnvironmentVariables["ConnectionStrings__DefaultConnection"] =
            "Server=(localdb)\\MSSQLLocalDB;Database=SiteQueryDefectTrackingDev;Integrated Security=True;TrustServerCertificate=True";
        psi.EnvironmentVariables["ASPNETCORE_URLS"] = "http://localhost:5170";
        psi.EnvironmentVariables["ASPNETCORE_ENVIRONMENT"] = "Development";
        psi.EnvironmentVariables["ASPNETCORE_PREVENTHOSTINGSTARTUP"] = "true";
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add(apiProject);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("Debug");
        if (buildOk) psi.ArgumentList.Add("--no-build");
        Process.Start(psi);
    }

    private static bool TryBuildApi(string dotnet, string apiProject, ILogger logger)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = dotnet,
                WorkingDirectory = Path.GetDirectoryName(apiProject)!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("build");
            psi.ArgumentList.Add(apiProject);
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("Debug");
            if (IsApiOutputPoisoned(apiProject))
            {
                // A Visual Studio build injects a Microsoft.WebTools.ApiEndpointDiscovery entry into the
                // API's deps.json that makes a bare `dotnet run`/`dotnet dll` launch crash immediately.
                // An incremental CLI build skips deps.json regeneration, so force a full rebuild here.
                psi.ArgumentList.Add("--no-incremental");
                logger.LogInformation("Auto-start API: Visual Studio deps.json detected; forcing a full CLI rebuild.");
            }
            // Avoid contending with the MSBuild server nodes Visual Studio holds open.
            psi.ArgumentList.Add("--disable-build-servers");
            using var process = Process.Start(psi);
            if (process is null) return false;
            process.WaitForExit(300000);
            if (process.ExitCode != 0)
            {
                logger.LogWarning("Auto-start API: CLI build failed with exit code {ExitCode}.", process.ExitCode);
                return false;
            }
            return !IsApiOutputPoisoned(apiProject);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Auto-start API: CLI build failed ({Message}); continuing with any existing output.", ex.Message);
            return false;
        }
    }

    private static bool IsApiOutputPoisoned(string apiProject)
    {
        try
        {
            var apiDir = Path.GetDirectoryName(apiProject)!;
            var depsPath = Path.Combine(apiDir, "bin", "Debug", "net10.0", "SiteQueryDefectTracking.Api.deps.json");
            if (!File.Exists(depsPath)) return true;
            var content = File.ReadAllText(depsPath);
            return content.Contains("WebTools", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    private static string FindDotNet()
    {
        var values = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';');
        foreach (var item in values)
        {
            var candidate = Path.Combine(item.Trim('"'), "dotnet.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return "dotnet";
    }
}
