# Home Assistant Personal Agent

Learning-first Microsoft Agent Framework assistant for Home Assistant.

Configure Telegram and LLM settings through the Home Assistant app/add-on UI. Secrets are read from `/data/options.json` at runtime and are not printed by `/status` or startup logs.

`llm_thinking_mode` defaults to `auto`: tool-enabled runs stay stable, while no-tools deep reasoning can use provider defaults when supported.
