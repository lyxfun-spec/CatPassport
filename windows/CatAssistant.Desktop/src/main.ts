import { app, BrowserWindow, dialog, ipcMain, Menu, shell, Tray } from 'electron';
import http from 'node:http';
import path from 'node:path';
import { BackendClient } from './backend-client';

declare const MAIN_WINDOW_VITE_DEV_SERVER_URL: string | undefined;
declare const MAIN_WINDOW_VITE_NAME: string;

const allowedMethods = new Set([
  'app.bootstrap', 'state.get',
  'conversations.list', 'conversations.get', 'conversations.create', 'conversations.start', 'conversations.rename', 'conversations.pin',
  'conversations.delete', 'conversations.activate', 'conversations.send', 'conversations.cancel',
  'work.workspace.get', 'work.inputs.add', 'work.approvals.list', 'work.approvals.resolve', 'work.artifacts.revealPath',
  'work.requests.resolve',
  'devices.scan', 'devices.connect', 'devices.forget', 'devices.followDesktop',
  'conversation.new', 'conversation.cancel', 'conversation.send', 'settings.save'
]);
const backend = new BackendClient();
let mainWindow: BrowserWindow | null = null;
let tray: Tray | null = null;
let quitting = false;
let windowRevealed = false;
const brandingDir = app.isPackaged
  ? process.resourcesPath
  : path.resolve(__dirname, '..', '..', '..', '..', 'assets', 'branding');
const windowIconPath = path.join(brandingDir, 'cat-assistant-icon-256.png');
const trayIconPath = path.join(brandingDir, 'cat-assistant-icon-32.png');

if (!app.requestSingleInstanceLock()) app.quit();

// TEMP: startup milestones for diagnosing the white-window report; remove once settled.
const startupEpoch = Date.now();
const mark = (label: string) => console.log(`[startup] +${Date.now() - startupEpoch}ms ${label}`);
mark('main module loaded');

function showWindow(): void {
  if (!mainWindow || mainWindow.isDestroyed()) createWindow();
  // Showing before the startup screen has painted is what produced the blank white window.
  if (!windowRevealed) return;
  mainWindow!.show(); mainWindow!.restore(); mainWindow!.focus();
}

function waitForHttpOk(url: string): Promise<void> {
  return new Promise(resolve => {
    const attempt = () => {
      const request = http.get(url, response => {
        response.resume();
        if (response.statusCode === 200) resolve();
        else setTimeout(attempt, 300);
      });
      request.on('error', () => setTimeout(attempt, 300));
    };
    attempt();
  });
}

