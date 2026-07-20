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
