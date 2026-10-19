import { describe, expect, it } from 'vitest';
import { clampTextareaHeight, greetingForHour, isNearScrollEnd, scrollThumbMetrics, shouldSubmitComposer, workPhaseLabel } from './ui-behavior';

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

describe('Work phase status', () => {
  it('uses the latest public agent update from the active run', () => {
    expect(workPhaseLabel([
      { role: 'assistant', source: 'runtime', runId: 'old', content: '旧任务' },
      { role: 'assistant', source: 'runtime', runId: 'run-1', content: '**计划**\n\n- 收集资料' },
      { role: 'assistant', source: 'runtime', runId: 'run-1', content: '正在整理最终文件。' }
    ], 'run-1')).toBe('正在整理最终文件。');
  });

  it('does not expose another run as the current phase', () => {
    expect(workPhaseLabel([
      { role: 'assistant', source: 'runtime', runId: 'old', content: '旧任务状态' }
    ], 'run-1')).toBeUndefined();
  });
});
