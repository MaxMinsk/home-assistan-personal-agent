import { useCallback, useEffect, useState } from 'preact/hooks';
import {
  createAgent,
  deleteAgent,
  getAgent,
  listAgentRuns,
  replyToAgent,
  runAgentNow,
  setAgentStatus,
  updateAgent,
  type AgentDetail,
  type AgentRun,
  type AgentToolScope,
  type AgentUpsert,
} from './api';

// Что: экраны управления автономным агентом (детальная панель и форма создания).
// Зачем: всё, что обещал эпик — создать, отредактировать, поставить на паузу, запустить сейчас, прочитать сводки и ответить на вопрос.
// Как: вкладки Обзор/Запуски/Вопросы/Настройки/Память поверх /api/agents; ответ кладётся в ту же очередь, что и Telegram-reply.

type AgentTab = 'overview' | 'runs' | 'questions' | 'settings' | 'memory';

const SCHEDULE_LABELS: Record<string, string> = {
  Manual: 'вручную',
  Hourly: 'каждый час',
  Daily: 'каждый день',
  Weekly: 'каждую неделю',
  Cron: 'cron',
};

const DEFAULT_TOOL_SCOPE: AgentToolScope = {
  allowHomeAssistantRead: true,
  allowWebSearch: true,
  allowMemoryRead: true,
  allowMemoryWrite: true,
  maxDurableFactsPerRun: 3,
};

export function formatDateTime(iso: string | null | undefined): string {
  if (!iso) {
    return '—';
  }
  const parsed = new Date(iso);
  if (Number.isNaN(parsed.getTime())) {
    return '—';
  }
  return parsed.toLocaleString(undefined, {
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
  });
}

export function scheduleLabel(kind: string, expression?: string | null): string {
  const label = SCHEDULE_LABELS[kind] ?? kind;
  return kind === 'Cron' && expression ? `${label} (${expression})` : label;
}

