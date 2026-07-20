using HaPersonalAgent;
using HaPersonalAgent.Agent;
using HaPersonalAgent.Configuration;
using HaPersonalAgent.Confirmation;
using HaPersonalAgent.Dialogue;
using HaPersonalAgent.HomeAssistant;
using HaPersonalAgent.Memory;
using HaPersonalAgent.Storage;
using HaPersonalAgent.Telegram;
using HaPersonalAgent.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// HPA-025: switched from the plain generic host to WebApplication so the add-on can host an
// embedded ASP.NET Core (Kestrel) web server for the Web UI + JSON API, in the SAME process as the
// worker + Telegram gateway. The generic-host services (hosted services, options, DI) all work the
// same on WebApplicationBuilder; the `ask` CLI path stays server-less (it never calls app.Run()).
var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddFilter("ModelContextProtocol.Client.McpClient", LogLevel.Warning);

builder.Configuration
    .AddHomeAssistantAddOnOptions()
    .AddEnvironmentVariables(prefix: "HA_PERSONAL_AGENT_")
    .AddAgentEnvironmentOverrides();

builder.Services.AddAgentConfiguration(builder.Configuration);
builder.Services.AddAgentRuntime();
builder.Services.AddAgentStorage();
builder.Services.AddConfirmationServices();
builder.Services.AddDialogueServices();
builder.Services.AddHomeAssistantMcp();
builder.Services.AddMemoryMcp();
builder.Services.AddTelegramGateway();
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<MemoryMcpBackfillService>();

// Bind Kestrel to the ingress port (only when the Web UI is enabled) before the app is built.
builder.ConfigureAgentWebHost();

var host = builder.Build();

if (args.Length > 0 && string.Equals(args[0], "ask", StringComparison.OrdinalIgnoreCase))
{
    var message = string.Join(' ', args.Skip(1));
    if (string.IsNullOrWhiteSpace(message))
    {
        Console.Error.WriteLine("Usage: dotnet run --project src/HaPersonalAgent/HaPersonalAgent.csproj -- ask \"message\"");
        Environment.ExitCode = 2;
        return;
    }

    var runtime = host.Services.GetRequiredService<IAgentRuntime>();
    var response = await runtime.SendAsync(
        message,
        AgentContext.Create(),
        onReasoningUpdate: null,
        CancellationToken.None);

    Console.WriteLine(response.Text);
    return;
}

host.MapAgentWebEndpoints();

host.Run();
