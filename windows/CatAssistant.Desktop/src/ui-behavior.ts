import { t } from './i18n';
import type { ConversationMessage, WorkArtifact, WorkRequest, WorkToolCall } from './shared';

export function toolDisplayName(name: string): string {
  const names = new Map<string, string>([
    ['execute_command', t('运行命令')],
    ['read_file', t('读取文件')],
    ['write_file', t('写入文件')],
    ['list_directory', t('查看文件夹')],
    ['web_search', t('搜索网络')],
    ['deliver_files', t('交付成果')],
    ['list_skills', t('查看技能列表')],
    ['load_skill', t('读取技能')],
    ['search_conversation_history', t('搜索对话历史')],
    ['request_work', t('请求 Work 任务')]
  ]);
  return names.get(name) ?? name;
}

export function shouldSubmitComposer(key: string, shiftKey: boolean, isComposing: boolean): boolean {
  return key === 'Enter' && !shiftKey && !isComposing;
}

export function isAtScrollEnd(
  scrollHeight: number,
  scrollTop: number,
  clientHeight: number,
  tolerance = 2
): boolean {
  return scrollHeight - scrollTop - clientHeight <= tolerance;
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
  if (hour < 11) return t("早上好");
  if (hour < 18) return t("下午好");
  return t("晚上好");
}

export function greetingForHour(hour: number): string {
  return t("{0}，想聊些什么？", greetingPrefixForHour(hour));
}

export function draftGreetingForHour(mode: 'chat' | 'work', hour: number): string {
  return t("{0}，{1}", greetingPrefixForHour(hour), mode === 'work' ? t("今天想完成什么？") : t("想聊些什么？"));
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
  if (active > 0) return { label: t("工具调用 · {0}/{1} 执行中", completed, tools.length), isActive: true };

  const parts = [
    completed > 0 ? t("{0} 项已完成", completed) : '',
    notExecuted > 0 ? t("{0} 项未执行", notExecuted) : '',
    failed > 0 ? t("{0} 项失败", failed) : ''
  ].filter(Boolean);
  return {
    label: t("工具调用 · {0}", parts.join(' · ') || t("已结束")),
    isActive: false
  };
}

/** Splits a reply into segments at the points where an inline card must be rendered. Always returns offsets.length + 1 pieces. */
export function splitAtAnchors(content: string, offsets: number[]): string[] {
  const cuts = offsets.map(offset => Math.max(0, Math.min(offset, content.length))).sort((left, right) => left - right);
  const segments: string[] = [];
  let start = 0;
  for (const cut of cuts) {
    segments.push(content.slice(start, cut));
    start = cut;
  }
  segments.push(content.slice(start));
  return segments;
}

export interface AnchoredWorkRequest { request: WorkRequest; offset: number }

export interface WorkRequestGroups {
  anchored: Map<string, AnchoredWorkRequest[]>;
  trailing: Map<string, WorkRequest[]>;
}

/** Binds each proposal to the assistant message of its run so it renders where the model actually called the tool. */
export function groupWorkRequests(messages: ConversationMessage[], requests: WorkRequest[], conversationId: string): WorkRequestGroups {
  const anchored = new Map<string, AnchoredWorkRequest[]>();
  const trailing = new Map<string, WorkRequest[]>();
  const assistantByRun = new Map<string, ConversationMessage>();
  for (const message of messages)
    if (message.role === 'assistant' && message.runId) assistantByRun.set(message.runId, message);
  for (const request of requests) {
    if (request.sourceConversationId !== conversationId) continue;
    const assistant = assistantByRun.get(request.sourceRunId);
    if (assistant) {
      // Rows created before offsets were recorded go after the reply text of their run.
      const offset = request.anchorOffset ?? Number.MAX_SAFE_INTEGER;
      anchored.set(assistant.id, [...(anchored.get(assistant.id) ?? []), { request, offset }]);
    } else {
      trailing.set(request.sourceMessageId, [...(trailing.get(request.sourceMessageId) ?? []), request]);
    }
  }
  for (const group of anchored.values()) group.sort((left, right) => left.offset - right.offset);
  return { anchored, trailing };
}