function createWindow(): void {
  mark('createWindow');
  windowRevealed = false;
  mainWindow = new BrowserWindow({
    width: 1080, height: 720, minWidth: 720, minHeight: 600, show: false,
    backgroundColor: '#ffffff', title: '小猫助手', icon: windowIconPath, autoHideMenuBar: true,
    // Blend the app into the window: hide the native title bar but keep the real
    // caption buttons (snap layouts, Aero Shake, accessibility all stay native).
    // The overlay is transparent so the buttons always sit on whatever the page
    // paints beneath them: the startup screen, the grey title-bar strip, or a
    // drawer sliding over it.
    titleBarStyle: 'hidden',
    titleBarOverlay: { color: '#00000000', symbolColor: '#5b6672', height: 40 },
    webPreferences: {
      preload: path.join(__dirname, 'preload.cjs'),
      contextIsolation: true, nodeIntegration: false, sandbox: true
    }
  });
  mainWindow.webContents.setWindowOpenHandler(({ url }) => {
    if (url.startsWith('https://') || url.startsWith('http://')) void shell.openExternal(url);
    return { action: 'deny' };
  });
  // ready-to-show can fire for the still-blank document and flash an empty white window
  // while the dev server is warming up, so wait for the preload to report that the inline
  // startup screen is on screen.
  let revealed = false;
  const reveal = () => {
    if (revealed || !mainWindow || mainWindow.isDestroyed()) return;
    revealed = true;
    windowRevealed = true;
    mark('window shown');
    mainWindow.show();
  };
  const onBootReady = () => { mark('boot:ready received'); reveal(); };
  ipcMain.on('boot:ready', onBootReady);
  mainWindow.once('closed', () => ipcMain.removeListener('boot:ready', onBootReady));
  mainWindow.webContents.on('dom-ready', () => mark('dom ready'));
  mainWindow.webContents.on('did-finish-load', () => mark('page finished load'));
  mainWindow.webContents.on('did-fail-load', (_event, code, description) => { mark(`page failed load ${code} ${description}`); reveal(); });
  if (MAIN_WINDOW_VITE_DEV_SERVER_URL) {
    // The dev server can hold the first index.html response for many seconds while it
    // pre-bundles dependencies, so paint the on-disk splash first and swap documents once
    // the server answers; the swap commits atomically, so no blank frame in between.
    const devServerUrl = MAIN_WINDOW_VITE_DEV_SERVER_URL;
    mark('splash load requested');
    void mainWindow.loadFile(path.join(__dirname, '..', '..', 'splash.html'));
    void waitForHttpOk(devServerUrl).then(() => {
      mark('dev server answered');
      if (mainWindow && !mainWindow.isDestroyed()) void mainWindow.loadURL(devServerUrl);
    });
  } else {
    mark('load requested');
    void mainWindow.loadFile(path.join(__dirname, `../renderer/${MAIN_WINDOW_VITE_NAME}/index.html`));
  }
  mainWindow.on('close', event => { if (!quitting) { event.preventDefault(); mainWindow?.hide(); } });
}

function createTray(): void {
  tray = new Tray(trayIconPath); tray.setToolTip('小猫助手');
  tray.setContextMenu(Menu.buildFromTemplate([
    { label: '打开小猫助手', click: showWindow },
    { type: 'separator' },
    { label: '退出', click: () => void quitApplication() }
  ]));
  tray.on('double-click', showWindow);
}

async function quitApplication(): Promise<void> {
  if (quitting) return;
  quitting = true;
  await backend.stop();
  tray?.destroy(); tray = null;
  app.quit();
}

app.on('second-instance', showWindow);
app.on('window-all-closed', () => { /* Keep Electron and the .NET service alive in the tray. */ });
app.on('before-quit', event => {
  if (quitting) return;
  event.preventDefault(); void quitApplication();
});

app.whenReady().then(() => {
  mark('app ready');
  Menu.setApplicationMenu(null);
  // Paint the startup screen before the backend spawns: `dotnet run` can take
  // several seconds while rebuilding CatAssistant.Backend.
  createWindow();
  backend.start();
  backend.on('event', event => BrowserWindow.getAllWindows().forEach(window => window.webContents.send('backend:event', event)));
  backend.on('error', error => BrowserWindow.getAllWindows().forEach(window => window.webContents.send('backend:event', { event: 'backend.exited', data: { code: null, message: error.message } })));
  ipcMain.handle('backend:invoke', (_event, method: string, params?: Record<string, unknown>) => {
    if (!allowedMethods.has(method)) throw new Error(`不允许的后台方法：${method}`);
    return backend.invoke(method, params);
  });
  ipcMain.handle('edit:paste', () => { mainWindow?.webContents.paste(); });
  ipcMain.handle('work:select-files', async () => {
    const result = await dialog.showOpenDialog(mainWindow!, { properties: ['openFile', 'multiSelections'] });
    return result.canceled ? [] : result.filePaths;
  });
  ipcMain.handle('work:reveal-artifact', async (_event, conversationId: string, artifactId: string) => {
    const result = await backend.invoke<{ path: string }>('work.artifacts.revealPath', { id: conversationId, artifactId });
    shell.showItemInFolder(result.path);
  });
  createTray();
});
