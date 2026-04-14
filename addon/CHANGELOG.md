# Changelog

## 0.1.3

- Add Telegram long polling dialogue MVP with `/start`, `/status`, and `/resetContext`.
- Persist per-chat Telegram conversation context in SQLite and pass recent turns into the agent runtime.
- Add memory strategy analysis: MVP uses last N turns plus rolling summary; vector memory is planned post-MVP.

## 0.1.2

- Add the first Microsoft Agent Framework runtime spike with Moonshot/OpenAI-compatible wiring and a safe status tool.
- Add SQLite state storage for Telegram update offsets.
- Remove the noisy worker heartbeat log.

## 0.1.1

- Fix Telegram allowlist option so Home Assistant accepts a single user ID or a comma-separated list from the add-on UI.

## 0.1.0

- Initial Home Assistant app/add-on packaging skeleton.
