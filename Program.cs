using BlobInventoryDotNet.Services;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Azure.Identity;
using Azure.Core;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices((context, services) =>
    {
        // Application Insights telemetry (reads APPLICATIONINSIGHTS_CONNECTION_STRING env var)
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        // Allow synchronous IO for DurableClient.CreateCheckStatusResponse
        services.Configure<KestrelServerOptions>(options =>
        {
            options.AllowSynchronousIO = true;
        });

        // Register DefaultAzureCredential as singleton — used by all Azure SDK clients.
        // In production (on Azure), this resolves to Managed Identity.
        // Locally, it resolves to Azure CLI / Visual Studio credentials.
        services.AddSingleton<TokenCredential>(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<Program>>();
            logger.LogInformation("Initializing DefaultAzureCredential (Managed Identity in production)");
            return new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                ExcludeInteractiveBrowserCredential = true,
                ExcludeVisualStudioCodeCredential = false,
                ExcludeAzureCliCredential = false,
                // In production: only ManagedIdentityCredential and WorkloadIdentityCredential
                // are needed. Keeping others for local dev convenience.
            });
        });

        // Register the inventory service
        services.AddSingleton<IInventoryService, InventoryService>();
    })
    .ConfigureLogging(logging =>
    {
        logging.AddConsole();
        logging.SetMinimumLevel(LogLevel.Information);
    })
    .Build();

await host.RunAsync();

// Marker class needed by SDK tooling
public partial class Program { }
