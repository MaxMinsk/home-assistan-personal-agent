import { useCallback, useEffect, useRef, useState } from 'preact/hooks';
import {
  getContext,
  getHealth,
  getSummary,
  resetContext,
  streamTurn,
  type ContextSnapshot,
  type ExecutionProfile,
  type HealthResponse,
  type SummaryResponse,
} from './api';

// Что: корневой компонент Web UI — «пульт оператора» для персонального агента.
// Зачем: показать основного conversation-агента (сейчас) и будущих фоновых агентов (HPA-033) в одном месте.
// Как: слева ростер агентов, справа детальная панель с вкладками Чат/Контекст/Память над dialogue API (HPA-026).

type Tab = 'chat' | 'context' | 'memory';
type DotKind = 'ok' | 'run' | 'wait' | 'idle';

interface ChatMessage {
  id: string;
  role: 'user' | 'assistant';
  text: string;
  reasoning?: string;
  correlationId?: string;
  isConfigured?: boolean;
  streaming?: boolean;
}

const CONVERSATION_ID_KEY = 'hpa.conversationId';
const THEME_KEY = 'hpa.theme';

// crypto.randomUUID доступен только в secure context; под ingress это не гарантировано, поэтому есть fallback.
function uid(): string {
  return typeof crypto !== 'undefined' && 'randomUUID' in crypto
    ? crypto.randomUUID()
    : `${Date.now()}-${Math.floor(Math.random() * 1e9)}`;
}

function readConversationId(): string {
  const existing = localStorage.getItem(CONVERSATION_ID_KEY);
  if (existing) {
    return existing;
  }
  const created = `web-${uid()}`;
  localStorage.setItem(CONVERSATION_ID_KEY, created);
  return created;
}

function initialTheme(): 'light' | 'dark' {
  const stored = localStorage.getItem(THEME_KEY);
  if (stored === 'light' || stored === 'dark') {
    return stored;
  }
  return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
}

export function App() {
  const conversationId = useRef(readConversationId()).current;
  const [theme, setTheme] = useState<'light' | 'dark'>(initialTheme);
  const [tab, setTab] = useState<Tab>('chat');
  const [health, setHealth] = useState<HealthResponse | null>(null);
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    document.documentElement.dataset.theme = theme;
    localStorage.setItem(THEME_KEY, theme);
  }, [theme]);

  useEffect(() => {
    getHealth().then(setHealth).catch(() => setHealth(null));
  }, []);

  const conversationDot: DotKind = busy ? 'run' : health ? 'ok' : 'idle';

  return (
    <div class="app">
      <Sidebar
        health={health}
        conversationDot={conversationDot}
        theme={theme}
        onToggleTheme={() => setTheme((t) => (t === 'dark' ? 'light' : 'dark'))}
      />
      <section class="detail">
        <header class="detail__head">
          <div class="detail__title-row">
            <span class="detail__title">Conversation</span>
            <span class="chip">
              <StatusDot kind={conversationDot} />
              {busy ? 'думает' : 'готов'}
            </span>
          </div>
          <p class="detail__sub">Основной диалоговый агент — общий рантайм и память с Telegram.</p>
          <nav class="tabs">
            <TabButton id="chat" active={tab} label="Чат" onSelect={setTab} />
            <TabButton id="context" active={tab} label="Контекст" onSelect={setTab} />
            <TabButton id="memory" active={tab} label="Память" onSelect={setTab} />
          </nav>
        </header>
        <div class="detail__body">
          {tab === 'chat' && (
            <ChatTab
              conversationId={conversationId}
              messages={messages}
              setMessages={setMessages}
              busy={busy}
              setBusy={setBusy}
            />
          )}
          {tab === 'context' && <ContextTab conversationId={conversationId} />}
          {tab === 'memory' && <MemoryTab conversationId={conversationId} />}
        </div>
      </section>
    </div>
  );
}

