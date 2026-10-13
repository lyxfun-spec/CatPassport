import type { BackendEvent, BackendSnapshot } from './shared';

export interface UiState { snapshot?: BackendSnapshot; streamingText: string; backendError?: string; }
export const initialState: UiState = { streamingText: '' };

export function reduceBackendEvent(state: UiState, event: BackendEvent): UiState {
  if (event.event === 'backend.ready') return { snapshot: event.data.state, streamingText: '' };
  if (event.event === 'backend.exited') return { ...state, backendError: `后台服务已退出${event.data.code === null ? '' : `（代码 ${event.data.code}）`}` };
  if (event.event === 'state.changed') {
    const last = event.data.messages.at(-1);
    return { ...state, snapshot: event.data, streamingText: last?.role === 'assistant' ? '' : state.streamingText, backendError: undefined };
  }
  if (event.data.phase === 'started') return { ...state, streamingText: '' };
  if (event.data.phase === 'delta') return { ...state, streamingText: state.streamingText + (event.data.delta ?? '') };
  return { ...state, streamingText: '' };
}
