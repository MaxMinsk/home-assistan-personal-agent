// Что: клиент HTTP/SSE к dialogue API встроенного веб-хоста (HPA-026).
// Зачем: SPA ведёт диалог с основным агентом через тот же transport-agnostic DialogueService, что и Telegram.
// Как: все пути относительные (без ведущего слэша), поэтому запросы корректно резолвятся под ingress-префиксом HA.

export interface HealthResponse {
  status: string;
  application: string;
  version: string;
  targetFramework: string;
  webUiEnabled: boolean;
}

export interface TurnResponse {
  text: string;
  correlationId: string;
  isConfigured: boolean;
}

export interface ContextSnapshot {
  conversationKey: string;
  storedMessageCount: number;
  rawEventCount: number;
  memoryRetrievalMode: string;
  maxContextMessages: number;
  loadedHistoryMessageCount: number;
  messagesSincePersistedSummary: number;
  persistedSummaryPresent: boolean;
  persistedSummaryVersion: number;
  persistedSummaryLength: number;
  estimatedContextTokenCount: number;
  estimatedHistoryTokenCount: number;
  estimatedPersistedSummaryTokenCount: number;
}

export interface SummaryResponse {
  present: boolean;
  summary: string | null;
  version: number;
  updatedUtc: string | null;
  sourceLastMessageId: number;
}

export type ExecutionProfile = 'tool' | 'deep';

async function readJson<T>(response: Response): Promise<T> {
  if (!response.ok) {
    const body = await response.text();
    let message = body;
    try {
      const parsed = JSON.parse(body);
      message = parsed.error ?? body;
    } catch {
      // Тело не JSON — оставляем как есть.
    }
    throw new Error(message || `HTTP ${response.status}`);
  }
  return (await response.json()) as T;
}

export function getHealth(): Promise<HealthResponse> {
  return fetch('api/health', { headers: { Accept: 'application/json' } }).then(readJson<HealthResponse>);
}

export interface HistoryMessage {
  role: string;
  text: string;
  createdUtc: string;
}

export function getHistory(conversationId: string): Promise<HistoryMessage[]> {
  const query = new URLSearchParams({ conversationId });
  return fetch(`api/dialogue/history?${query}`, { headers: { Accept: 'application/json' } })
    .then(readJson<HistoryMessage[]>);
}

export function getContext(conversationId: string): Promise<ContextSnapshot> {
  const query = new URLSearchParams({ conversationId });
  return fetch(`api/dialogue/context?${query}`, { headers: { Accept: 'application/json' } })
    .then(readJson<ContextSnapshot>);
}

export function getSummary(conversationId: string): Promise<SummaryResponse> {
  const query = new URLSearchParams({ conversationId });
  return fetch(`api/dialogue/summary?${query}`, { headers: { Accept: 'application/json' } })
    .then(readJson<SummaryResponse>);
}

export function resetContext(conversationId: string): Promise<{ ok: boolean }> {
  return fetch('api/dialogue/reset', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ conversationId }),
  }).then(readJson<{ ok: boolean }>);
}

export function sendTurn(
  conversationId: string,
  text: string,
  profile: ExecutionProfile,
): Promise<TurnResponse> {
  return fetch('api/dialogue/turn', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ conversationId, text, profile }),
  }).then(readJson<TurnResponse>);
}

// ---------- Автономные агенты (HPA-033) ----------

export interface AgentToolScope {
  allowHomeAssistantRead: boolean;
  allowWebSearch: boolean;
  allowMemoryRead: boolean;
  allowMemoryWrite: boolean;
  maxDurableFactsPerRun: number;
  allowProposeActions: boolean;
  allowCrossAgentContext: boolean;
}

export interface AgentSummary {
  id: string;
  name: string;
  status: string;
  scheduleKind: string;
  nextRunUtc: string | null;
  lastRunUtc: string | null;
  hasRunningRun: boolean;
  pendingReplyCount: number;
  openQuestionCount: number;
}

export interface AgentDetail {
  id: string;
  name: string;
  mission: string;
  status: string;
  scheduleKind: string;
  scheduleExpression: string | null;
  deliveryTelegramChatId: number | null;
  toolScope: AgentToolScope;
  nextRunUtc: string | null;
  lastRunUtc: string | null;
  createdUtc: string;
  updatedUtc: string;
  hasRunningRun: boolean;
  pendingReplyCount: number;
  focus: string | null;
  openQuestions: string | null;
  capsuleNoteKey: string | null;
  capsuleUpdatedUtc: string | null;
}

export interface AgentRun {
  id: string;
  status: string;
  startedUtc: string;
  finishedUtc: string | null;
  summary: string | null;
  questions: string[];
  error: string | null;
  toolCallCount: number;
}

export interface AgentInboxEntry {
  id: string;
  source: string;
  text: string;
  receivedUtc: string;
}

export interface AgentUpsert {
  name: string;
  mission: string;
  scheduleKind: string;
  scheduleExpression?: string | null;
  deliveryTelegramChatId?: number | null;
  toolScope?: AgentToolScope;
}

function jsonRequest<T>(path: string, method: string, body?: unknown): Promise<T> {
  return fetch(path, {
    method,
    headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  }).then(readJson<T>);
}

export interface Capabilities {
  webSearchConfigured: boolean;
  memoryConfigured: boolean;
}

export function getCapabilities(): Promise<Capabilities> {
  return fetch('api/capabilities', { headers: { Accept: 'application/json' } }).then(readJson<Capabilities>);
}

export function listAgents(): Promise<AgentSummary[]> {
  return fetch('api/agents', { headers: { Accept: 'application/json' } }).then(readJson<AgentSummary[]>);
}

