# Agent Runtime Architecture

## Контекст

`HAAG-052` декомпозирует `AgentRuntime` из монолита в orchestration facade + специализированные компоненты.

Цель: упростить сопровождение, локализовать риски изменений и делать точечные тесты на policy/flow уровни.

## Новая схема

1. `AgentRuntime`
   - orchestration only: health -> resolve -> run -> fallback -> response.
2. `AgentRuntimePreflight`
   - проверка конфигурации (`ApiKey/BaseUrl/Model/ThinkingMode/RouterMode`).
3. `AgentExecutionResolver` + `AgentExecutionDecision`
   - объединяет `LlmExecutionRouter` и `LlmExecutionPlanner`.
4. `HomeAssistantMcpToolSetResolver`
   - загружает MCP read-only tool set и graceful fallback при недоступности.
5. `AgentRunner`
   - выполняет один run attempt (sync/streaming) через MAF.
6. `AgentMafFactory`
   - wiring chat client/middleware/compaction/tools/instructions и создание `ChatClientAgent`.
7. `AgentCompactionPipelineFactory`
   - сборка MAF compaction pipeline.
8. `AgentToolCatalog`
   - формирование tools и runtime instructions.
9. `AgentFallbackExecutor`
   - fallback policy `small -> default`.
10. `AgentRuntimeDiagnosticsLogger`
    - start/finish/reasoning/compaction logs.
11. `AgentRuntimeResultFactory`
    - response mapping (success/failure) и execution bucket resolution.

## Где расширять дальше

- `AgentExecutionResolver`:
  - classifier-based routing, confidence score, provider/model budgets.
- `AgentFallbackExecutor`:
  - multi-tier fallback (`small -> medium -> default`), provider-aware retry table.
- `AgentToolCatalog`:
  - pluggable tool modules по доменам (HA/files/workflows/artifacts).
- `AgentCompactionPipelineFactory`:
  - adaptive thresholds и provider-specific summarize profile.
- `AgentRuntimeDiagnosticsLogger`:
  - structured OTEL events/span attributes.

## MAF reference alignment

- Middleware and per-request policy:
  - `dotnet/samples/02-agents/Agents/Agent_Step11_Middleware`
  - `dotnet/samples/03-foundry-local/AgentsWithFoundry/Agent_Step12_Middleware`
- Compaction pipeline:
  - `dotnet/samples/02-agents/Agents/Agent_Step18_CompactionPipeline/Program.cs`
- Streaming run pattern:
  - `dotnet/samples/02-agents/Agents/Agent_Step02_StructuredOutput/Program.cs`
