import React, { useEffect, useMemo, useReducer, useRef, useState } from 'react';
import { createRoot } from 'react-dom/client';
import ReactMarkdown from 'react-markdown';
import type {
  BackendEvent,
  BootstrapState,
  CatDevice,
  ConversationDetail,
  ConversationMessage,
  ConversationSummary,
  PublicSettings
} from './shared';
import { deviceDiscoveryMessage, initialState, reduceBackendEvent } from './state';
import './styles.css';

type IconName = 'new' | 'search' | 'chat' | 'work' | 'device' | 'settings' | 'menu' | 'send' | 'copy' | 'close' | 'sidebar' | 'check' | 'back';

const iconPaths: Record<IconName, React.ReactNode> = {
  new: <><path d="M12 5v14M5 12h14"/></>,
  search: <><circle cx="10.5" cy="10.5" r="6.5"/><path d="m16 16 4 4"/></>,
  chat: <><path d="M5 18.2 3.8 21l3.7-1.2A9 9 0 1 0 5 18.2Z"/><path d="M8 11.5h8M8 15h5"/></>,
  work: <><rect x="4" y="7" width="16" height="12" rx="2"/><path d="M9 7V5h6v2M4 12h16M10 12v2h4v-2"/></>,
  device: <><rect x="6" y="3" width="12" height="18" rx="3"/><path d="M10 7h4M10 17h4"/></>,
  settings: <><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.9l.1.1-2.8 2.8-.1-.1a1.7 1.7 0 0 0-1.9-.3 1.7 1.7 0 0 0-1 1.6V21h-4v-.1a1.7 1.7 0 0 0-1-1.6 1.7 1.7 0 0 0-1.9.3l-.1.1L4.2 17l.1-.1a1.7 1.7 0 0 0 .3-1.9A1.7 1.7 0 0 0 3 14v-4a1.7 1.7 0 0 0 1.6-1 1.7 1.7 0 0 0-.3-1.9L4.2 7 7 4.2l.1.1A1.7 1.7 0 0 0 9 4.6 1.7 1.7 0 0 0 10 3h4a1.7 1.7 0 0 0 1 1.6 1.7 1.7 0 0 0 1.9-.3l.1-.1L19.8 7l-.1.1a1.7 1.7 0 0 0-.3 1.9 1.7 1.7 0 0 0 1.6 1v4a1.7 1.7 0 0 0-1.6 1Z"/></>,
  menu: <><circle cx="5" cy="12" r="1"/><circle cx="12" cy="12" r="1"/><circle cx="19" cy="12" r="1"/></>,
  send: <><path d="m4 12 16-8-6.5 16-2-6Z"/><path d="m11.5 14 3-3"/></>,
  copy: <><rect x="8" y="8" width="11" height="11" rx="2"/><path d="M16 8V6a2 2 0 0 0-2-2H6a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h2"/></>,
  close: <><path d="m6 6 12 12M18 6 6 18"/></>,
  sidebar: <><rect x="3" y="4" width="18" height="16" rx="2"/><path d="M9 4v16"/></>,
  check: <path d="m5 12 4 4L19 6"/>,
  back: <><path d="m15 18-6-6 6-6"/></>
};

function Icon({ name, size = 19 }: { name: IconName; size?: number }) {
  return <svg className="icon" width={size} height={size} viewBox="0 0 24 24" aria-hidden="true">{iconPaths[name]}</svg>;
}

function invoke<T>(method: string, params?: Record<string, unknown>) {
  return window.catAssistant.invoke<T>(method, params);
}

