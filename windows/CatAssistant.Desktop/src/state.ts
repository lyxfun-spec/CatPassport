import type { BackendEvent, BootstrapState, ConversationDetail, ConversationEvent, DeviceState } from './shared';

export interface UiState {
  bootstrap?: BootstrapState;
  backendError?: string;
  runError?: string;
}

export type UiAction =
  | BackendEvent
  | { event: 'ui.bootstrap'; data: BootstrapState }
  | { event: 'ui.conversation'; data: ConversationDetail }
  | { event: 'ui.device'; data: DeviceState }
  | { event: 'ui.clearError'; data?: undefined };

export const initialState: UiState = {};

export function deviceDiscoveryMessage(device: DeviceState): string {
  const scanFailed = device.status.startsWith('蓝牙错误')
    || device.detail.startsWith('Windows 蓝牙已关闭')
    || device.detail.startsWith('蓝牙扫描错误');
  return scanFailed && device.detail
    ? device.detail
    : '正在查找附近的 AI 卡片…';
}

export function reduceBackendEvent(state: UiState, action: UiAction): UiState {
  if (action.event === 'backend.ready' || action.event === 'ui.bootstrap') {
    const bootstrap = action.event === 'backend.ready' ? action.data.state : action.data;
    return { bootstrap, backendError: undefined };
  }
  if (action.event === 'backend.exited') {
    return { ...state, backendError: action.data.message ?? `后台服务已退出${action.data.code === null ? '' : `（代码 ${action.data.code}）`}` };
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

function reduceConversationEvent(state: UiState, event: ConversationEvent): UiState {
  const bootstrap = state.bootstrap!;
  const detail = bootstrap.activeConversation;
  if (event.type === 'run.failed') return { ...state, runError: event.error ?? 'AI 服务暂时不可用。' };
  if (!detail || detail.conversation.id !== event.conversationId) return state;
  if (event.type === 'message.delta' && event.messageId && event.delta) {
    const messages = detail.messages.map(message => message.id === event.messageId
      ? { ...message, content: message.content + event.delta, status: 'streaming' as const }
      : message);
    return { ...state, bootstrap: { ...bootstrap, activeConversation: { ...detail, messages, activeRunId: event.runId } } };
  }
  if (event.type === 'message.interrupted' && event.messageId) {
    const messages = detail.messages.map(message => message.id === event.messageId
      ? { ...message, status: 'interrupted' as const }
      : message);
    return { ...state, bootstrap: { ...bootstrap, activeConversation: { ...detail, messages, activeRunId: undefined } } };
  }
  return state;
}

function isConversationEvent(value: unknown): value is ConversationEvent {
  return typeof value === 'object' && value !== null && 'conversationId' in value && 'type' in value;
}
