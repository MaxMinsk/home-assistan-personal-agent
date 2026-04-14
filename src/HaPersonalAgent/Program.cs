using HaPersonalAgent;
using HaPersonalAgent.Configuration;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration
    .AddHomeAssistantAddOnOptions()
    .AddEnvironmentVariables(prefix: "HA_PERSONAL_AGENT_")
    .AddAgentEnvironmentOverrides();

builder.Services.AddAgentConfiguration(builder.Configuration);
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
