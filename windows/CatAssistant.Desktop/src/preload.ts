import { contextBridge, ipcRenderer } from 'electron';
import type { BackendApi, BackendEvent } from './shared';

const api: BackendApi = {
  invoke: (method, params) => ipcRenderer.invoke('backend:invoke', method, params),
  onEvent: listener => {
    const handler = (_event: Electron.IpcRendererEvent, value: BackendEvent) => listener(value);
    ipcRenderer.on('backend:event', handler);
    return () => ipcRenderer.removeListener('backend:event', handler);
  },
  selectWorkFiles: () => ipcRenderer.invoke('work:select-files'),
  revealArtifact: (conversationId, artifactId) => ipcRenderer.invoke('work:reveal-artifact', conversationId, artifactId)
};

contextBridge.exposeInMainWorld('catAssistant', api);
