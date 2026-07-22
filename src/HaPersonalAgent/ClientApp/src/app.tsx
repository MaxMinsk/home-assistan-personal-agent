import { useCallback, useEffect, useRef, useState } from 'preact/hooks';
import {
  getContext,
  getHealth,
  getHistory,
  getSummary,
  listAgents,
  pauseAllAgents,
  resetContext,
  runAgentNow,
  streamTurn,
  type AgentSummary,
  type ContextSnapshot,
  type ExecutionProfile,
  type HealthResponse,
  type SummaryResponse,
} from './api';
import { AgentCreateView, AgentDetailView, formatDateTime, scheduleLabel } from './agents';

// Что: корневой компонент Web UI — «пульт оператора» для персонального агента.
// Зачем: показать основного conversation-агента (сейчас) и будущих фоновых агентов (HPA-033) в одном месте.
// Как: слева ростер агентов, справа детальная панель с вкладками Чат/Контекст/Память над dialogue API (HPA-026).

type Tab = 'chat' | 'context' | 'memory';
type DotKind = 'ok' | 'run' | 'wait' | 'idle';

/// Что выбрано в левом ростере: интерактивный диалог, конкретный фоновый агент или форма создания.
type Selection =
  | { kind: 'conversation' }
  | { kind: 'agent'; id: string }
  | { kind: 'new' };

interface ChatMessage {
  id: string;
  role: 'user' | 'assistant';
  text: string;
  reasoning?: string;
  correlationId?: string;
  isConfigured?: boolean;
  streaming?: boolean;
  /// Локальный вывод команды веб-чата (не ответ модели) — рендерится как приглушённая инфо-плашка.
  system?: boolean;
}

interface WebCommand {
  name: string;
  description: string;
}

// Команды веб-чата (HPA-048): обрабатываются на клиенте, в модель не уходят. Диагностические
// telegram-команды сознательно не тащим — им место в UI-аффордансах (кнопки/вкладки).
const WEB_COMMANDS: WebCommand[] = [
  { name: '/help', description: 'список команд' },
  { name: '/status', description: 'версия и снимок контекста' },
  { name: '/summary', description: 'свёрнутая память чата' },
  { name: '/reset', description: 'очистить контекст' },
  { name: '/fast', description: 'режим ответа: быстрый' },
  { name: '/deep', description: 'режим ответа: глубокий' },
];

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
  const [selection, setSelection] = useState<Selection>({ kind: 'conversation' });
  const [agents, setAgents] = useState<AgentSummary[]>([]);

  useEffect(() => {
    document.documentElement.dataset.theme = theme;
    localStorage.setItem(THEME_KEY, theme);
  }, [theme]);

  useEffect(() => {
    getHealth().then(setHealth).catch(() => setHealth(null));
  }, []);

  // Восстанавливаем ленту чата из персистентной истории: после перезагрузки/апдейта разговор не должен выглядеть «забытым».
  useEffect(() => {
    getHistory(conversationId)
      .then((history) => {
        if (history.length > 0) {
          setMessages(() =>
            history.map((message) => ({
              id: uid(),
              role: message.role === 'user' ? 'user' : 'assistant',
              text: message.text,
            })),
          );
        }
      })
      .catch(() => undefined);
  }, [conversationId]);

  const refreshAgents = useCallback(() => {
    listAgents()
      .then(setAgents)
      .catch(() => setAgents([]));
  }, []);

  useEffect(() => {
    refreshAgents();
    // Лёгкий поллинг: статус «идёт запуск» и время следующего запуска меняются сами по себе.
    const timer = window.setInterval(refreshAgents, 15_000);
    return () => window.clearInterval(timer);
  }, [refreshAgents]);

  const conversationDot: DotKind = busy ? 'run' : health ? 'ok' : 'idle';
  const selectedAgent = selection.kind === 'agent'
    ? agents.find((agent) => agent.id === selection.id)
    : undefined;

  return (
    <div class="app">
      <Sidebar
        health={health}
        conversationDot={conversationDot}
        theme={theme}
        agents={agents}
        selection={selection}
        onSelect={setSelection}
        onAgentsChanged={refreshAgents}
        onToggleTheme={() => setTheme((t) => (t === 'dark' ? 'light' : 'dark'))}
      />
      <section class="detail">
        {selection.kind === 'conversation' && (
          <>
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
          </>
        )}

        {selection.kind === 'agent' && (
          <>
            <header class="detail__head">
              <div class="detail__title-row">
                <span class="detail__title">{selectedAgent?.name ?? 'Агент'}</span>
                <span class="chip">
                  <StatusDot kind={agentDot(selectedAgent)} />
                  {agentStateLabel(selectedAgent)}
                </span>
              </div>
              <p class="detail__sub">
                Фоновый исследователь · {selectedAgent ? scheduleLabel(selectedAgent.scheduleKind) : '—'} ·
                следующий запуск {formatDateTime(selectedAgent?.nextRunUtc)}
              </p>
            </header>
            <div class="detail__body">
              <AgentDetailView
                agentId={selection.id}
                onChanged={refreshAgents}
                onDeleted={() => {
                  setSelection({ kind: 'conversation' });
                  refreshAgents();
                }}
              />
            </div>
          </>
        )}

        {selection.kind === 'new' && (
          <>
            <header class="detail__head">
              <div class="detail__title-row">
                <span class="detail__title">Новый агент</span>
              </div>
              <p class="detail__sub">
                Опиши миссию и каденцию — агент будет просыпаться сам и присылать сводку.
              </p>
            </header>
            <div class="detail__body">
              <AgentCreateView
                onCreated={(agentId) => {
                  refreshAgents();
                  setSelection({ kind: 'agent', id: agentId });
                }}
                onCancel={() => setSelection({ kind: 'conversation' })}
              />
            </div>
          </>
        )}
      </section>
    </div>
  );
}

