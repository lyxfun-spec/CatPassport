import { contextBridge, ipcRenderer } from 'electron';
import type { BackendApi, BackendEvent } from './shared';

const languageSettings = ipcRenderer.sendSync('ui:language') as BackendApi['languageSettings'];

const api: BackendApi = {
  languageSettings,
  setLanguage: preference => ipcRenderer.invoke('ui:set-language', preference),
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

// main.ts tags the dev URL with the splash start time; seed the inline startup
// screen's progress offset before it paints so the bar survives the document swap.
const bootStartedAt = Number(new URLSearchParams(location.hash.slice(1)).get('boot'));
if (Number.isFinite(bootStartedAt) && bootStartedAt > 0) {
  const applyBootOffset = () => {
    if (!document.documentElement) return false;
    document.documentElement.style.setProperty('--boot-offset', `${Date.now() - bootStartedAt}ms`);
    return true;
  };
  if (!applyBootOffset()) {
    const observer = new MutationObserver(() => {
      if (applyBootOffset()) observer.disconnect();
    });
    observer.observe(document, { childList: true });
  }
}

// The startup screen is inline markup, so it is on screen long before the bundled
// renderer runs; report it as soon as it exists instead of waiting for DOMContentLoaded.
const reportBootPainted = () => requestAnimationFrame(() => requestAnimationFrame(() => ipcRenderer.send('boot:ready')));
function localizeBoot(): void {
  document.documentElement.lang = languageSettings.language;
  const english = languageSettings.language === 'en';
  document.title = english ? 'Cat Assistant' : '小猫助手';
  const title = document.querySelector('.boot-title');
  const status = document.getElementById('boot-text');
  if (title) title.textContent = document.title;
  if (status) status.textContent = english ? 'Starting up' : '正在启动';
}
if (document.getElementById('boot-text')) { localizeBoot(); reportBootPainted(); }
else new MutationObserver((_, observer) => {
  if (!document.getElementById('boot-text')) return;
  observer.disconnect();
  localizeBoot();
  reportBootPainted();
}).observe(document, { childList: true, subtree: true });
