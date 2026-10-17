export interface CatDevice {
  address: number;
  addressText: string;
  name: string;
  rssi: number;
}

export interface PublicSettings {
  asrModel: string;
  chatModel: string;
  screenTimeoutSeconds: number;
  autoReconnect: boolean;
  webSearchEnabled: boolean;
  hasApiKey: boolean;
}

export interface ConversationSummary {
  id: string;
  mode: 'chat' | 'work';
  title: string;
  titleSource: 'fallback' | 'generated' | 'user';
  createdAt: string;
  updatedAt: string;
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
}

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
  | 'run.failed';

export type BackendEvent =
  | { event: 'backend.ready'; data: { version: number; state: BootstrapState } }
  | { event: 'backend.exited'; data: { code: number | null; message?: string } }
  | { event: 'device.changed'; data: DeviceState }
  | { event: ConversationEventName; data: ConversationEvent };

export interface BackendApi {
  invoke<T = unknown>(method: string, params?: Record<string, unknown>): Promise<T>;
  onEvent(listener: (event: BackendEvent) => void): () => void;
}

declare global {
  interface Window { catAssistant: BackendApi; }
}
