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

function greetingPrefixForHour(hour: number): string {
  if (hour < 11) return '早上好';
  if (hour < 18) return '下午好';
  return '晚上好';
}

export function greetingForHour(hour: number): string {
  return `${greetingPrefixForHour(hour)}，想聊些什么？`;
}

export function draftGreetingForHour(mode: 'chat' | 'work', hour: number): string {
  return `${greetingPrefixForHour(hour)}，${mode === 'work' ? '今天想完成什么？' : '想聊些什么？'}`;
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
      if (previous?.kind === 'tools' && previous.tools[0]?.runId === item.tool.runId) previous.tools.push(item.tool);
      else timeline.push({ kind: 'tools', key: `tools-${item.tool.id}`, createdAt: item.createdAt, tools: [item.tool] });
      continue;
    }
    if (item.kind === 'artifact') {
      if (previous?.kind === 'artifacts' && previous.artifacts[0]?.runId === item.artifact.runId) previous.artifacts.push(item.artifact);
      else timeline.push({ kind: 'artifacts', key: `artifacts-${item.artifact.id}`, createdAt: item.createdAt, artifacts: [item.artifact] });
      continue;
    }
    timeline.push({ kind: 'message', key: item.key, createdAt: item.createdAt, message: item.message });
  }
  return timeline;
}

export function assistantCopyTargets(
  messages: ConversationMessage[],
  activeRunId?: string
): Map<string, string> {
  const runs = new Map<string, ConversationMessage[]>();
  const standalone: ConversationMessage[] = [];
  for (const message of messages) {
    if (message.role !== 'assistant' || !message.content.trim()) continue;
    if (!message.runId) { if (message.status !== 'streaming') standalone.push(message); continue; }
    const grouped = runs.get(message.runId) ?? [];
    grouped.push(message);
    runs.set(message.runId, grouped);
  }

  const targets = new Map<string, string>();
  for (const message of standalone) targets.set(message.id, message.content);
  for (const [runId, output] of runs) {
    if (runId === activeRunId) continue;
    const last = output.at(-1);
    if (last) targets.set(last.id, output.map(message => message.content.trim()).filter(Boolean).join('\n\n'));
  }
  return targets;
}

export function workTimelineCopyActions(
  timeline: WorkTimelineItem[],
  messages: ConversationMessage[],
  activeRunId?: string
): Map<string, string> {
  const copyTargets = assistantCopyTargets(messages, activeRunId);
  const lastItemByRun = new Map<string, string>();
  for (const item of timeline) {
    if (item.kind === 'message' && item.message.runId) lastItemByRun.set(item.message.runId, item.key);
    if (item.kind === 'tools') for (const tool of item.tools) lastItemByRun.set(tool.runId, item.key);
    if (item.kind === 'artifacts') for (const artifact of item.artifacts) {
      if (artifact.runId) lastItemByRun.set(artifact.runId, item.key);
    }
  }

  const actions = new Map<string, string>();
  for (const message of messages) {
    const content = copyTargets.get(message.id);
    if (!content) continue;
    const targetKey = message.runId ? lastItemByRun.get(message.runId) : message.id;
    if (targetKey) actions.set(targetKey, content);
  }
  return actions;
}

export interface WorkToolGroupSummary {
  label: string;
  isActive: boolean;
}

export function summarizeWorkToolGroup(tools: WorkToolCall[]): WorkToolGroupSummary {
  const active = tools.filter(tool => ['pending', 'waiting_approval', 'running'].includes(tool.state)).length;
  const completed = tools.filter(tool => tool.state === 'completed').length;
  const notExecuted = tools.filter(tool =>
    ['rejected', 'cancelled'].includes(tool.state) || tool.errorCode === 'permission_rejected'
  ).length;
  const failed = Math.max(0, tools.length - active - completed - notExecuted);
  if (active > 0) return { label: `工具调用 · ${completed}/${tools.length} 执行中`, isActive: true };

  const parts = [
    completed > 0 ? `${completed} 项已完成` : '',
    notExecuted > 0 ? `${notExecuted} 项未执行` : '',
    failed > 0 ? `${failed} 项失败` : ''
  ].filter(Boolean);
  return {
    label: `工具调用 · ${parts.join(' · ') || '已结束'}`,
    isActive: false
  };
}
