# Home Assistant Personal Agent

Learning-first C#/.NET project for Microsoft Agent Framework, packaged as a Home Assistant app/add-on.

The practical target is a personal Home Assistant assistant that can later talk through Telegram, use Home Assistant MCP tools and analyze camera alerts.

## Development

```bash
dotnet restore HomeAssistantPersonalAgent.sln
dotnet build HomeAssistantPersonalAgent.sln
dotnet test HomeAssistantPersonalAgent.sln
```

## Home Assistant Repository

Add this repository to Home Assistant:

```text
https://github.com/MaxMinsk/home-assistan-personal-agent
```

The app image is published to:

```text
ghcr.io/maxminsk/home-assistan-personal-agent
```
