export function shouldSubmitComposer(key: string, shiftKey: boolean, isComposing: boolean): boolean {
  return key === 'Enter' && !shiftKey && !isComposing;
}

export function isNearScrollEnd(
  scrollHeight: number,
  scrollTop: number,
  clientHeight: number,
  threshold = 96
): boolean {
  return scrollHeight - scrollTop - clientHeight <= threshold;
}

export function clampTextareaHeight(scrollHeight: number, minimum = 38, maximum = 152): number {
  return Math.max(minimum, Math.min(maximum, scrollHeight));
}

export interface ScrollThumbMetrics {
  visible: boolean;
  size: number;
  offset: number;
}

export function scrollThumbMetrics(
  scrollHeight: number,
  clientHeight: number,
  scrollTop: number,
  trackHeight: number,
  minimum = 24
): ScrollThumbMetrics {
  if (scrollHeight <= clientHeight || clientHeight <= 0 || trackHeight <= 0) {
    return { visible: false, size: 0, offset: 0 };
  }
  const size = Math.min(trackHeight, Math.max(minimum, trackHeight * clientHeight / scrollHeight));
  const travel = Math.max(0, trackHeight - size);
  const scrollRange = Math.max(1, scrollHeight - clientHeight);
  const offset = Math.max(0, Math.min(travel, travel * scrollTop / scrollRange));
  return { visible: true, size, offset };
}

export function greetingForHour(hour: number): string {
  if (hour < 11) return '早上好，想聊些什么？';
  if (hour < 18) return '下午好，想聊些什么？';
  return '晚上好，想聊些什么？';
}

export function workPhaseLabel(
  messages: Array<{ role: string; source: string; content: string; runId?: string }>,
  activeRunId?: string,
  maximumLength = 72
): string | undefined {
  if (!activeRunId) return undefined;
  const message = [...messages].reverse().find(item =>
    item.role === 'assistant'
    && item.source === 'runtime'
    && item.runId === activeRunId
    && item.content.trim().length > 0
  );
  if (!message) return undefined;
  const line = message.content.split(/\r?\n/).map(value => value.trim()).find(Boolean)
    ?.replace(/^[#>*\-\s]+/, '')
    .replace(/^\*\*(.+)\*\*:?$/, '$1')
    .trim();
  if (!line) return undefined;
  return line.length > maximumLength ? `${line.slice(0, maximumLength - 1)}…` : line;
}
