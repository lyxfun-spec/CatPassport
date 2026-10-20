import { describe, expect, it } from 'vitest';
import type { BootstrapState, ConversationMessage, WorkRequest } from './shared';
import { deviceDiscoveryMessage, initialState, reduceBackendEvent } from './state';

const bootstrap: BootstrapState = {
  conversations: [{ id: 'chat-1', mode: 'chat', title: '测试', titleSource: 'generated', createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z', isPinned: false }],
  activeConversationId: 'chat-1',
  activeConversation: {
    conversation: { id: 'chat-1', mode: 'chat', title: '测试', titleSource: 'generated', createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z', isPinned: false },
    messages: [{ id: 'assistant-1', conversationId: 'chat-1', role: 'assistant', source: 'runtime', status: 'streaming', sequence: 2, content: '', createdAt: '2026-01-01T00:00:01Z', runId: 'run-1' }],
    citations: [],
    activeRunId: 'run-1'
  },
  device: { status: '未连接', detail: '', isConnected: false, hasPairedDevice: false, maxPduSize: 0, devices: [], followDesktop: true },
  settings: { asrModel: 'asr', chatModel: 'chat', workModel: 'work', screenTimeoutSeconds: 60, autoReconnect: true, webSearchEnabled: true, hasApiKey: true }
};

describe('conversation stream state', () => {
  it('assembles incremental assistant text for the active run', () => {
    let state = reduceBackendEvent(initialState, { event: 'ui.bootstrap', data: bootstrap });
    state = reduceBackendEvent(state, { event: 'message.delta', data: { type: 'message.delta', conversationId: 'chat-1', messageId: 'assistant-1', runId: 'run-1', delta: '你' } });
    state = reduceBackendEvent(state, { event: 'message.delta', data: { type: 'message.delta', conversationId: 'chat-1', messageId: 'assistant-1', runId: 'run-1', delta: '好' } });
    expect(state.bootstrap?.activeConversation?.messages[0].content).toBe('你好');
  });

  it('attaches streamed deltas to a message that appears mid-run', () => {
    const created: ConversationMessage = {
      id: 'assistant-2', conversationId: 'chat-1', role: 'assistant', source: 'runtime', status: 'streaming',
      sequence: 3, content: '', createdAt: '2026-01-01T00:00:02Z', runId: 'run-1'
    };
    let state = reduceBackendEvent(initialState, { event: 'ui.bootstrap', data: bootstrap });
    state = reduceBackendEvent(state, { event: 'message.created', data: { type: 'message.created', conversationId: 'chat-1', messageId: created.id, runId: 'run-1', message: created } });
    state = reduceBackendEvent(state, { event: 'message.delta', data: { type: 'message.delta', conversationId: 'chat-1', messageId: created.id, runId: 'run-1', delta: '正在生成' } });

    expect(state.bootstrap?.activeConversation?.messages.at(-1)?.content).toBe('正在生成');

    const redelivered = reduceBackendEvent(state, { event: 'message.created', data: { type: 'message.created', conversationId: 'chat-1', messageId: created.id, runId: 'run-1', message: { ...created, content: '正在生成' } } });
    expect(redelivered.bootstrap?.activeConversation?.messages.at(-1)?.content).toBe('正在生成');
  });

  it('keeps assembled stream text when a refresh snapshot is older', () => {
    let state = reduceBackendEvent(initialState, { event: 'ui.bootstrap', data: bootstrap });
    state = reduceBackendEvent(state, { event: 'message.delta', data: { type: 'message.delta', conversationId: 'chat-1', messageId: 'assistant-1', runId: 'run-1', delta: '环境已确认' } });

    const stale: BootstrapState = { ...bootstrap, activeConversation: { ...bootstrap.activeConversation!, messages: [{ ...bootstrap.activeConversation!.messages[0], content: '环境' }] } };
    state = reduceBackendEvent(state, { event: 'ui.bootstrap', data: stale });
    expect(state.bootstrap?.activeConversation?.messages[0].content).toBe('环境已确认');

    state = reduceBackendEvent(state, { event: 'message.delta', data: { type: 'message.delta', conversationId: 'chat-1', messageId: 'assistant-1', runId: 'run-1', delta: '：python-pptx 可用。' } });
    expect(state.bootstrap?.activeConversation?.messages[0].content).toBe('环境已确认：python-pptx 可用。');
  });

  it('adopts the snapshot once the server holds the finished answer', () => {
    let state = reduceBackendEvent(initialState, { event: 'ui.bootstrap', data: bootstrap });
    state = reduceBackendEvent(state, { event: 'message.delta', data: { type: 'message.delta', conversationId: 'chat-1', messageId: 'assistant-1', runId: 'run-1', delta: '你好' } });

    const finished: BootstrapState = {
      ...bootstrap,
      activeConversation: {
        ...bootstrap.activeConversation!,
        activeRunId: undefined,
        messages: [{ ...bootstrap.activeConversation!.messages[0], status: 'complete', content: '你好，世界' }]
      }
    };
    state = reduceBackendEvent(state, { event: 'ui.bootstrap', data: finished });
    expect(state.bootstrap?.activeConversation?.messages[0].content).toBe('你好，世界');
    expect(state.bootstrap?.activeConversation?.messages[0].status).toBe('complete');
  });

  it('marks a partial answer interrupted', () => {
    let state = reduceBackendEvent(initialState, { event: 'ui.bootstrap', data: bootstrap });
    state = reduceBackendEvent(state, { event: 'message.interrupted', data: { type: 'message.interrupted', conversationId: 'chat-1', messageId: 'assistant-1', runId: 'run-1' } });
    expect(state.bootstrap?.activeConversation?.messages[0].status).toBe('interrupted');
    expect(state.bootstrap?.activeConversation?.activeRunId).toBeUndefined();
  });

  it('ends the active run and keeps the failure visible when a run fails', () => {
    let state = reduceBackendEvent(initialState, { event: 'ui.bootstrap', data: bootstrap });
    state = reduceBackendEvent(state, { event: 'run.failed', data: { type: 'run.failed', conversationId: 'chat-1', runId: 'run-1', error: '模型在 10 分钟内没有完成生成，本轮任务已中断。' } });
    expect(state.bootstrap?.activeConversation?.activeRunId).toBeUndefined();
    expect(state.runError).toBe('模型在 10 分钟内没有完成生成，本轮任务已中断。');

    const refreshed = reduceBackendEvent(state, { event: 'ui.bootstrap', data: { ...bootstrap, activeConversation: { ...bootstrap.activeConversation!, activeRunId: undefined } } });
    expect(refreshed.runError).toBe(state.runError);
  });

  it('clears the failure when another conversation is opened', () => {
    let state = reduceBackendEvent(initialState, { event: 'ui.bootstrap', data: bootstrap });
    state = reduceBackendEvent(state, { event: 'run.failed', data: { type: 'run.failed', conversationId: 'chat-1', runId: 'run-1' } });
    expect(state.runError).toBe('AI 服务暂时不可用。');

    state = reduceBackendEvent(state, { event: 'ui.conversation', data: { ...bootstrap.activeConversation!, conversation: { ...bootstrap.activeConversation!.conversation, id: 'chat-2' } } });
    expect(state.runError).toBeUndefined();

    const ignored = reduceBackendEvent(state, { event: 'run.failed', data: { type: 'run.failed', conversationId: 'chat-1', runId: 'run-1' } });
    expect(ignored.runError).toBeUndefined();
  });
});

describe('work request proposals', () => {
  const workRequest: WorkRequest = {
    id: 'req-1', sourceConversationId: 'chat-1', sourceMessageId: 'user-1', sourceRunId: 'run-1', toolCallId: 'call-1',
    title: '发布计划', prompt: '创建一份可审阅的发布计划文档。', contextSummary: '用户想要发布计划。',
    state: 'proposed', createdAt: '2026-01-01T00:00:03Z', updatedAt: '2026-01-01T00:00:03Z'
  };

  it('surfaces a proposal as soon as the event arrives', () => {
    let state = reduceBackendEvent(initialState, { event: 'ui.bootstrap', data: bootstrap });
    state = reduceBackendEvent(state, { event: 'work.request.proposed', data: { type: 'work.request.proposed', conversationId: 'chat-1', workRequest } });

    expect(state.bootstrap?.activeConversation?.workRequests).toEqual([workRequest]);
  });

  it('updates the proposal in place when it is resolved', () => {
    let state = reduceBackendEvent(initialState, { event: 'ui.bootstrap', data: bootstrap });
    state = reduceBackendEvent(state, { event: 'work.request.proposed', data: { type: 'work.request.proposed', conversationId: 'chat-1', workRequest } });
    state = reduceBackendEvent(state, {
      event: 'work.request.resolved',
      data: { type: 'work.request.resolved', conversationId: 'chat-1', workRequest: { ...workRequest, state: 'started', targetConversationId: 'work-1' } }
    });

    const requests = state.bootstrap?.activeConversation?.workRequests;
    expect(requests).toHaveLength(1);
    expect(requests?.[0].state).toBe('started');
    expect(requests?.[0].targetConversationId).toBe('work-1');
  });

  it('ignores proposals that belong to another conversation', () => {
    let state = reduceBackendEvent(initialState, { event: 'ui.bootstrap', data: bootstrap });
    state = reduceBackendEvent(state, {
      event: 'work.request.proposed',
      data: { type: 'work.request.proposed', conversationId: 'chat-2', workRequest: { ...workRequest, sourceConversationId: 'chat-2' } }
    });

    expect(state.bootstrap?.activeConversation?.workRequests ?? []).toHaveLength(0);
  });
});

describe('backend startup failure', () => {
  it('shows the backend diagnostic instead of a generic not-started error', () => {
    const state = reduceBackendEvent(initialState, {
      event: 'backend.exited',
      data: { code: 1, message: '后台服务已退出：数据库迁移失败' }
    });
    expect(state.backendError).toBe('后台服务已退出：数据库迁移失败');
  });
});

describe('device discovery message', () => {
  it('explains that Windows Bluetooth must be enabled', () => {
    const device = {
      ...bootstrap.device,
      status: '蓝牙错误：BluetoothDisabled',
      detail: 'Windows 蓝牙已关闭；请先在系统设置中打开蓝牙，然后重新扫描。'
    };

    expect(deviceDiscoveryMessage(device)).toBe(device.detail);
  });

  it('keeps the scanning prompt during a normal scan', () => {
    expect(deviceDiscoveryMessage({ ...bootstrap.device, status: '扫描中' }))
      .toBe('正在查找附近的 AI 卡片…');
  });

  it('shows the Bluetooth prompt if the radio is turned off during scanning', () => {
    const device = {
      ...bootstrap.device,
      status: '扫描中',
      detail: 'Windows 蓝牙已关闭；请先在系统设置中打开蓝牙，然后重新扫描。'
    };

    expect(deviceDiscoveryMessage(device)).toBe(device.detail);
  });
});