function AgentRosterRow(props: {
  agent: AgentSummary;
  active: boolean;
  onSelect: () => void;
  onRan: () => void;
}) {
  const { agent, active, onSelect, onRan } = props;
  const [running, setRunning] = useState(false);
  const [done, setDone] = useState(false);
  // Пока агент уже выполняется или на паузе — принудительный запуск не имеет смысла.
  const canRun = agent.status !== 'Paused' && !agent.hasRunningRun && !running;

  const run = async (event: Event) => {
    // Клик по кнопке не должен ещё и выбирать агента в списке.
    event.stopPropagation();
    if (!canRun) {
      return;
    }
    setRunning(true);
    setDone(false);
    try {
      await runAgentNow(agent.id);
      setDone(true);
      onRan();
      setTimeout(() => setDone(false), 2500);
    } finally {
      setRunning(false);
    }
  };

  return (
    <div class={active ? 'agent-row is-active' : 'agent-row'}>
      <button class={active ? 'agent is-active' : 'agent'} type="button" onClick={onSelect}>
        <StatusDot kind={agentDot(agent)} />
        <span class="agent__body">
          <span class="agent__name">{agent.name}</span>
          <span class="agent__meta">
            {agent.status === 'Paused' ? 'пауза' : formatDateTime(agent.nextRunUtc)}
            {agent.openQuestionCount > 0 ? ` · ${agent.openQuestionCount} ?` : ''}
          </span>
        </span>
      </button>
      <button
        class="agent-run"
        type="button"
        title={
          agent.status === 'Paused'
            ? 'Агент на паузе'
            : agent.hasRunningRun
              ? 'Уже выполняется'
              : 'Выполнить сейчас'
        }
        disabled={!canRun}
        onClick={run}
      >
        {done ? '✓' : running || agent.hasRunningRun ? '⏳' : '▶'}
      </button>
    </div>
  );
}

