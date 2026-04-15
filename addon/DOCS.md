# Home Assistant Personal Agent

## Configuration

Use the Home Assistant app/add-on UI to set:

- Telegram bot token
- Allowed Telegram user IDs, as a comma-separated string such as `93372553` or `93372553,12345`
- Moonshot/Kimi API key
- LLM base URL and model
- LLM thinking mode: `auto`, `disabled`, or `enabled`
- Home Assistant MCP endpoint
- Workspace and state paths
- Project capsule extraction mode: `manual` or `auto-batched`
- Auto-batched capsule threshold (new raw events count)

The default LLM backend is Moonshot/Kimi:

```text
https://api.moonshot.ai/v1
```

The app uses `/data` for persistent state and workspace files.

Telegram commands related to memory:

- `/showSummary`, `/refreshSummary`
- `/showRawEvents [N]`
- `/showCapsules [N]`, `/refreshCapsules`

`auto` is the recommended thinking mode. It disables provider thinking only when a tool-enabled run would otherwise require unsupported provider metadata round-trip, and leaves no-tools reasoning runs on provider defaults. `enabled` means "do not force-disable"; explicit provider enable is used only when a capability profile supports it.
