# Home Assistant Personal Agent

## Configuration

Use the Home Assistant app/add-on UI to set:

- Telegram bot token
- Allowed Telegram user IDs
- Moonshot/Kimi API key
- LLM base URL and model
- Home Assistant MCP endpoint
- Workspace and state paths

The default LLM backend is Moonshot/Kimi:

```text
https://api.moonshot.ai/v1
```

The app uses `/data` for persistent state and workspace files.
