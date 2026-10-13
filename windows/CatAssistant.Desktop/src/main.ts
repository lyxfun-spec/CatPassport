import { app, BrowserWindow, ipcMain, Menu, nativeImage, Tray } from 'electron';
import path from 'node:path';
import { BackendClient } from './backend-client';

declare const MAIN_WINDOW_VITE_DEV_SERVER_URL: string | undefined;
declare const MAIN_WINDOW_VITE_NAME: string;

const allowedMethods = new Set([
  'state.get', 'devices.scan', 'devices.connect', 'devices.forget',
  'conversation.new', 'conversation.cancel', 'conversation.send', 'settings.save'
]);
const backend = new BackendClient();
let mainWindow: BrowserWindow | null = null;
let tray: Tray | null = null;
let quitting = false;
const iconFileName = 'cat-assistant-icon-transparent.png';
const iconPath = app.isPackaged
  ? path.join(process.resourcesPath, iconFileName)
  : path.resolve(__dirname, '..', '..', '..', '..', 'assets', 'branding', iconFileName);

if (!app.requestSingleInstanceLock()) app.quit();

function showWindow(): void {
  if (!mainWindow || mainWindow.isDestroyed()) createWindow();
  mainWindow!.show(); mainWindow!.restore(); mainWindow!.focus();
}

function createWindow(): void {
  mainWindow = new BrowserWindow({
    width: 1080, height: 720, minWidth: 860, minHeight: 600, show: false,
    backgroundColor: '#f8f4ee', title: '小猫助手', icon: iconPath, autoHideMenuBar: true,
    webPreferences: {
      preload: path.join(__dirname, 'preload.cjs'),
      contextIsolation: true, nodeIntegration: false, sandbox: true
    }
  });
  if (MAIN_WINDOW_VITE_DEV_SERVER_URL) void mainWindow.loadURL(MAIN_WINDOW_VITE_DEV_SERVER_URL);
  else void mainWindow.loadFile(path.join(__dirname, `../renderer/${MAIN_WINDOW_VITE_NAME}/index.html`));
  mainWindow.once('ready-to-show', () => mainWindow?.show());
  mainWindow.on('close', event => { if (!quitting) { event.preventDefault(); mainWindow?.hide(); } });
}

function createTray(): void {
  const icon = nativeImage.createFromPath(iconPath);
  tray = new Tray(icon); tray.setToolTip('小猫助手');
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
  Menu.setApplicationMenu(null);
  backend.start();
  backend.on('event', event => BrowserWindow.getAllWindows().forEach(window => window.webContents.send('backend:event', event)));
  backend.on('error', error => BrowserWindow.getAllWindows().forEach(window => window.webContents.send('backend:event', { event: 'backend.exited', data: { code: null, message: error.message } })));
  ipcMain.handle('backend:invoke', (_event, method: string, params?: Record<string, unknown>) => {
    if (!allowedMethods.has(method)) throw new Error(`不允许的后台方法：${method}`);
    return backend.invoke(method, params);
  });
  createWindow(); createTray();
});
