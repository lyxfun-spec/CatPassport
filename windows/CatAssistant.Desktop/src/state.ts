import { t } from './i18n';
import type { BackendEvent, BootstrapState, ConversationDetail, ConversationEvent, DeviceState, WorkRequest } from './shared';

export interface UiState {
  bootstrap?: BootstrapState;
  backendError?: string;
  runError?: string;
  endedRunIds: ReadonlySet<string>;
}

export type UiAction =
  | BackendEvent
  | { event: 'ui.bootstrap'; data: BootstrapState }
  | { event: 'ui.conversation'; data: ConversationDetail }
  | { event: 'ui.device'; data: DeviceState }
  | { event: 'ui.clearError'; data?: undefined };

export const initialState: UiState = { endedRunIds: new Set() };

export function deviceDiscoveryMessage(device: DeviceState): string {
  const scanFailed = device.status.startsWith('蓝牙错误')
    || device.detail.startsWith('Windows 蓝牙已关闭')
    || device.detail.startsWith('蓝牙扫描错误');
  return scanFailed && device.detail
    ? t(device.detail)
    : t("正在查找附近的 AI 卡片…");
}

export function reduceBackendEvent(state: UiState, action: UiAction): UiState {
  if (action.event === 'backend.ready' || action.event === 'ui.bootstrap') {
    const incoming = action.event === 'backend.ready' ? action.data.state : action.data;
    const bootstrap: BootstrapState = { ...incoming, activeConversation: keepAheadOfSnapshot(state.bootstrap?.activeConversation, withoutEndedRun(incoming.activeConversation, state.endedRunIds)) };
    return { bootstrap, backendError: undefined, runError: state.runError, endedRunIds: state.endedRunIds };
  }
  if (action.event === 'backend.exited') {
    return { ...state, backendError: action.data.message ?? t("后台服务已退出{0}", action.data.code === null ? '' : t("（代码 {0}）", action.data.code)) };
  }
  if (action.event === 'device.changed' || action.event === 'ui.device') {
    if (!state.bootstrap) return state;
    return { ...state, bootstrap: { ...state.bootstrap, device: action.data } };
  }
  if (action.event === 'ui.conversation') {
    if (!state.bootstrap) return state;
    return {
      ...state,
      runError: undefined,
      bootstrap: {
        ...state.bootstrap,
        activeConversationId: action.data.conversation.id,
        activeConversation: action.data
      }
    };
  }
  if (action.event === 'ui.clearError') return { ...state, backendError: undefined, runError: undefined };
  if (!state.bootstrap || !isConversationEvent(action.data)) return state;
  return reduceConversationEvent(state, action.data);
}

// A bootstrap snapshot can be read before the run's terminal event and arrive after it,
// so a run the desktop has already seen finish must not come back as active.
function withoutEndedRun(detail: ConversationDetail | undefined, ended: ReadonlySet<string>): ConversationDetail | undefined {
  return detail?.activeRunId && ended.has(detail.activeRunId) ? { ...detail, activeRunId: undefined } : detail;
}

// A bootstrap snapshot is read while deltas keep arriving, so it can lag the assembled text.
// Backend persists content before publishing, therefore local content is always an extension of the snapshot.
function keepAheadOfSnapshot(local: ConversationDetail | undefined, incoming: ConversationDetail | undefined): ConversationDetail | undefined {
  if (!local || !incoming || local.conversation.id !== incoming.conversation.id) return incoming;
  const streaming = new Map(local.messages.filter(message => message.status === 'streaming').map(message => [message.id, message]));
  if (streaming.size === 0) return incoming;
  let replaced = false;
  const messages = incoming.messages.map(message => {
    const ahead = message.status === 'streaming' ? streaming.get(message.id) : undefined;
    if (!ahead || ahead.content.length <= message.content.length || !ahead.content.startsWith(message.content)) return message;
    replaced = true;
    return { ...message, content: ahead.content };
  });
  return replaced ? { ...incoming, messages } : incoming;
}

function reduceConversationEvent(state: UiState, event: ConversationEvent): UiState {
  const bootstrap = state.bootstrap!;
  const detail = bootstrap.activeConversation;
  if (!detail || detail.conversation.id !== event.conversationId) return state;
  if (event.type === 'run.completed' || event.type === 'run.stopped' || event.type === 'run.failed') {
    const endedRunIds = event.runId ? new Set(state.endedRunIds).add(event.runId) : state.endedRunIds;
    const activeRunId = event.runId && event.runId !== detail.activeRunId ? detail.activeRunId : undefined;
    const next: UiState = { ...state, endedRunIds, bootstrap: { ...bootstrap, activeConversation: { ...detail, activeRunId } } };
    return event.type === 'run.failed' ? { ...next, runError: event.error ?? t("AI 服务暂时不可用。") } : next;
  }
  if (event.type === 'message.created' && event.message) {
    const created = event.message;
    if (detail.messages.some(message => message.id === created.id)) return state;
    return {
      ...state,
      bootstrap: {
        ...bootstrap,
        activeConversation: { ...detail, messages: [...detail.messages, created], activeRunId: event.runId ?? detail.activeRunId }
      }
    };
  }
  if (event.type === 'message.delta' && event.messageId && event.delta) {
    const messages = detail.messages.map(message => message.id === event.messageId
      ? { ...message, content: message.content + event.delta, status: 'streaming' as const }
      : message);
    return { ...state, bootstrap: { ...bootstrap, activeConversation: { ...detail, messages, activeRunId: event.runId } } };
  }
  if (event.type === 'message.completed' && event.message) {
    const completed = event.message;
    const index = detail.messages.findIndex(message => message.id === completed.id);
    const messages = index < 0 ? [...detail.messages, completed] : detail.messages.map(message =>
      message.id === completed.id ? completed : message
    );
    return {
      ...state,
      bootstrap: {
        ...bootstrap,
        activeConversation: { ...detail, messages, activeRunId: event.runId ?? detail.activeRunId }
      }
    };
  }
  if (event.type === 'message.interrupted' && event.messageId) {
    const messages = detail.messages.map(message => message.id === event.messageId
      ? { ...message, status: 'interrupted' as const }
      : message);
    const endedRunIds = event.runId ? new Set(state.endedRunIds).add(event.runId) : state.endedRunIds;
    return { ...state, endedRunIds, bootstrap: { ...bootstrap, activeConversation: { ...detail, messages, activeRunId: undefined } } };
  }
  if ((event.type === 'work.request.proposed' || event.type === 'work.request.resolved') && event.workRequest) {
    const workRequests = upsertWorkRequest(detail.workRequests ?? [], event.workRequest);
    return { ...state, bootstrap: { ...bootstrap, activeConversation: { ...detail, workRequests } } };
  }
  return state;
}

function upsertWorkRequest(requests: WorkRequest[], request: WorkRequest): WorkRequest[] {
  const index = requests.findIndex(item => item.id === request.id);
  if (index < 0) return [...requests, request];
  const next = requests.slice();
  next[index] = request;
  return next;
}

function isConversationEvent(value: unknown): value is ConversationEvent {
  return typeof value === 'object' && value !== null && 'conversationId' in value && 'type' in value;
}
