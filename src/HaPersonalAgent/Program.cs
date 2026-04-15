using HaPersonalAgent;
using HaPersonalAgent.Agent;
using HaPersonalAgent.Configuration;
using HaPersonalAgent.Confirmation;
using HaPersonalAgent.Dialogue;
using HaPersonalAgent.HomeAssistant;
using HaPersonalAgent.Storage;
using HaPersonalAgent.Telegram;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

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
builder.Services.AddTelegramGateway();
builder.Services.AddHostedService<Worker>();

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

host.Run();