function Sidebar(props: {
  health: HealthResponse | null;
  conversationDot: DotKind;
  theme: 'light' | 'dark';
  onToggleTheme: () => void;
}) {
  return (
    <aside class="sidebar">
      <div class="brand">
        <div class="brand__mark">◆</div>
        <div>
          <div class="brand__title">Personal Agent</div>
          <div class="brand__sub">operator console</div>
        </div>
      </div>
      <div class="rail">
        <div class="rail__label">Диалог</div>
        <button class="agent is-active" type="button">
          <StatusDot kind={props.conversationDot} />
          <span class="agent__body">
            <span class="agent__name">Conversation</span>
            <span class="agent__meta">интерактивный · сейчас</span>
          </span>
        </button>

        <div class="rail__label">Агенты</div>
        <div class="empty-agents">
          Фоновых агентов пока нет. Планировщик и автономные исследования появятся в следующих релизах.
        </div>
        <button class="btn-new" type="button" disabled title="Появится в HPA-028">
          + Новый агент
        </button>
      </div>
      <div class="sidebar__footer">
        <span class="version">{props.health ? `v${props.health.version}` : '—'}</span>
        <button class="theme-toggle" type="button" onClick={props.onToggleTheme}>
          {props.theme === 'dark' ? '☾ тёмная' : '☀ светлая'}
        </button>
      </div>
    </aside>
  );
}

function TabButton(props: { id: Tab; active: Tab; label: string; onSelect: (t: Tab) => void }) {
  return (
    <button
      type="button"
      class={props.active === props.id ? 'tab is-active' : 'tab'}
      onClick={() => props.onSelect(props.id)}
    >
      {props.label}
    </button>
  );
}

function StatusDot(props: { kind: DotKind }) {
  return <span class={`dot dot--${props.kind}`} aria-hidden="true" />;
}

function ChatTab(props: {
  conversationId: string;
  messages: ChatMessage[];
  setMessages: (updater: (prev: ChatMessage[]) => ChatMessage[]) => void;
  busy: boolean;
  setBusy: (value: boolean) => void;
}) {
  const { conversationId, messages, setMessages, busy, setBusy } = props;
  const [input, setInput] = useState('');
  const [profile, setProfile] = useState<ExecutionProfile>('tool');
  const scrollRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const node = scrollRef.current;
    if (node) {
      node.scrollTop = node.scrollHeight;
    }
  }, [messages]);

  const send = useCallback(async () => {
    const text = input.trim();
    if (!text || busy) {
      return;
    }
    setInput('');
    const userId = uid();
    const assistantId = uid();
    setMessages((prev) => [
      ...prev,
      { id: userId, role: 'user', text },
      { id: assistantId, role: 'assistant', text: '', reasoning: '', streaming: true },
    ]);
    setBusy(true);
    try {
      await streamTurn(conversationId, text, profile, {
        onReasoning: (delta) =>
          setMessages((prev) =>
            prev.map((m) => (m.id === assistantId ? { ...m, reasoning: (m.reasoning ?? '') + delta } : m)),
          ),
        onMessage: (msg) =>
          setMessages((prev) =>
            prev.map((m) =>
              m.id === assistantId
                ? {
                    ...m,
                    text: msg.text,
                    correlationId: msg.correlationId,
                    isConfigured: msg.isConfigured,
                    streaming: false,
                  }
                : m,
            ),
          ),
      });
    } catch (error) {
      setMessages((prev) =>
        prev.map((m) =>
          m.id === assistantId
            ? { ...m, text: `Не удалось получить ответ: ${(error as Error).message}`, streaming: false, isConfigured: false }
            : m,
        ),
      );
    } finally {
      setBusy(false);
    }
  }, [input, busy, conversationId, profile, setMessages, setBusy]);

  const reset = useCallback(async () => {
    try {
      await resetContext(conversationId);
    } catch {
      // Даже при ошибке очищаем локальную ленту — контекст на сервере всё равно перезапишется.
    }
    setMessages(() => []);
  }, [conversationId, setMessages]);

  return (
    <div class="chat">
      <div class="messages" ref={scrollRef}>
        {messages.length === 0 ? (
          <div class="chat-empty">
            <div class="chat-empty__mark">◆</div>
            <div>Напиши сообщение — агент ответит через тот же рантайм, что и в Telegram.</div>
          </div>
        ) : (
          messages.map((m) => <MessageView key={m.id} message={m} />)
        )}
      </div>
      <div class="composer">
        <div class="composer__row">
          <textarea
            value={input}
            placeholder="Сообщение агенту…"
            rows={1}
            onInput={(e) => setInput((e.target as HTMLTextAreaElement).value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter' && !e.shiftKey) {
                e.preventDefault();
                void send();
              }
            }}
          />
          <button class="send" type="button" disabled={busy || input.trim().length === 0} onClick={() => void send()}>
            {busy ? '…' : 'Отправить'}
          </button>
        </div>
        <div class="composer__meta">
          <div class="seg">
            <button
              type="button"
              class={profile === 'tool' ? 'is-active' : ''}
              onClick={() => setProfile('tool')}
            >
              tools
            </button>
            <button
              type="button"
              class={profile === 'deep' ? 'is-active' : ''}
              onClick={() => setProfile('deep')}
            >
              deep
            </button>
          </div>
          <button class="link-btn" type="button" onClick={() => void reset()}>
            Очистить контекст
          </button>
          <span class="composer__hint">Enter — отправить · Shift+Enter — перенос</span>
        </div>
      </div>
    </div>
  );
}