function App() {
  const [state, dispatch] = useReducer(reduceBackendEvent, initialState);
  const [sidebarCollapsed, setSidebarCollapsed] = useState(false);
  const [newOpen, setNewOpen] = useState(false);
  const [deviceOpen, setDeviceOpen] = useState(false);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [query, setQuery] = useState('');
  const [draft, setDraft] = useState('');
  const [busy, setBusy] = useState<string>();
  const [localError, setLocalError] = useState<string>();
  const onboardingShown = useRef(false);

  const refresh = async () => dispatch({ event: 'ui.bootstrap', data: await invoke<BootstrapState>('app.bootstrap') });

  useEffect(() => {
    let refreshTimer: ReturnType<typeof setTimeout> | undefined;
    const unsubscribe = window.catAssistant.onEvent((event: BackendEvent) => {
      dispatch(event);
      if (event.event === 'message.delta' || event.event === 'device.changed' || event.event === 'backend.ready') return;
      if (event.event === 'backend.exited') return;
      clearTimeout(refreshTimer);
      refreshTimer = setTimeout(() => void refresh().catch(reason => setLocalError(errorText(reason))), 30);
    });
    invoke<BootstrapState>('app.bootstrap').then(data => dispatch({ event: 'ui.bootstrap', data })).catch(reason => setLocalError(errorText(reason)));
    return () => { clearTimeout(refreshTimer); unsubscribe(); };
  }, []);

  useEffect(() => {
    if (!onboardingShown.current && state.bootstrap && !state.bootstrap.settings.hasApiKey) {
      onboardingShown.current = true;
      setSettingsOpen(true);
    }
  }, [state.bootstrap]);

  const run = async <T,>(name: string, action: () => Promise<T>): Promise<T | undefined> => {
    setBusy(name); setLocalError(undefined);
    try { return await action(); }
    catch (reason) { setLocalError(errorText(reason)); return undefined; }
    finally { setBusy(undefined); }
  };

  if (!state.bootstrap) return <Loading error={localError ?? state.backendError}/>;
  const bootstrap = state.bootstrap;
  const active = bootstrap.activeConversation;

  const selectConversation = async (id: string) => {
    const detail = await run('activate', () => invoke<ConversationDetail>('conversations.activate', { id }));
    if (detail) dispatch({ event: 'ui.conversation', data: detail });
    void refresh().catch(reason => setLocalError(errorText(reason)));
  };

  const createConversation = async () => {
    const created = await run('create', () => invoke<ConversationSummary>('conversations.create', { mode: 'chat' }));
    if (!created) return;
    setNewOpen(false);
    await refresh();
  };

  const send = async () => {
    const text = draft.trim();
    if (!text || !active) return;
    setDraft('');
    const detail = await run('send', () => invoke<ConversationDetail>('conversations.send', { id: active.conversation.id, text }));
    if (detail) dispatch({ event: 'ui.conversation', data: detail });
  };

  const rename = async () => {
    if (!active) return;
    const title = window.prompt('重命名对话', active.conversation.title)?.trim();
    if (!title || title === active.conversation.title) return;
    await run('rename', () => invoke('conversations.rename', { id: active.conversation.id, title }));
    await refresh();
  };

  const remove = async () => {
    if (!active || !window.confirm(`删除“${active.conversation.title}”？此操作无法撤销。`)) return;
    await run('delete', () => invoke('conversations.delete', { id: active.conversation.id }));
    await refresh();
  };

  return <div className={`app-shell ${sidebarCollapsed ? 'sidebar-collapsed' : ''}`}>
    <Sidebar
      bootstrap={bootstrap}
      query={query}
      onQuery={setQuery}
      collapsed={sidebarCollapsed}
      onToggle={() => setSidebarCollapsed(value => !value)}
      onNew={() => setNewOpen(true)}
      onSelect={id => void selectConversation(id)}
      onDevice={() => setDeviceOpen(true)}
      onSettings={() => setSettingsOpen(true)}
    />
    <main className="conversation-pane">
      {(localError || state.backendError) && <div className="global-alert"><span>{localError ?? state.backendError}</span><button onClick={() => { setLocalError(undefined); dispatch({ event: 'ui.clearError' }); }}><Icon name="close"/></button></div>}
      {active ? <>
        <ConversationHeader conversation={active.conversation} onRename={() => void rename()} onDelete={() => void remove()}/>
        <MessageList detail={active} runError={state.runError}/>
        <Composer value={draft} onChange={setDraft} onSend={() => void send()} disabled={!bootstrap.settings.hasApiKey || !draft.trim()} sending={busy === 'send'} onOpenSettings={() => setSettingsOpen(true)}/>
      </> : <EmptyConversation onNew={() => setNewOpen(true)}/>}
    </main>
    {newOpen && <NewConversationDialog
      busy={busy === 'create'}
      onClose={() => setNewOpen(false)}
      onCreate={() => void createConversation()}
    />}
    {deviceOpen && <DevicePanel
      bootstrap={bootstrap}
      busy={busy}
      run={run}
      onClose={() => setDeviceOpen(false)}
      onOpenConversation={id => { setDeviceOpen(false); void selectConversation(id); }}
      onRefresh={() => void refresh()}
    />}
    {settingsOpen && <SettingsDialog
      settings={bootstrap.settings}
      busy={busy === 'settings'}
      run={run}
      onClose={() => setSettingsOpen(false)}
      onSaved={() => void refresh()}
    />}
  </div>;
}

