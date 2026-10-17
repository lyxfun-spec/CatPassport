import { describe, expect, it } from 'vitest';
import type { BootstrapState } from './shared';
import { initialState, reduceBackendEvent } from './state';

const bootstrap: BootstrapState = {
  conversations: [{ id: 'chat-1', mode: 'chat', title: '测试', titleSource: 'generated', createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z' }],
  activeConversationId: 'chat-1',
  activeConversation: {
    conversation: { id: 'chat-1', mode: 'chat', title: '测试', titleSource: 'generated', createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z' },
    messages: [{ id: 'assistant-1', conversationId: 'chat-1', role: 'assistant', source: 'runtime', status: 'streaming', sequence: 2, content: '', createdAt: '2026-01-01T00:00:01Z', runId: 'run-1' }],
    citations: [],
    activeRunId: 'run-1'
  },
  device: { status: '未连接', detail: '', isConnected: false, hasPairedDevice: false, maxPduSize: 0, devices: [], followDesktop: true },
  settings: { asrModel: 'asr', chatModel: 'chat', screenTimeoutSeconds: 60, autoReconnect: true, webSearchEnabled: true, hasApiKey: true }
};

describe('conversation stream state', () => {
  it('assembles incremental assistant text for the active run', () => {
    let state = reduceBackendEvent(initialState, { event: 'ui.bootstrap', data: bootstrap });
    state = reduceBackendEvent(state, { event: 'message.delta', data: { type: 'message.delta', conversationId: 'chat-1', messageId: 'assistant-1', runId: 'run-1', delta: '你' } });
    state = reduceBackendEvent(state, { event: 'message.delta', data: { type: 'message.delta', conversationId: 'chat-1', messageId: 'assistant-1', runId: 'run-1', delta: '好' } });
    expect(state.bootstrap?.activeConversation?.messages[0].content).toBe('你好');
  });

  it('marks a partial answer interrupted', () => {
    let state = reduceBackendEvent(initialState, { event: 'ui.bootstrap', data: bootstrap });
    state = reduceBackendEvent(state, { event: 'message.interrupted', data: { type: 'message.interrupted', conversationId: 'chat-1', messageId: 'assistant-1', runId: 'run-1' } });
    expect(state.bootstrap?.activeConversation?.messages[0].status).toBe('interrupted');
    expect(state.bootstrap?.activeConversation?.activeRunId).toBeUndefined();
  });
});
