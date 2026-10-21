import { describe, expect, it } from 'vitest';
import type { ConversationMessage, WorkArtifact, WorkToolCall } from './shared';
import { assistantCopyTargets, buildWorkTimeline, clampTextareaHeight, greetingForHour, isNearScrollEnd, scrollThumbMetrics, shouldSubmitComposer, summarizeWorkToolGroup, workTimelineCopyActions } from './ui-behavior';

describe('composer keyboard behavior', () => {
  it('submits on plain Enter', () => {
    expect(shouldSubmitComposer('Enter', false, false)).toBe(true);
  });

  it('does not submit during Chinese IME composition or on Shift+Enter', () => {
    expect(shouldSubmitComposer('Enter', false, true)).toBe(false);
    expect(shouldSubmitComposer('Enter', true, false)).toBe(false);
  });
});

describe('scroll and textarea behavior', () => {
  it('only treats the viewport as pinned when it is near the latest message', () => {
    expect(isNearScrollEnd(1000, 620, 300)).toBe(true);
    expect(isNearScrollEnd(1000, 300, 300)).toBe(false);
  });

  it('keeps the composer within its visual bounds', () => {
    expect(clampTextareaHeight(12)).toBe(38);
    expect(clampTextareaHeight(84)).toBe(84);
    expect(clampTextareaHeight(400)).toBe(152);
  });

  it('maps scroll progress onto a compact overlay thumb', () => {
    expect(scrollThumbMetrics(1000, 250, 375, 200)).toEqual({ visible: true, size: 50, offset: 75 });
    expect(scrollThumbMetrics(200, 250, 0, 200)).toEqual({ visible: false, size: 0, offset: 0 });
  });
});

describe('empty conversation greeting', () => {
  it('uses a restrained time-aware greeting', () => {
    expect(greetingForHour(8)).toBe('早上好，想聊些什么？');
    expect(greetingForHour(14)).toBe('下午好，想聊些什么？');
    expect(greetingForHour(21)).toBe('晚上好，想聊些什么？');
  });
});

describe('Work timeline', () => {
  const message = (id: string, createdAt: string): ConversationMessage => ({
    id, conversationId: 'work-1', role: id.startsWith('user') ? 'user' : 'assistant', source: 'runtime',
    status: 'complete', sequence: 1, content: id, createdAt, runId: 'run-1'
  });
  const tool = (id: string, createdAt: string): WorkToolCall => ({
    id, runId: 'run-1', roundId: 'round-1', batchId: 'batch-1', toolCallId: `call-${id}`, ordinal: 0,
    toolName: 'read_file', argumentsJson: '{}', state: 'completed', createdAt
  });
  const artifact = (id: string, createdAt: string): WorkArtifact => ({
    id, conversationId: 'work-1', runId: 'run-1', path: `C:\\${id}.docx`, label: `${id}.docx`, createdAt
  });

  it('groups consecutive tools between visible messages', () => {
    const timeline = buildWorkTimeline(
      [message('user-1', '2026-01-01T00:00:00Z'), message('assistant-1', '2026-01-01T00:00:03Z')],
      [tool('tool-1', '2026-01-01T00:00:01Z'), tool('tool-2', '2026-01-01T00:00:02Z')],
      []
    );

    expect(timeline.map(item => item.kind)).toEqual(['message', 'tools', 'message']);
    expect(timeline[1].kind === 'tools' && timeline[1].tools.map(item => item.id)).toEqual(['tool-1', 'tool-2']);
  });

  it('uses delivered artifacts as a boundary between tool groups', () => {
    const timeline = buildWorkTimeline(
      [message('user-1', '2026-01-01T00:00:00Z')],
      [tool('tool-1', '2026-01-01T00:00:01Z'), tool('tool-2', '2026-01-01T00:00:03Z')],
      [artifact('result', '2026-01-01T00:00:02Z')]
    );

    expect(timeline.map(item => item.kind)).toEqual(['message', 'tools', 'artifacts', 'tools']);
  });

  it('groups artifacts delivered together', () => {
    const timeline = buildWorkTimeline([], [], [
      artifact('deck', '2026-01-01T00:00:01Z'), artifact('notes', '2026-01-01T00:00:02Z')
    ]);

    expect(timeline).toHaveLength(1);
    expect(timeline[0].kind === 'artifacts' && timeline[0].artifacts).toHaveLength(2);
  });
});

describe('Work transient activity', () => {
  const message = (id: string, role: 'user' | 'assistant', status: ConversationMessage['status'], content: string, runId = 'run-1'): ConversationMessage => ({
    id, conversationId: 'work-1', role, source: 'runtime', status, sequence: 1, content,
    createdAt: '2026-01-01T00:00:00Z', runId
  });
  const tool = (state: string): WorkToolCall => ({
    id: `tool-${state}`, runId: 'run-1', roundId: 'round-1', batchId: 'batch-1', toolCallId: `call-${state}`,
    ordinal: 0, toolName: 'read_file', argumentsJson: '{}', state, createdAt: '2026-01-01T00:00:01Z'
  });

  it('places one copy target at the end of each completed run', () => {
    const messages = [
      message('assistant-1', 'assistant', 'complete', '先读取资料。'),
      message('assistant-2', 'assistant', 'complete', '已经生成结果。'),
      message('assistant-3', 'assistant', 'complete', '另一个回答。', 'run-2')
    ];
    const targets = assistantCopyTargets(messages);

    expect([...targets.keys()]).toEqual(['assistant-2', 'assistant-3']);
    expect(targets.get('assistant-2')).toBe('先读取资料。\n\n已经生成结果。');
  });

  it('does not expose a copy action before the active run finishes', () => {
    const targets = assistantCopyTargets([
      message('assistant-1', 'assistant', 'complete', '阶段输出。')
    ], 'run-1');
    expect(targets.size).toBe(0);
  });

  it('places the run copy action after trailing tools', () => {
    const messages = [message('assistant-1', 'assistant', 'interrupted', '阶段输出。')];
    const tools = [tool('cancelled')];
    const timeline = buildWorkTimeline(messages, tools, []);
    const actions = workTimelineCopyActions(timeline, messages);

    expect([...actions.keys()]).toEqual(['tools-tool-cancelled']);
    expect(actions.get('tools-tool-cancelled')).toBe('阶段输出。');
  });
});

describe('Work tool group summary', () => {
  const tool = (id: string, state: string, errorCode?: string): WorkToolCall => ({
    id, runId: 'run-1', roundId: 'round-1', batchId: 'batch-1', toolCallId: `call-${id}`,
    ordinal: 0, toolName: 'read_file', argumentsJson: '{}', state, errorCode, createdAt: '2026-01-01T00:00:01Z'
  });

  it('uses one consistent format while tools are running', () => {
    expect(summarizeWorkToolGroup([tool('done', 'completed'), tool('active', 'running')])).toEqual({
      label: '工具调用 · 1/2 执行中', isActive: true
    });
  });

  it('uses the same format for completed and mixed outcomes', () => {
    expect(summarizeWorkToolGroup([tool('one', 'completed'), tool('two', 'completed')]).label).toBe('工具调用 · 2 项已完成');
    expect(summarizeWorkToolGroup([
      tool('done', 'completed'), tool('denied', 'failed', 'permission_rejected'), tool('broken', 'failed')
    ])).toEqual({
      label: '工具调用 · 1 项已完成 · 1 项未执行 · 1 项失败', isActive: false
    });
  });
});