function Sidebar({ bootstrap, query, onQuery, collapsed, onToggle, onNew, onSelect, onDevice, onSettings }: {
  bootstrap: BootstrapState;
  query: string;
  onQuery(value: string): void;
  collapsed: boolean;
  onToggle(): void;
  onNew(): void;
  onSelect(id: string): void;
  onDevice(): void;
  onSettings(): void;
}) {
  const filtered = useMemo(() => bootstrap.conversations.filter(item => item.title.toLocaleLowerCase().includes(query.toLocaleLowerCase())), [bootstrap.conversations, query]);
  const groups = groupConversations(filtered);
  const activeDeviceConversation = bootstrap.conversations.find(item => item.id === bootstrap.device.activeConversationId);
  return <aside className="sidebar">
    <div className="brand-row">
      <div className="brand-mark"><span>CA</span></div>
      <div className="brand-copy"><strong>Cat Assistant</strong><small>Desktop AI</small></div>
      <button className="icon-button sidebar-toggle" onClick={onToggle} title={collapsed ? '展开侧边栏' : '收起侧边栏'}><Icon name="sidebar"/></button>
    </div>
    <button className="new-conversation" onClick={onNew}><Icon name="new"/><span>新对话</span></button>
    <label className="search-box"><Icon name="search"/><input value={query} onChange={event => onQuery(event.target.value)} placeholder="搜索对话" aria-label="搜索对话"/></label>
    <div className="conversation-list">
      {groups.map(group => <section className="conversation-group" key={group.label}>
        <h2>{group.label}</h2>
        {group.items.map(item => <button key={item.id} className={`conversation-item ${bootstrap.activeConversationId === item.id ? 'active' : ''}`} onClick={() => onSelect(item.id)} title={item.title}>
          <Icon name={item.mode === 'work' ? 'work' : 'chat'} size={17}/><span>{item.title}</span>
        </button>)}
      </section>)}
      {filtered.length === 0 && <div className="no-results">没有匹配的对话</div>}
    </div>
    <div className="sidebar-footer">
      <button className="footer-entry device-entry" onClick={onDevice}>
        <span className={`status-dot ${bootstrap.device.isConnected ? 'online' : ''}`}/><Icon name="device"/><span className="footer-copy"><b>{bootstrap.device.isConnected ? 'AI 卡片' : '连接 AI 卡片'}</b><small>{bootstrap.device.isConnected ? activeDeviceConversation?.title ?? '已连接' : bootstrap.device.hasPairedDevice ? '正在等待设备' : '可选辅助硬件'}</small></span>
      </button>
      <button className="footer-entry" onClick={onSettings}><Icon name="settings"/><span className="footer-copy"><b>设置</b><small>{bootstrap.settings.hasApiKey ? 'AI 服务已配置' : '需要配置 API Key'}</small></span></button>
    </div>
  </aside>;
}

