export interface CatDevice {
  address: number;
  addressText: string;
  name: string;
  rssi: number;
}

export interface PublicSettings {
  asrModel: string;
  chatModel: string;
  workModel: string;
  screenTimeoutSeconds: number;
  autoReconnect: boolean;
  webSearchEnabled: boolean;
  hasApiKey: boolean;
  hasSearchApiKey: boolean;
}

export interface ConversationSummary {
  id: string;
  mode: 'chat' | 'work';
  title: string;
  titleSource: 'fallback' | 'generated' | 'user';
  createdAt: string;
  updatedAt: string;
  isPinned: boolean;
  originConversationId?: string;
}

export interface ConversationMessage {
  id: string;
  conversationId: string;
  role: 'user' | 'assistant' | 'system';
  source: 'desktop' | 'device' | 'runtime';
  status: 'complete' | 'streaming' | 'interrupted' | 'failed';
  sequence: number;
  content: string;
  createdAt: string;
  completedAt?: string;
  runId?: string;
}

export interface Citation {
  id: string;
  messageId: string;
  ordinal: number;
  title: string;
  url: string;
  snippet?: string;
  sourceName?: string;
}

export interface ConversationDetail {
  conversation: ConversationSummary;
  messages: ConversationMessage[];
  citations: Citation[];
  activeRunId?: string;
  workRequests?: WorkRequest[];
}

export interface WorkRequest {
  id: string;
  sourceConversationId: string;
  sourceMessageId: string;
  sourceRunId: string;
  toolCallId: string;
  title: string;
  prompt: string;
  contextSummary: string;
  state: 'proposed' | 'approved' | 'rejected' | 'started' | 'failed';
  targetConversationId?: string;
  errorMessage?: string;
  createdAt: string;
  updatedAt: string;
}

export interface DeviceState {
  status: string;
  detail: string;
  isConnected: boolean;
  hasPairedDevice: boolean;
  pairedDeviceAddress?: number;
  pairedDeviceName?: string;
  maxPduSize: number;
  devices: CatDevice[];
  activeConversationId?: string;
  activeConversationTitle?: string;
  followDesktop: boolean;
}

export interface BootstrapState {
  conversations: ConversationSummary[];
  activeConversationId?: string;
  activeConversation?: ConversationDetail;
  device: DeviceState;
  settings: PublicSettings;
  pendingApprovals?: WorkApproval[];
  workToolCalls?: WorkToolCall[];
  workArtifacts?: WorkArtifact[];
}

export interface WorkApproval {
  id: string; conversationId: string; runId: string; toolCallId: string; operationJson: string;
  scope: 'single' | 'task_directory'; targetPath?: string; capability: 'read' | 'write' | 'execute';
  state: string; requestedAt: string; resolvedAt?: string;
}

export interface WorkToolCall {
  id: string; runId: string; roundId: string; batchId: string; toolCallId: string; ordinal: number;
  toolName: string; argumentsJson: string; state: string; resultJson?: string; errorCode?: string;
  errorMessage?: string; createdAt: string; startedAt?: string; completedAt?: string; durationMilliseconds?: number;
}

export interface WorkArtifact { id: string; conversationId: string; runId?: string; path: string; label: string; createdAt: string; }

export interface ConversationEvent {
  type: string;
  conversationId: string;
  messageId?: string;
  runId?: string;
  delta?: string;
  message?: ConversationMessage;
  conversation?: ConversationSummary;
  status?: string;
  error?: string;
  workRequest?: WorkRequest;
}

export type ConversationEventName =
  | 'conversation.created'
  | 'conversation.activated'
  | 'conversation.updated'
  | 'conversation.deleted'
  | 'device.binding'
  | 'message.created'
  | 'run.started'
  | 'message.delta'
  | 'message.completed'
  | 'message.interrupted'
  | 'run.completed'
  | 'run.stopped'
  | 'run.failed'
  | 'tool.created'
  | 'tool.started'
  | 'tool.completed'
  | 'tool.failed'
  | 'tool.cancelled'
  | 'approval.requested'
  | 'approval.resolved'
  | 'work.request.proposed'
  | 'work.request.resolved';

export type BackendEvent =
  | { event: 'backend.ready'; data: { version: number; state: BootstrapState } }
  | { event: 'backend.exited'; data: { code: number | null; message?: string } }
  | { event: 'device.changed'; data: DeviceState }
  | { event: ConversationEventName; data: ConversationEvent };

export interface BackendApi {
  invoke<T = unknown>(method: string, params?: Record<string, unknown>): Promise<T>;
  onEvent(listener: (event: BackendEvent) => void): () => void;
  selectWorkFiles(): Promise<string[]>;
  revealArtifact(conversationId: string, artifactId: string): Promise<void>;
  paste(): Promise<void>;
}

declare global {
  interface Window { catAssistant: BackendApi; }
}
