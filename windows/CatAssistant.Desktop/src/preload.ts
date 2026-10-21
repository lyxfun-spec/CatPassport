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
  revealArtifact: (conversationId, artifactId) => ipcRenderer.invoke('work:reveal-artifact', conversationId, artifactId),
  paste: () => ipcRenderer.invoke('edit:paste')
};

contextBridge.exposeInMainWorld('catAssistant', api);

// The startup screen is inline markup, so it is on screen long before the bundled
// renderer runs; report it as soon as it exists instead of waiting for DOMContentLoaded.
const reportBootPainted = () => requestAnimationFrame(() => requestAnimationFrame(() => ipcRenderer.send('boot:ready')));
if (document.getElementById('boot')) reportBootPainted();
else new MutationObserver((_, observer) => {
  if (!document.getElementById('boot')) return;
  observer.disconnect();
  reportBootPainted();
}).observe(document, { childList: true, subtree: true });