function ConversationHeader({ conversation, onRename, onDelete }: { conversation: ConversationSummary; onRename(): void; onDelete(): void }) {
  const [menuOpen, setMenuOpen] = useState(false);
  return <header className="conversation-header">
    <div className="title-block"><h1>{conversation.title}</h1><span className="mode-chip"><Icon name={conversation.mode === 'work' ? 'work' : 'chat'} size={14}/>{conversation.mode === 'work' ? 'Work' : 'Chat'}</span></div>
    <div className="header-menu"><button className="icon-button" onClick={() => setMenuOpen(value => !value)} title="对话选项"><Icon name="menu"/></button>{menuOpen && <div className="popover-menu"><button onClick={() => { setMenuOpen(false); onRename(); }}>重命名</button><button className="danger" onClick={() => { setMenuOpen(false); onDelete(); }}>删除对话</button></div>}</div>
  </header>;
}

function MessageList({ detail, runError }: { detail: ConversationDetail; runError?: string }) {
  const scroller = useRef<HTMLDivElement>(null);
  useEffect(() => { scroller.current?.scrollTo({ top: scroller.current.scrollHeight, behavior: 'smooth' }); }, [detail.messages.length, detail.messages.at(-1)?.content]);
  if (detail.messages.length === 0) return <div className="messages empty-chat" ref={scroller}><div className="welcome-mark">CA</div><h2>有什么想一起聊聊的吗？</h2><p>可以直接提问，需要最新信息时我会使用网络搜索。</p></div>;
  return <div className="messages" ref={scroller}>
    <div className="message-column">
      {detail.messages.map(message => <MessageItem key={message.id} message={message} citations={detail.citations.filter(item => item.messageId === message.id)}/>) }
      {runError && <div className="run-error">{runError}</div>}
    </div>
  </div>;
}

function MessageItem({ message, citations }: { message: ConversationMessage; citations: ConversationDetail['citations'] }) {
  const [copied, setCopied] = useState(false);
  if (message.role === 'assistant' && message.status === 'streaming' && !message.content) return <article className="message assistant"><div className="thinking"><i/><i/><i/></div></article>;
  return <article className={`message ${message.role} ${message.status}`}>
    <div className="message-body">{message.role === 'assistant'
      ? <ReactMarkdown components={{ a: props => <a {...props} target="_blank" rel="noreferrer"/> }}>{message.content}</ReactMarkdown>
      : <p>{message.content}</p>}</div>
    {citations.length > 0 && <div className="sources"><span>来源</span>{citations.map(citation => <a key={citation.id} href={citation.url} target="_blank" rel="noreferrer"><b>{citation.ordinal}</b><span>{citation.title}</span></a>)}</div>}
    {message.role === 'assistant' && message.content && <div className="message-actions">
      {message.status === 'interrupted' && <span className="interrupted-label">已中断</span>}
      {message.status === 'failed' && <span className="failed-label">生成失败</span>}
      <button onClick={() => { void navigator.clipboard.writeText(message.content).then(() => { setCopied(true); setTimeout(() => setCopied(false), 1200); }); }}><Icon name={copied ? 'check' : 'copy'} size={15}/>{copied ? '已复制' : '复制'}</button>
    </div>}
  </article>;
}

function Composer({ value, onChange, onSend, disabled, sending, onOpenSettings }: { value: string; onChange(value: string): void; onSend(): void; disabled: boolean; sending: boolean; onOpenSettings(): void }) {
  return <div className="composer-wrap">
    <div className="composer">
      <textarea value={value} onChange={event => onChange(event.target.value)} onKeyDown={event => { if (event.key === 'Enter' && !event.shiftKey) { event.preventDefault(); if (!disabled) onSend(); } }} placeholder="输入消息" rows={1}/>
      <button className="send-button" disabled={disabled || sending} onClick={onSend} title="发送"><Icon name="send"/></button>
    </div>
    {disabled && !value.trim() ? <p>AI 生成的内容可能不准确，请核实重要信息。</p> : null}
    {disabled && value.trim() ? <button className="key-hint" onClick={onOpenSettings}>配置 API Key 后即可发送</button> : null}
  </div>;
}

function EmptyConversation({ onNew }: { onNew(): void }) {
  return <div className="standalone-empty"><h1>开始一段新对话</h1><button onClick={onNew}><Icon name="new"/>新建 Chat</button></div>;
}