export function getAgent(agentId: string): Promise<AgentDetail> {
  return fetch(`api/agents/${encodeURIComponent(agentId)}`, { headers: { Accept: 'application/json' } })
    .then(readJson<AgentDetail>);
}

export function createAgent(request: AgentUpsert): Promise<AgentDetail> {
  return jsonRequest<AgentDetail>('api/agents', 'POST', request);
}

export function updateAgent(agentId: string, request: AgentUpsert): Promise<AgentDetail> {
  return jsonRequest<AgentDetail>(`api/agents/${encodeURIComponent(agentId)}`, 'PUT', request);
}

export function deleteAgent(agentId: string): Promise<{ ok: boolean }> {
  return jsonRequest<{ ok: boolean }>(`api/agents/${encodeURIComponent(agentId)}`, 'DELETE');
}

export function setAgentStatus(agentId: string, status: 'Active' | 'Paused'): Promise<{ ok: boolean }> {
  return jsonRequest<{ ok: boolean }>(`api/agents/${encodeURIComponent(agentId)}/status`, 'POST', { status });
}

export function runAgentNow(agentId: string): Promise<{ ok: boolean }> {
  return jsonRequest<{ ok: boolean }>(`api/agents/${encodeURIComponent(agentId)}/run`, 'POST');
}

export function listAgentRuns(agentId: string): Promise<AgentRun[]> {
  return fetch(`api/agents/${encodeURIComponent(agentId)}/runs`, { headers: { Accept: 'application/json' } })
    .then(readJson<AgentRun[]>);
}

// Глобальный стоп-кран: ставит на паузу всех активных агентов.
export function pauseAllAgents(): Promise<{ ok: boolean; paused: number }> {
  return jsonRequest<{ ok: boolean; paused: number }>('api/agents/pause-all', 'POST');
}

export function replyToAgent(agentId: string, text: string): Promise<{ ok: boolean }> {
  return jsonRequest<{ ok: boolean }>(`api/agents/${encodeURIComponent(agentId)}/reply`, 'POST', { text });
}

export function getAgentInbox(agentId: string): Promise<AgentInboxEntry[]> {
  return fetch(`api/agents/${encodeURIComponent(agentId)}/inbox`, { headers: { Accept: 'application/json' } })
    .then(readJson<AgentInboxEntry[]>);
}

export function deleteInboxEntry(agentId: string, entryId: string): Promise<{ ok: boolean }> {
  return jsonRequest<{ ok: boolean }>(
    `api/agents/${encodeURIComponent(agentId)}/inbox/${encodeURIComponent(entryId)}`,
    'DELETE',
  );
}

export interface AgentAction {
  id: string;
  actionKind: string;
  summary: string;
  risk: string;
  createdUtc: string;
  expiresUtc: string;
}

export interface AgentActionDecision {
  ok: boolean;
  outcome: string;
  message: string;
}

export function getAgentActions(agentId: string): Promise<AgentAction[]> {
  return fetch(`api/agents/${encodeURIComponent(agentId)}/actions`, { headers: { Accept: 'application/json' } })
    .then(readJson<AgentAction[]>);
}

export function approveAgentAction(agentId: string, actionId: string): Promise<AgentActionDecision> {
  return jsonRequest<AgentActionDecision>(
    `api/agents/${encodeURIComponent(agentId)}/actions/${encodeURIComponent(actionId)}/approve`,
    'POST',
  );
}

export function rejectAgentAction(agentId: string, actionId: string): Promise<AgentActionDecision> {
  return jsonRequest<AgentActionDecision>(
    `api/agents/${encodeURIComponent(agentId)}/actions/${encodeURIComponent(actionId)}/reject`,
    'POST',
  );
}

export interface StreamHandlers {
  onReasoning: (delta: string) => void;
  onMessage: (message: TurnResponse) => void;
}

// Стримит ход диалога через SSE (event: reasoning дельты, затем event: message с финальным ответом).
export async function streamTurn(
  conversationId: string,
  text: string,
  profile: ExecutionProfile,
  handlers: StreamHandlers,
): Promise<void> {
  const response = await fetch('api/dialogue/stream', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'text/event-stream' },
    body: JSON.stringify({ conversationId, text, profile }),
  });

  const contentType = response.headers.get('content-type') ?? '';
  if (!response.ok || !response.body || !contentType.includes('text/event-stream')) {
    // Ошибка валидации/сервера приходит обычным JSON — разбираем как ход без стрима.
    const message = await readJson<TurnResponse>(response).catch((error: Error) => {
      throw error;
    });
    handlers.onMessage(message);
    return;
  }

  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let buffer = '';

  for (;;) {
    const { done, value } = await reader.read();
    if (done) {
      break;
    }
    buffer += decoder.decode(value, { stream: true });

    let separator: number;
    while ((separator = buffer.indexOf('\n\n')) >= 0) {
      const rawEvent = buffer.slice(0, separator);
      buffer = buffer.slice(separator + 2);
      dispatchSseEvent(rawEvent, handlers);
    }
  }
}

function dispatchSseEvent(rawEvent: string, handlers: StreamHandlers): void {
  let eventName = 'message';
  const dataLines: string[] = [];

  for (const line of rawEvent.split('\n')) {
    if (line.startsWith('event:')) {
      eventName = line.slice('event:'.length).trim();
    } else if (line.startsWith('data:')) {
      dataLines.push(line.slice('data:'.length).replace(/^ /, ''));
    }
  }

  const data = dataLines.join('\n');
  if (eventName === 'reasoning') {
    handlers.onReasoning(data);
  } else if (eventName === 'message' && data) {
    handlers.onMessage(JSON.parse(data) as TurnResponse);
  }
}
