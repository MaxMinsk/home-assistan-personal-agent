# Agent Reasoning And Provider Capabilities

## Цель

HAAG-032 не должен быть Moonshot/Kimi-only fix. Цель - отделить три вещи:

- какой provider подключен;
- какие capabilities у provider известны приложению;
- какой execution profile нужен конкретному agent run.

Так runtime может стабильно работать с Moonshot/Kimi, OpenAI и другими OpenAI-compatible backend без размазывания provider-specific условий по Telegram, DialogueService или tools.

## Execution Profiles

`ToolEnabled` - обычный режим ассистента. Tools доступны: status, Home Assistant MCP read-only tools и proposal tool для confirmation-required действий. Для provider, где thinking + tools требует metadata round-trip, runtime может отключить thinking в `auto`.

`PureChat` - обычный LLM-чат без tools. Сейчас это профиль для planner/tests и будущих adapter сценариев.

`DeepReasoning` - no-tools режим для сложных вопросов. В Telegram он доступен как `/think <вопрос>`. В этом режиме агент не должен утверждать, что проверил Home Assistant, файлы или внешние системы.

## Provider Capabilities

`LlmProviderCapabilities` описывает не бренд модели, а поведение, важное runtime:

- `SupportsTools`;
- `SupportsStreaming`;
- `SupportsReasoning`;
- `RequiresReasoningContentRoundTripForToolCalls`;
- `SupportsReasoningContentRoundTrip`;
- `ThinkingControlStyle`.

Moonshot profile сейчас говорит: reasoning поддерживается, tools поддерживаются, и для tool-call history нужен `reasoning_content` round-trip. В runtime добавлен per-run `ReasoningContentReplayChatClient`, который делает capture+replay этого поля в рамках одного run, поэтому `auto + ToolEnabled` может оставлять provider default thinking mode.

Generic OpenAI-compatible profile консервативен: tools считаем доступными, но request-level thinking control не применяем, пока явно не добавим capability profile для конкретного provider.

## reasoning_content

`reasoning_content` - provider-specific часть assistant response. Ее заполняет provider/model, а приложение не должно синтезировать ее само.

При thinking-enabled tool calling provider может ожидать такой цикл:

1. Request с user/system messages и tools.
2. Assistant response с `reasoning_content` и `tool_calls`.
3. Runtime выполняет tools.
4. Следующий request включает исходный assistant tool-call message с тем же `reasoning_content`, затем tool results.
5. Provider продолжает reasoning и возвращает новые tool calls или финальный `content`.

Если runtime потерял `reasoning_content`, provider может отклонить следующий request как неполную tool-call history. Для Moonshot/Kimi это проявлялось как HTTP 400: `thinking is enabled but reasoning_content is missing in assistant tool call message`.

## Storage Rule

Reasoning trace не является пользовательским ответом и не является dialogue memory.

Даже если позже мы реализуем полноценный round-trip, `reasoning_content` должен жить только в ephemeral per-run state. Его нельзя сохранять в:

- `conversation_messages`;
- Telegram/Web UI output;
- long-term vector memory;
- обычные production logs.

Допустимы только sanitized diagnostics: был ли reasoning включен, effective mode, размер provider extension и причина fallback.

## Current Decision

`llm_thinking_mode = auto`:

- `ToolEnabled`: не отключает thinking, если provider profile заявляет supported reasoning-content round-trip (для Moonshot через middleware replay).
- `PureChat` и `DeepReasoning`: не добавляет forced disabled thinking, provider может использовать свой default.

`llm_thinking_mode = disabled`:

- если provider profile знает request-level schema, runtime добавляет explicit disable;
- если schema неизвестна, runtime не patch-ит request body и логирует reason в execution plan.

`llm_thinking_mode = enabled`:

- если provider profile явно знает request-level schema для explicit enable, runtime добавляет explicit enable;
- для Moonshot/Kimi сейчас используется provider default, потому что официально нам нужен documented disable path, а `kimi-k2.5` already enables thinking by default outside tool-safe auto mode;
- для tool-enabled providers без round-trip это может быть рискованно, поэтому `auto` остается безопасным default и может fallback-нуться в disable.

## Next Improvements

- Добавить capability profiles для других providers после проверки их API contract.
- Исследовать, можно ли в текущем MAF/OpenAI SDK stack round-trip-ить raw provider extensions.
- Развести обычный tool-enabled dialogue и pure chat режимы на уровне будущего Web UI.
- Добавить streaming events, где reasoning diagnostics остаются internal, а user видит только progress/final answer.