function MessageView(props: { message: ChatMessage }) {
  const { message } = props;
  const isUser = message.role === 'user';
  return (
    <div class={`msg msg--${message.role}`}>
      <span class="msg__role">{isUser ? 'ты' : 'агент'}</span>
      {message.reasoning ? (
        <div class="reasoning">
          <span class="reasoning__label">рассуждения</span>
          {message.reasoning}
        </div>
      ) : null}
      {message.streaming && !message.text ? (
        <div class="bubble">
          <span class="typing">
            <span />
            <span />
            <span />
          </span>
        </div>
      ) : (
        <div class={`bubble${message.isConfigured === false ? ' is-unconfigured' : ''}`}>{message.text}</div>
      )}
      {message.correlationId ? <span class="corr">id {message.correlationId}</span> : null}
    </div>
  );
}

function ContextTab(props: { conversationId: string }) {
  const [snapshot, setSnapshot] = useState<ContextSnapshot | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  const load = useCallback(() => {
    setLoading(true);
    setError(null);
    getContext(props.conversationId)
      .then((data) => setSnapshot(data))
      .catch((e: Error) => setError(e.message))
      .finally(() => setLoading(false));
  }, [props.conversationId]);

  useEffect(load, [load]);

  return (
    <div class="panel-pad">
      <div class="panel-head">
        <h2>Контекст диалога</h2>
        <button class="mini" type="button" onClick={load}>
          Обновить
        </button>
      </div>
      {loading && <div class="state">Загрузка…</div>}
      {error && <div class="state state--error">{error}</div>}
      {snapshot && !loading && (
        <div class="metrics">
          <Metric k="Сообщений в истории" v={snapshot.storedMessageCount} />
          <Metric k="Загружено / окно" v={`${snapshot.loadedHistoryMessageCount} / ${snapshot.maxContextMessages}`} />
          <Metric k="Сырых событий" v={snapshot.rawEventCount} />
          <Metric k="Токенов (оценка)" v={snapshot.estimatedContextTokenCount} sub="история + summary" />
          <Metric k="Токенов истории" v={snapshot.estimatedHistoryTokenCount} />
          <Metric k="Режим памяти" v={snapshot.memoryRetrievalMode} />
          <Metric
            k="Persisted summary"
            v={snapshot.persistedSummaryPresent ? `v${snapshot.persistedSummaryVersion}` : 'нет'}
            sub={snapshot.persistedSummaryPresent ? `${snapshot.persistedSummaryLength} симв.` : undefined}
          />
          <Metric k="С последнего summary" v={snapshot.messagesSincePersistedSummary} sub="сообщений" />
        </div>
      )}
    </div>
  );
}

function MemoryTab(props: { conversationId: string }) {
  const [summary, setSummary] = useState<SummaryResponse | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  const load = useCallback(() => {
    setLoading(true);
    setError(null);
    getSummary(props.conversationId)
      .then((data) => setSummary(data))
      .catch((e: Error) => setError(e.message))
      .finally(() => setLoading(false));
  }, [props.conversationId]);

  useEffect(load, [load]);

  return (
    <div class="panel-pad">
      <div class="panel-head">
        <h2>Свёрнутая память чата</h2>
        <button class="mini" type="button" onClick={load}>
          Обновить
        </button>
      </div>
      {loading && <div class="state">Загрузка…</div>}
      {error && <div class="state state--error">{error}</div>}
      {summary && !loading && !summary.present && (
        <div class="state">
          Persisted summary для этого чата пока нет. Он собирается автоматически по мере накопления истории.
        </div>
      )}
      {summary && !loading && summary.present && (
        <>
          <div class="panel-head">
            <span class="chip">версия v{summary.version}</span>
            <span class="version">обновлён {summary.updatedUtc ?? '—'}</span>
          </div>
          <div class="summary-box">{summary.summary}</div>
        </>
      )}
    </div>
  );
}

function Metric(props: { k: string; v: string | number; sub?: string }) {
  return (
    <div class="metric">
      <div class="metric__k">{props.k}</div>
      <div class="metric__v">
        {props.v}
        {props.sub ? <small> {props.sub}</small> : null}
      </div>
    </div>
  );
}