function NewConversationDialog({ busy, onClose, onCreate }: { busy: boolean; onClose(): void; onCreate(): void }) {
  return <Modal onClose={onClose} width="500px"><div className="dialog-heading"><div><h2>开始新对话</h2><p>选择这次对话使用的模式</p></div><button className="icon-button" onClick={onClose}><Icon name="close"/></button></div>
    <div className="mode-selector"><button className="selected"><Icon name="chat"/><span><b>Chat</b><small>日常交流与联网搜索</small></span><Icon name="check"/></button><button disabled><Icon name="work"/><span><b>Work</b><small>完成本地工作任务</small></span><em>即将推出</em></button></div>
    <button className="primary wide" disabled={busy} onClick={onCreate}>{busy ? '正在创建…' : '开始 Chat'}</button>
  </Modal>;
}

function DevicePanel({ bootstrap, busy, run, onClose, onOpenConversation, onRefresh }: {
  bootstrap: BootstrapState;
  busy?: string;
  run<T>(name: string, action: () => Promise<T>): Promise<T | undefined>;
  onClose(): void;
  onOpenConversation(id: string): void;
  onRefresh(): void;
}) {
  const device = bootstrap.device;
  const active = bootstrap.conversations.find(item => item.id === device.activeConversationId);
  const connect = async (item: CatDevice) => { await run('device', () => invoke('devices.connect', { address: item.address, name: item.name })); onRefresh(); };
  const follow = async () => { await run('device', () => invoke('devices.followDesktop')); onRefresh(); };
  const forget = async () => { if (!window.confirm('忘记当前 AI 卡片并重新扫描？')) return; await run('device', () => invoke('devices.forget')); onRefresh(); };
  return <div className="drawer-backdrop" onMouseDown={event => { if (event.target === event.currentTarget) onClose(); }}><aside className="device-panel">
    <div className="panel-header"><button className="icon-button" onClick={onClose}><Icon name="back"/></button><div><h2>AI 卡片</h2><p>用于语音输入和随身查看回答</p></div></div>
    <section className="device-hero"><div className={`device-visual ${device.isConnected ? 'online' : ''}`}><Icon name="device" size={28}/><span/></div><div><h3>{device.isConnected ? device.pairedDeviceName ?? 'AI 卡片' : device.hasPairedDevice ? '正在等待已绑定卡片' : '尚未连接卡片'}</h3><p>{device.isConnected ? '已连接，可以开始对话' : device.detail}</p></div></section>
    {device.isConnected && <section className="panel-section"><label>卡片当前对话</label><div className="bound-conversation"><Icon name="chat"/><span><b>{active?.title ?? 'Chat 对话'}</b><small>{device.followDesktop ? '正在跟随电脑' : '卡片独立对话'}</small></span></div><div className="panel-actions">{active && <button onClick={() => onOpenConversation(active.id)}>打开对话</button>}<button className="primary" disabled={device.followDesktop || bootstrap.activeConversation?.conversation.mode !== 'chat' || busy === 'device'} onClick={() => void follow()}>跟随当前对话</button></div></section>}
    {!device.isConnected && <section className="panel-section"><div className="section-title"><label>附近的设备</label><button onClick={() => void run('device', () => invoke('devices.scan')).then(onRefresh)}>重新扫描</button></div><div className="device-list">{device.devices.length ? device.devices.map(item => <button key={item.address} disabled={busy === 'device'} onClick={() => void connect(item)}><span><b>{item.name}</b><small>{item.addressText}</small></span><em>{item.rssi} dBm</em></button>) : <div className="device-empty">{deviceDiscoveryMessage(device)}</div>}</div></section>}
    <details className="diagnostics"><summary>高级诊断</summary><dl><div><dt>状态</dt><dd>{device.status}</dd></div><div><dt>详情</dt><dd>{device.detail}</dd></div><div><dt>PDU</dt><dd>{device.maxPduSize || '—'}</dd></div></dl></details>
    {device.hasPairedDevice && <button className="text-danger" onClick={() => void forget()}>忘记这张卡片</button>}
  </aside></div>;
}

