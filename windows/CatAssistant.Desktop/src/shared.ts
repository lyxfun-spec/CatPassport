export interface CatDevice {
  address: number;
  addressText: string;
  name: string;
  rssi: number;
}

export interface ChatMessage {
  role: 'user' | 'assistant' | string;
  content: string;
  createdAt: string;
}

export interface PublicSettings {
  asrModel: string;
  chatModel: string;
  screenTimeoutSeconds: number;
  autoReconnect: boolean;
  hasApiKey: boolean;
}

export interface BackendSnapshot {
  status: string;
  detail: string;
  isConnected: boolean;
  hasPairedDevice: boolean;
  maxPduSize: number;
  devices: CatDevice[];
  settings: PublicSettings;
  conversationId?: number;
  messages: ChatMessage[];
}

export interface StreamUpdate {
  conversationId: number;
  phase: 'started' | 'delta' | 'completed';
  delta?: string;
  message?: ChatMessage;
}

export type BackendEvent =
  | { event: 'backend.ready'; data: { version: number; state: BackendSnapshot } }
  | { event: 'state.changed'; data: BackendSnapshot }
  | { event: 'conversation.stream'; data: StreamUpdate }
  | { event: 'backend.exited'; data: { code: number | null } };

export interface BackendApi {
  invoke<T = unknown>(method: string, params?: Record<string, unknown>): Promise<T>;
  onEvent(listener: (event: BackendEvent) => void): () => void;
}

declare global {
  interface Window { catAssistant: BackendApi; }
}
