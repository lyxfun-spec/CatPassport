import type { ConversationMessage, WorkArtifact, WorkToolCall } from './shared';

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

export type WorkTimelineItem =
  | { kind: 'message'; key: string; createdAt: string; message: ConversationMessage }
  | { kind: 'tools'; key: string; createdAt: string; tools: WorkToolCall[] }
  | { kind: 'artifacts'; key: string; createdAt: string; artifacts: WorkArtifact[] };

type RawWorkTimelineItem =
  | { kind: 'message'; key: string; createdAt: string; order: number; message: ConversationMessage }
  | { kind: 'tool'; key: string; createdAt: string; order: number; tool: WorkToolCall }
  | { kind: 'artifact'; key: string; createdAt: string; order: number; artifact: WorkArtifact };

export function buildWorkTimeline(
  messages: ConversationMessage[],
  tools: WorkToolCall[],
  artifacts: WorkArtifact[]
): WorkTimelineItem[] {
  const raw: RawWorkTimelineItem[] = [
    ...messages.map((message, order): RawWorkTimelineItem => ({ kind: 'message', key: message.id, createdAt: message.createdAt, order, message })),
    ...tools.map((tool, order): RawWorkTimelineItem => ({ kind: 'tool', key: tool.id, createdAt: tool.createdAt, order: messages.length + order, tool })),
    ...artifacts.map((artifact, order): RawWorkTimelineItem => ({ kind: 'artifact', key: artifact.id, createdAt: artifact.createdAt, order: messages.length + tools.length + order, artifact }))
  ].sort((left, right) => {
    const time = Date.parse(left.createdAt) - Date.parse(right.createdAt);
    return Number.isNaN(time) || time === 0 ? left.order - right.order : time;
  });

  const timeline: WorkTimelineItem[] = [];
  for (const item of raw) {
    const previous = timeline.at(-1);
    if (item.kind === 'tool') {
      if (previous?.kind === 'tools') previous.tools.push(item.tool);
      else timeline.push({ kind: 'tools', key: `tools-${item.tool.id}`, createdAt: item.createdAt, tools: [item.tool] });
      continue;
    }
    if (item.kind === 'artifact') {
      if (previous?.kind === 'artifacts') previous.artifacts.push(item.artifact);
      else timeline.push({ kind: 'artifacts', key: `artifacts-${item.artifact.id}`, createdAt: item.createdAt, artifacts: [item.artifact] });
      continue;
    }
    timeline.push({ kind: 'message', key: item.key, createdAt: item.createdAt, message: item.message });
  }
  return timeline;
}