function SettingsDialog({ settings, busy, run, onClose, onSaved }: { settings: PublicSettings; busy: boolean; run<T>(name: string, action: () => Promise<T>): Promise<T | undefined>; onClose(): void; onSaved(): void }) {
  const [apiKey, setApiKey] = useState('');
  const [chatModel, setChatModel] = useState(settings.chatModel);
  const [asrModel, setAsrModel] = useState(settings.asrModel);
  const [screenTimeout, setScreenTimeout] = useState(settings.screenTimeoutSeconds);
  const [autoReconnect, setAutoReconnect] = useState(settings.autoReconnect);
  const [webSearch, setWebSearch] = useState(settings.webSearchEnabled);
  const save = async () => {
    const result = await run('settings', () => invoke('settings.save', { apiKey: apiKey || undefined, chatModel, asrModel, screenTimeoutSeconds: screenTimeout, autoReconnect, webSearchEnabled: webSearch }));
    if (result !== undefined) { onSaved(); onClose(); }
  };
  return <Modal onClose={onClose} width="680px"><div className="dialog-heading"><div><h2>设置</h2><p>AI 服务、联网搜索与卡片偏好</p></div><button className="icon-button" onClick={onClose}><Icon name="close"/></button></div>
    <div className="settings-grid">
      <section><h3>AI 服务</h3><label><span>DashScope API Key <em>{settings.hasApiKey ? '已安全配置' : '尚未配置'}</em></span><input type="password" value={apiKey} onChange={event => setApiKey(event.target.value)} placeholder={settings.hasApiKey ? '留空表示不更改' : '输入 API Key'} autoComplete="off"/></label><label><span>Chat 模型</span><input value={chatModel} onChange={event => setChatModel(event.target.value)}/></label><label className="toggle-row"><span><b>联网搜索</b><small>需要最新信息时允许 AI 自动搜索</small></span><input type="checkbox" checked={webSearch} onChange={event => setWebSearch(event.target.checked)}/></label></section>
      <section><h3>AI 卡片</h3><label><span>语音识别模型</span><input value={asrModel} onChange={event => setAsrModel(event.target.value)}/></label><label><span>屏幕休眠时间（秒）</span><input type="number" min="0" max="86400" value={screenTimeout} onChange={event => setScreenTimeout(Math.max(0, Math.min(86400, Number(event.target.value))))}/></label><label className="toggle-row"><span><b>自动重连</b><small>启动后连接已绑定的卡片</small></span><input type="checkbox" checked={autoReconnect} onChange={event => setAutoReconnect(event.target.checked)}/></label></section>
    </div>
    <div className="dialog-footer"><span>API Key 使用 Windows DPAPI 加密保存在本机。</span><button className="primary" disabled={busy} onClick={() => void save()}>{busy ? '保存中…' : '保存设置'}</button></div>
  </Modal>;
}

function Modal({ children, onClose, width }: { children: React.ReactNode; onClose(): void; width: string }) {
  return <div className="modal-backdrop" onMouseDown={event => { if (event.target === event.currentTarget) onClose(); }}><section className="modal" style={{ width }}>{children}</section></div>;
}

function Loading({ error }: { error?: string }) {
  return <div className="loading-screen"><div className="brand-mark large"><span>CA</span></div><h1>Cat Assistant</h1><p>{error ?? '正在准备桌面 AI…'}</p></div>;
}

function groupConversations(items: ConversationSummary[]) {
  const result = new Map<string, ConversationSummary[]>();
  const now = new Date();
  const startToday = new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime();
  for (const item of items) {
    const updated = new Date(item.updatedAt).getTime();
    const age = startToday - updated;
    const label = updated >= startToday ? '今天' : age < 2 * 86400000 ? '昨天' : age < 7 * 86400000 ? '最近 7 天' : '更早';
    result.set(label, [...(result.get(label) ?? []), item]);
  }
  return [...result].map(([label, groupItems]) => ({ label, items: groupItems }));
}

function errorText(reason: unknown) { return reason instanceof Error ? reason.message : String(reason); }

createRoot(document.getElementById('root')!).render(<React.StrictMode><App/></React.StrictMode>);