function agentDot(agent: AgentSummary | undefined): DotKind {
  if (!agent) {
    return 'idle';
  }
  if (agent.hasRunningRun) {
    return 'run';
  }
  if (agent.status === 'Paused') {
    return 'idle';
  }
  // Есть открытые вопросы, но пользователь уже что-то поставил в очередь → ждём не его, а следующего запуска.
  if (agent.openQuestionCount > 0 && agent.pendingReplyCount > 0) {
    return 'ok';
  }
  return agent.openQuestionCount > 0 ? 'wait' : 'ok';
}

function agentStateLabel(agent: AgentSummary | undefined): string {
  if (!agent) {
    return '—';
  }
  if (agent.hasRunningRun) {
    return 'работает';
  }
  if (agent.status === 'Paused') {
    return 'на паузе';
  }
  // Ответ/контекст уже в очереди — не «ждёт ответа», а учтёт при следующем запуске.
  if (agent.openQuestionCount > 0 && agent.pendingReplyCount > 0) {
    return 'ответ в очереди';
  }
  return agent.openQuestionCount > 0 ? 'ждёт ответа' : 'активен';
}

function Sidebar(props: {
  health: HealthResponse | null;
  conversationDot: DotKind;
  theme: 'light' | 'dark';
  agents: AgentSummary[];
  selection: Selection;
  onSelect: (selection: Selection) => void;
  onAgentsChanged: () => void;
  onToggleTheme: () => void;
}) {
  const { selection } = props;

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
        <button
          class={selection.kind === 'conversation' ? 'agent is-active' : 'agent'}
          type="button"
          onClick={() => props.onSelect({ kind: 'conversation' })}
        >
          <StatusDot kind={props.conversationDot} />
          <span class="agent__body">
            <span class="agent__name">Conversation</span>
            <span class="agent__meta">интерактивный · сейчас</span>
          </span>
        </button>

        <div class="rail__label rail__label--row">
          <span>Агенты</span>
          {props.agents.some((agent) => agent.status !== 'Paused') && (
            <button
              class="rail__action"
              type="button"
              title="Поставить на паузу всех агентов. Идущий сейчас запуск доработает, но следующего не будет."
              onClick={() => {
                if (confirm('Поставить на паузу всех агентов?')) {
                  void pauseAllAgents().then(props.onAgentsChanged).catch(() => undefined);
                }
              }}
            >
              стоп-кран
            </button>
          )}
        </div>
        {props.agents.length === 0 ? (
          <div class="empty-agents">
            Фоновых агентов пока нет. Создай первого — он будет просыпаться по расписанию и присылать сводку.
          </div>
        ) : (
          props.agents.map((agent) => (
            <AgentRosterRow
              key={agent.id}
              agent={agent}
              active={selection.kind === 'agent' && selection.id === agent.id}
              onSelect={() => props.onSelect({ kind: 'agent', id: agent.id })}
              onRan={props.onAgentsChanged}
            />
          ))
        )}
        <button
          class={selection.kind === 'new' ? 'btn-new is-active' : 'btn-new'}
          type="button"
          onClick={() => props.onSelect({ kind: 'new' })}
        >
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
  const [commandIndex, setCommandIndex] = useState(0);
  const [paletteDismissed, setPaletteDismissed] = useState(false);
  const scrollRef = useRef<HTMLDivElement>(null);

  const commandToken = input.startsWith('/') ? input.split(/\s/)[0].toLowerCase() : null;
  const matchingCommands = commandToken !== null
    ? WEB_COMMANDS.filter((command) => command.name.startsWith(commandToken))
    : [];
  const paletteOpen = matchingCommands.length > 0 && !paletteDismissed && !busy;
  const activeCommand = matchingCommands[Math.min(commandIndex, matchingCommands.length - 1)];

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

  const pushSystem = useCallback(
    (text: string) => setMessages((prev) => [...prev, { id: uid(), role: 'assistant', text, system: true }]),
    [setMessages],
  );

  const runCommand = useCallback(
    async (name: string) => {
      setInput('');
      setPaletteDismissed(false);
      setCommandIndex(0);

      switch (name) {
        case '/help':
          pushSystem('Команды веб-чата:\n' + WEB_COMMANDS.map((c) => `${c.name} — ${c.description}`).join('\n'));
          break;
        case '/reset':
          await reset();
          pushSystem('Контекст очищен.');
          break;
        case '/fast':
          setProfile('tool');
          pushSystem('Режим ответа: быстрый (инструменты доступны).');
          break;
        case '/deep':
          setProfile('deep');
          pushSystem('Режим ответа: глубокий (инструменты доступны).');
          break;
        case '/status':
          try {
            const [health, context] = await Promise.all([getHealth(), getContext(conversationId)]);
            pushSystem(
              [
                `${health.application} v${health.version}`,
                `Сообщений в истории: ${context.storedMessageCount}`,
                `Загружено / окно: ${context.loadedHistoryMessageCount} / ${context.maxContextMessages}`,
                `Токенов (оценка): ${context.estimatedContextTokenCount}`,
                `Режим памяти: ${context.memoryRetrievalMode}`,
                `Persisted summary: ${context.persistedSummaryPresent ? 'v' + context.persistedSummaryVersion : 'нет'}`,
              ].join('\n'),
            );
          } catch (e) {
            pushSystem('Не удалось получить статус: ' + (e as Error).message);
          }
          break;
        case '/summary':
          try {
            const summary = await getSummary(conversationId);
            pushSystem(
              summary.present && summary.summary
                ? `Свёрнутая память (v${summary.version}):\n\n${summary.summary}`
                : 'Свёрнутой памяти для этого чата пока нет.',
            );
          } catch (e) {
            pushSystem('Не удалось получить summary: ' + (e as Error).message);
          }
          break;
        default:
          break;
      }
    },
    [conversationId, pushSystem, reset],
  );

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
        {paletteOpen && (
          <div class="cmd-palette" role="listbox">
            {matchingCommands.map((command, index) => (
              <button
                key={command.name}
                type="button"
                class={command === activeCommand ? 'cmd is-active' : 'cmd'}
                onMouseEnter={() => setCommandIndex(index)}
                onClick={() => void runCommand(command.name)}
              >
                <span class="cmd__name">{command.name}</span>
                <span class="cmd__desc">{command.description}</span>
              </button>
            ))}
          </div>
        )}
        <div class="composer__row">
          <textarea
            value={input}
            placeholder="Сообщение агенту… (/ — команды)"
            rows={1}
            onInput={(e) => {
              setInput((e.target as HTMLTextAreaElement).value);
              setPaletteDismissed(false);
              setCommandIndex(0);
            }}
            onKeyDown={(e) => {
              if (paletteOpen) {
                if (e.key === 'ArrowDown') {
                  e.preventDefault();
                  setCommandIndex((i) => Math.min(i + 1, matchingCommands.length - 1));
                  return;
                }
                if (e.key === 'ArrowUp') {
                  e.preventDefault();
                  setCommandIndex((i) => Math.max(i - 1, 0));
                  return;
                }
                if (e.key === 'Escape') {
                  e.preventDefault();
                  setPaletteDismissed(true);
                  return;
                }
                if ((e.key === 'Enter' && !e.shiftKey) || e.key === 'Tab') {
                  e.preventDefault();
                  if (activeCommand) {
                    void runCommand(activeCommand.name);
                  }
                  return;
                }
              }
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
          <div class="seg" role="group" aria-label="Режим ответа">
            <button
              type="button"
              class={profile === 'tool' ? 'is-active' : ''}
              title="Обычный ответ. Инструменты доступны: память, Home Assistant."
              onClick={() => setProfile('tool')}
            >
              быстрый
            </button>
            <button
              type="button"
              class={profile === 'deep' ? 'is-active' : ''}
              title="Глубокое рассуждение. Инструменты тоже доступны — агент может свериться с памятью и состоянием дома."
              onClick={() => setProfile('deep')}
            >
              глубокий
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

  if (message.system) {
    return (
      <div class="msg msg--system">
        <span class="msg__role">команда</span>
        <div class="bubble bubble--system">{message.text}</div>
      </div>
    );
  }

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