export function AgentDetailView(props: {
  agentId: string;
  onChanged: () => void;
  onDeleted: () => void;
}) {
  const { agentId, onChanged, onDeleted } = props;
  const [tab, setTab] = useState<AgentTab>('overview');
  const [agent, setAgent] = useState<AgentDetail | null>(null);
  const [runs, setRuns] = useState<AgentRun[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(() => {
    setError(null);
    getAgent(agentId)
      .then(setAgent)
      .catch((e: Error) => setError(e.message));
    listAgentRuns(agentId)
      .then(setRuns)
      .catch(() => setRuns([]));
  }, [agentId]);

  useEffect(() => {
    setTab('overview');
    load();
  }, [load]);

  const act = useCallback(
    async (action: () => Promise<unknown>) => {
      setBusy(true);
      setError(null);
      try {
        await action();
        load();
        onChanged();
      } catch (e) {
        setError((e as Error).message);
      } finally {
        setBusy(false);
      }
    },
    [load, onChanged],
  );

  if (error && !agent) {
    return <div class="panel-pad"><div class="state state--error">{error}</div></div>;
  }

  if (!agent) {
    return <div class="panel-pad"><div class="state">Загрузка…</div></div>;
  }

  const paused = agent.status === 'Paused';

  return (
    <div class="agent-detail">
      <nav class="tabs tabs--inset">
        <TabButton id="overview" active={tab} label="Обзор" onSelect={setTab} />
        <TabButton id="runs" active={tab} label={`Запуски${runs.length ? ` (${runs.length})` : ''}`} onSelect={setTab} />
        <TabButton id="questions" active={tab} label="Вопросы" onSelect={setTab} />
        <TabButton id="settings" active={tab} label="Настройки" onSelect={setTab} />
        <TabButton id="memory" active={tab} label="Память" onSelect={setTab} />
      </nav>

      {error && <div class="panel-pad"><div class="state state--error">{error}</div></div>}

      {tab === 'overview' && (
        <div class="panel-pad">
          <div class="panel-head">
            <h2>Миссия</h2>
            <div class="actions">
              <button class="mini" type="button" disabled={busy} onClick={() => void act(() => runAgentNow(agent.id))}>
                Запустить сейчас
              </button>
              <button
                class="mini"
                type="button"
                disabled={busy}
                onClick={() => void act(() => setAgentStatus(agent.id, paused ? 'Active' : 'Paused'))}
              >
                {paused ? 'Возобновить' : 'Пауза'}
              </button>
            </div>
          </div>
          <div class="summary-box">{agent.mission}</div>

          <div class="metrics metrics--tight">
            <Metric k="Состояние" v={paused ? 'на паузе' : agent.hasRunningRun ? 'идёт запуск' : 'активен'} />
            <Metric k="Расписание" v={scheduleLabel(agent.scheduleKind, agent.scheduleExpression)} />
            <Metric k="Следующий запуск" v={formatDateTime(agent.nextRunUtc)} />
            <Metric k="Последний запуск" v={formatDateTime(agent.lastRunUtc)} />
            <Metric k="Ответов в очереди" v={agent.pendingReplyCount} />
            <Metric k="Доставка" v={agent.deliveryTelegramChatId ? 'Telegram' : 'только панель'} />
          </div>

          <div class="danger-row">
            <button
              class="link-btn"
              type="button"
              disabled={busy}
              onClick={() => {
                if (confirm(`Удалить агента «${agent.name}»? Его запуски и очередь ответов будут удалены.`)) {
                  void act(async () => {
                    await deleteAgent(agent.id);
                    onDeleted();
                  });
                }
              }}
            >
              Удалить агента
            </button>
          </div>
        </div>
      )}

      {tab === 'runs' && (
        <div class="panel-pad">
          <div class="panel-head">
            <h2>История запусков</h2>
            <button class="mini" type="button" onClick={load}>Обновить</button>
          </div>
          {runs.length === 0 && <div class="state">Запусков пока не было.</div>}
          {runs.map((run) => (
            <article class="run" key={run.id}>
              <header class="run__head">
                <span class={`chip chip--${run.status.toLowerCase()}`}>{runStatusLabel(run.status)}</span>
                <span class="version">{formatDateTime(run.startedUtc)}</span>
              </header>
              {run.summary && <div class="run__body">{run.summary}</div>}
              {run.error && <div class="state state--error">{run.error}</div>}
              {run.questions.length > 0 && (
                <ul class="run__questions">
                  {run.questions.map((question) => <li key={question}>{question}</li>)}
                </ul>
              )}
            </article>
          ))}
        </div>
      )}

      {tab === 'questions' && (
        <QuestionsTab agent={agent} runs={runs} onAnswered={() => { load(); onChanged(); }} />
      )}

      {tab === 'settings' && (
        <AgentForm
          initial={agent}
          submitLabel="Сохранить"
          onSubmit={async (request) => {
            await updateAgent(agent.id, request);
            load();
            onChanged();
          }}
        />
      )}

      {tab === 'memory' && (
        <div class="panel-pad">
          <div class="panel-head">
            <h2>Состояние исследования</h2>
            <button class="mini" type="button" onClick={load}>Обновить</button>
          </div>
          <div class="metrics metrics--tight">
            <Metric k="Капсула в памяти" v={agent.capsuleNoteKey ? 'есть' : 'нет'} />
            <Metric k="Обновлена" v={formatDateTime(agent.capsuleUpdatedUtc)} />
          </div>
          <h3 class="sub-head">Следующий шаг</h3>
          <div class="summary-box">{agent.focus || 'Агент ещё не обозначил следующий шаг.'}</div>
          <h3 class="sub-head">Открытые вопросы</h3>
          <div class="summary-box">{agent.openQuestions || 'Открытых вопросов нет.'}</div>
          {agent.capsuleNoteKey && (
            <p class="version">Ключ заметки: {agent.capsuleNoteKey}</p>
          )}
        </div>
      )}
    </div>
  );
}

function QuestionsTab(props: { agent: AgentDetail; runs: AgentRun[]; onAnswered: () => void }) {
  const { agent, runs, onAnswered } = props;
  const [text, setText] = useState('');
  const [busy, setBusy] = useState(false);
  const [sent, setSent] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const latestQuestions = runs.find((run) => run.questions.length > 0)?.questions ?? [];

  const send = useCallback(async () => {
    if (!text.trim() || busy) {
      return;
    }
    setBusy(true);
    setError(null);
    try {
      await replyToAgent(agent.id, text.trim());
      setText('');
      setSent(true);
      onAnswered();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }, [agent.id, text, busy, onAnswered]);

  return (
    <div class="panel-pad">
      <div class="panel-head">
        <h2>Вопросы агента</h2>
        {agent.pendingReplyCount > 0 && (
          <span class="chip">в очереди: {agent.pendingReplyCount}</span>
        )}
      </div>

      {latestQuestions.length === 0 ? (
        <div class="state">Агент пока ничего не спрашивал.</div>
      ) : (
        <ol class="questions">
          {latestQuestions.map((question) => <li key={question}>{question}</li>)}
        </ol>
      )}

      <h3 class="sub-head">Твой ответ</h3>
      <p class="hint">
        Ответ не запускает агента — он попадёт в контекст следующего запуска
        ({formatDateTime(agent.nextRunUtc)}).
      </p>
      <textarea
        class="form-input form-input--area"
        value={text}
        placeholder="Например: интересует только B2B, бюджет до 20k."
        onInput={(e) => { setText((e.target as HTMLTextAreaElement).value); setSent(false); }}
      />
      {error && <div class="state state--error">{error}</div>}
      <div class="form-actions">
        <button class="send" type="button" disabled={busy || !text.trim()} onClick={() => void send()}>
          {busy ? '…' : 'Поставить в очередь'}
        </button>
        {sent && <span class="hint">Ответ поставлен в очередь.</span>}
      </div>
    </div>
  );
}

export function AgentForm(props: {
  initial?: AgentDetail;
  submitLabel: string;
  onSubmit: (request: AgentUpsert) => Promise<void>;
  onCancel?: () => void;
}) {
  const { initial, submitLabel, onSubmit, onCancel } = props;
  const [name, setName] = useState(initial?.name ?? '');
  const [mission, setMission] = useState(initial?.mission ?? '');
  const [scheduleKind, setScheduleKind] = useState(initial?.scheduleKind ?? 'Weekly');
  const [scheduleExpression, setScheduleExpression] = useState(initial?.scheduleExpression ?? '');
  const [chatId, setChatId] = useState(
    initial?.deliveryTelegramChatId ? String(initial.deliveryTelegramChatId) : '',
  );
  const [scope, setScope] = useState<AgentToolScope>(initial?.toolScope ?? DEFAULT_TOOL_SCOPE);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);

  const submit = useCallback(async () => {
    if (busy) {
      return;
    }
    setBusy(true);
    setError(null);
    try {
      await onSubmit({
        name: name.trim(),
        mission: mission.trim(),
        scheduleKind,
        scheduleExpression: scheduleKind === 'Cron' ? scheduleExpression.trim() : null,
        deliveryTelegramChatId: chatId.trim() ? Number(chatId.trim()) : null,
        toolScope: scope,
      });
      setSaved(true);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }, [busy, name, mission, scheduleKind, scheduleExpression, chatId, scope, onSubmit]);

  return (
    <div class="panel-pad">
      <label class="form-row">
        <span class="form-label">Название</span>
        <input
          class="form-input"
          value={name}
          placeholder="Еженедельный дайджест"
          onInput={(e) => { setName((e.target as HTMLInputElement).value); setSaved(false); }}
        />
      </label>

      <label class="form-row">
        <span class="form-label">Миссия — что исследовать</span>
        <textarea
          class="form-input form-input--area"
          value={mission}
          placeholder="Опиши, что отслеживать и на что смотреть. Например: следи за выбранной темой и раз в неделю присылай ключевые находки и открытые вопросы."
          onInput={(e) => { setMission((e.target as HTMLTextAreaElement).value); setSaved(false); }}
        />
      </label>

      <div class="form-grid">
        <label class="form-row">
          <span class="form-label">Расписание</span>
          <select
            class="form-input"
            value={scheduleKind}
            onChange={(e) => { setScheduleKind((e.target as HTMLSelectElement).value); setSaved(false); }}
          >
            <option value="Manual">Только вручную</option>
            <option value="Hourly">Каждый час</option>
            <option value="Daily">Каждый день</option>
            <option value="Weekly">Каждую неделю</option>
            <option value="Cron">Cron</option>
          </select>
        </label>

        {scheduleKind === 'Cron' && (
          <label class="form-row">
            <span class="form-label">Cron (5 полей)</span>
            <input
              class="form-input"
              value={scheduleExpression}
              placeholder="0 9 * * 1"
              onInput={(e) => { setScheduleExpression((e.target as HTMLInputElement).value); setSaved(false); }}
            />
          </label>
        )}

        <label class="form-row">
          <span class="form-label">Telegram chat id (необязательно)</span>
          <input
            class="form-input"
            value={chatId}
            placeholder="оставь пустым — сводка только в панели"
            onInput={(e) => { setChatId((e.target as HTMLInputElement).value); setSaved(false); }}
          />
        </label>
      </div>

      <h3 class="sub-head">Что агенту разрешено</h3>
      <p class="hint">
        Фоновый запуск идёт без тебя, поэтому управление устройствами недоступно — только чтение и
        ограниченная запись в память.
      </p>
      <div class="checks">
        <Check
          label="Читать состояние Home Assistant"
          checked={scope.allowHomeAssistantRead}
          onToggle={(v) => { setScope({ ...scope, allowHomeAssistantRead: v }); setSaved(false); }}
        />
        <Check
          label="Искать в вебе"
          checked={scope.allowWebSearch}
          onToggle={(v) => { setScope({ ...scope, allowWebSearch: v }); setSaved(false); }}
        />
        <Check
          label="Читать долговременную память"
          checked={scope.allowMemoryRead}
          onToggle={(v) => {
            setScope({ ...scope, allowMemoryRead: v, allowMemoryWrite: v ? scope.allowMemoryWrite : false });
            setSaved(false);
          }}
        />
        <Check
          label="Записывать в долговременную память"
          checked={scope.allowMemoryWrite}
          disabled={!scope.allowMemoryRead}
          onToggle={(v) => { setScope({ ...scope, allowMemoryWrite: v }); setSaved(false); }}
        />
      </div>

      <label class="form-row form-row--narrow">
        <span class="form-label">Максимум фактов в память за запуск</span>
        <input
          class="form-input"
          type="number"
          min={0}
          max={5}
          value={scope.maxDurableFactsPerRun}
          onInput={(e) => {
            setScope({ ...scope, maxDurableFactsPerRun: Number((e.target as HTMLInputElement).value) });
            setSaved(false);
          }}
        />
      </label>

      {error && <div class="state state--error">{error}</div>}

      <div class="form-actions">
        <button class="send" type="button" disabled={busy || !name.trim() || !mission.trim()} onClick={() => void submit()}>
          {busy ? '…' : submitLabel}
        </button>
        {onCancel && (
          <button class="mini" type="button" onClick={onCancel}>Отмена</button>
        )}
        {saved && <span class="hint">Сохранено.</span>}
      </div>
    </div>
  );
}

export function AgentCreateView(props: { onCreated: (agentId: string) => void; onCancel: () => void }) {
  return (
    <AgentForm
      submitLabel="Создать агента"
      onCancel={props.onCancel}
      onSubmit={async (request) => {
        const created = await createAgent(request);
        props.onCreated(created.id);
      }}
    />
  );
}

function Check(props: { label: string; checked: boolean; disabled?: boolean; onToggle: (value: boolean) => void }) {
  return (
    <label class={props.disabled ? 'check is-disabled' : 'check'}>
      <input
        type="checkbox"
        checked={props.checked}
        disabled={props.disabled}
        onChange={(e) => props.onToggle((e.target as HTMLInputElement).checked)}
      />
      <span>{props.label}</span>
    </label>
  );
}

function Metric(props: { k: string; v: string | number }) {
  return (
    <div class="metric">
      <div class="metric__k">{props.k}</div>
      <div class="metric__v metric__v--sm">{props.v}</div>
    </div>
  );
}

function TabButton(props: { id: AgentTab; active: AgentTab; label: string; onSelect: (t: AgentTab) => void }) {
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

function runStatusLabel(status: string): string {
  if (status === 'Completed') {
    return 'завершён';
  }
  if (status === 'Failed') {
    return 'ошибка';
  }
  return 'идёт';
}
