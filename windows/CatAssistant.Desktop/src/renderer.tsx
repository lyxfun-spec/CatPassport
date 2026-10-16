import React, { useEffect, useReducer, useState } from 'react';
import { createRoot } from 'react-dom/client';
import type { BackendSnapshot, CatDevice } from './shared';
import { initialState, reduceBackendEvent } from './state';
import './styles.css';

type Page = 'connection' | 'status' | 'chat' | 'settings';

function Icon({ name }: { name: Page }) {
  const paths: Record<Page, React.ReactNode> = {
    connection: <><path d="M10 13a5 5 0 0 0 7.1 0l2-2a5 5 0 0 0-7.1-7.1l-1.1 1.1"/><path d="M14 11a5 5 0 0 0-7.1 0l-2 2A5 5 0 0 0 12 20.1l1.1-1.1"/></>,
    status: <><path d="M4 4h16v12H7l-3 3z"/><path d="M8 8h8M8 12h5"/></>,
    chat: <><path d="M12 3a8 8 0 0 0-7 12l-1 5 5-1a8 8 0 1 0 3-16z"/><path d="M8 11h.01M12 11h.01M16 11h.01"/></>,
    settings: <><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.9l.1.1-2.8 2.8-.1-.1a1.7 1.7 0 0 0-1.9-.3 1.7 1.7 0 0 0-1 1.6V21h-4v-.1a1.7 1.7 0 0 0-1-1.6 1.7 1.7 0 0 0-1.9.3l-.1.1L4.2 17l.1-.1a1.7 1.7 0 0 0 .3-1.9A1.7 1.7 0 0 0 3 14H3v-4h.1a1.7 1.7 0 0 0 1.6-1 1.7 1.7 0 0 0-.3-1.9L4.2 7 7 4.2l.1.1A1.7 1.7 0 0 0 9 4.6a1.7 1.7 0 0 0 1-1.6V3h4v.1a1.7 1.7 0 0 0 1 1.6 1.7 1.7 0 0 0 1.9-.3l.1-.1L19.8 7l-.1.1a1.7 1.7 0 0 0-.3 1.9 1.7 1.7 0 0 0 1.6 1h.1v4H21a1.7 1.7 0 0 0-1.6 1z"/></>
  };
  return <svg viewBox="0 0 24 24" aria-hidden="true">{paths[name]}</svg>;
}

function invoke(method: string, params?: Record<string, unknown>) {
  return window.catAssistant.invoke<BackendSnapshot>(method, params);
}

function App() {
  const [state, dispatch] = useReducer(reduceBackendEvent, initialState);
  const [page, setPage] = useState<Page>('connection');
  const [busy, setBusy] = useState<string>();
  const [error, setError] = useState<string>();

  useEffect(() => {
    const unsubscribe = window.catAssistant.onEvent(dispatch);
    invoke('state.get').then(snapshot => dispatch({ event: 'state.changed', data: snapshot })).catch(reason => setError(String(reason.message ?? reason)));
    return unsubscribe;
  }, []);

  const run = async (name: string, action: () => Promise<unknown>) => {
    setBusy(name); setError(undefined);
    try { await action(); } catch (reason) { setError(reason instanceof Error ? reason.message : String(reason)); }
    finally { setBusy(undefined); }
  };

  const snapshot = state.snapshot;
  return <div className="shell">
    <aside>
      <div className="brand"><div className="cat">🐱</div><div><strong>小猫助手</strong><span>Windows AI 网关</span></div></div>
      <nav>
        {([['connection','连接'],['status','状态'],['chat','电脑对话'],['settings','设置']] as [Page,string][]).map(([id,label]) =>
          <button key={id} className={page === id ? 'active' : ''} onClick={() => setPage(id)}><Icon name={id}/><span>{label}</span></button>)}
      </nav>
      <div className="resident"><i className={snapshot?.isConnected ? 'online' : ''}/><div><b>{snapshot?.isConnected ? '卡片在线' : '后台常驻'}</b><span>关闭窗口仍会继续服务</span></div></div>
    </aside>
    <main>
      {(error || state.backendError) && <div className="alert">{error ?? state.backendError}</div>}
      {!snapshot ? <Loading/> : page === 'connection' ? <Connection snapshot={snapshot} busy={busy} run={run}/>
        : page === 'status' ? <Status snapshot={snapshot} busy={busy} run={run}/>
        : page === 'chat' ? <Chat snapshot={snapshot} streaming={state.streamingText} busy={busy} run={run}/>
        : <Settings snapshot={snapshot} busy={busy} run={run}/>} 
    </main>
  </div>;
}

function Header({ eyebrow, title, description, action }: { eyebrow: string; title: string; description: string; action?: React.ReactNode }) {
  return <header className="page-header"><div><span>{eyebrow}</span><h1>{title}</h1><p>{description}</p></div>{action}</header>;
}

function Loading() { return <div className="loading"><div className="pulse">🐾</div><h2>正在启动 .NET 后台</h2><p>正在恢复本地会话并准备蓝牙服务…</p></div>; }

function Connection({ snapshot, busy, run }: { snapshot: BackendSnapshot; busy?: string; run: (n:string,a:()=>Promise<unknown>)=>Promise<void> }) {
  const connect = (device: CatDevice) => run('connect', () => invoke('devices.connect', { address: device.address, name: device.name }));
  const unpair = () => run('forget', () => invoke('devices.forget'));
  const owner = snapshot.pairedDeviceAddress;
  const description = snapshot.isConnected ? '当前客户端只服务这张已绑定的 AI 卡片。'
    : snapshot.hasPairedDevice ? '正在自动恢复已绑定卡片；其它卡片不会被连接。'
    : '选择一张 AI 卡片完成首次绑定。';
  return <section className="page"><Header eyebrow="BLE GATEWAY" title="连接卡片" description={description} action={<button className="ghost danger unpair" disabled={!snapshot.hasPairedDevice || !!busy} onClick={() => void unpair()}>{busy === 'forget' ? '正在取消…' : '取消配对'}</button>}/>
    <div className="scan-state"><i className={snapshot.isConnected ? 'online' : ''}/><span>{snapshot.isConnected ? '已绑定卡片在线' : busy === 'connect' ? '正在建立安全连接…' : snapshot.hasPairedDevice ? '正在等待已绑定卡片…' : '正在自动扫描附近设备…'}</span></div>
    <div className="device-grid">{snapshot.devices.length ? snapshot.devices.map(device => {
      const isOwner = owner == null || device.address === owner;
      const locked = snapshot.isConnected || !isOwner;
      const hint = snapshot.isConnected ? '已绑定卡片在线' : !isOwner ? '已绑定其它卡片' : snapshot.hasPairedDevice ? '已绑定 · 点击重连' : '点击绑定';
      return <button key={device.address} className={`device ${locked ? 'locked' : ''}`} disabled={locked || !!busy} onClick={() => void connect(device)}><span className="signal">{device.rssi > -60 ? '▂▄▆█' : device.rssi > -75 ? '▂▄▆' : '▂▄'}</span><strong>{device.name}</strong><code>{device.addressText}</code><small>{device.rssi} dBm · {hint}</small></button>;
    }) : <div className="empty"><b>{snapshot.isConnected ? '已绑定卡片在线' : snapshot.hasPairedDevice ? '正在查找已绑定卡片' : '正在查找附近卡片'}</b><span>{snapshot.hasPairedDevice ? '如需更换卡片，请先点击右上角“取消配对”。' : '请确认卡片开机并正在广播，无需手动刷新。'}</span></div>}</div>
    <div className="diagnostic"><div><span>连接诊断</span><strong>{snapshot.detail}</strong></div><code>PDU {snapshot.maxPduSize || '--'} · 要求 ≥177</code></div>
  </section>;
}

function Status({ snapshot, busy, run }: { snapshot: BackendSnapshot; busy?: string; run: (n:string,a:()=>Promise<unknown>)=>Promise<void> }) {
  return <section className="page"><Header eyebrow="SERVICE STATUS" title="运行状态" description="窗口隐藏后，BLE、音频与 AI 服务继续由 .NET 后台处理。"/>
    <div className="status-card"><div className="orb"><i className={snapshot.isConnected ? 'online' : ''}/></div><div><span>当前阶段</span><h2>{snapshot.status}</h2><p>{snapshot.detail}</p></div><code>PDU {snapshot.maxPduSize || '--'}</code></div>
    <div className="toolbar"><button className="primary" disabled={!!busy} onClick={() => void run('new', () => invoke('conversation.new'))}>新对话</button><button disabled={!!busy} onClick={() => void run('cancel', () => invoke('conversation.cancel'))}>取消当前轮次</button></div>
    <div className="privacy"><b>本地与安全</b><p>API Key 仍由当前 Windows 用户的 DPAPI 加密；SQLite 只保存设置和会话，不保存原始录音、密钥或调试对话日志。</p></div>
  </section>;
}

function Chat({ snapshot, streaming, busy, run }: { snapshot: BackendSnapshot; streaming: string; busy?: string; run: (n:string,a:()=>Promise<unknown>)=>Promise<void> }) {
  const [text, setText] = useState('');
  const send = async () => { const value = text.trim(); if (!value) return; setText(''); await run('send', () => invoke('conversation.send', { text: value })); };
  return <section className="page chat-page"><Header eyebrow="DESKTOP CONVERSATION" title="电脑对话" description="与卡片共用同一套会话、模型和流式后台结构。"/>
    <div className="messages">{snapshot.messages.length === 0 && !streaming ? <div className="empty"><b>开始一段新对话</b><span>消息由 .NET 后台发送，前端只负责显示流式结果。</span></div> : snapshot.messages.map((message,index) => <article key={`${message.createdAt}-${index}`} className={message.role === 'user' ? 'user' : 'assistant'}><small>{message.role === 'user' ? '你' : '小猫助手'}</small><p>{message.content}</p></article>)}{streaming && <article className="assistant streaming"><small>小猫助手 · 正在输入</small><p>{streaming}<i/></p></article>}</div>
    <div className="composer"><textarea value={text} onChange={event => setText(event.target.value)} onKeyDown={event => { if (event.key === 'Enter' && !event.shiftKey) { event.preventDefault(); void send(); } }} placeholder="直接在电脑上问小猫助手…"/><button className="primary" disabled={!text.trim() || busy === 'send'} onClick={() => void send()}>发送</button></div>
  </section>;
}

function Settings({ snapshot, busy, run }: { snapshot: BackendSnapshot; busy?: string; run: (n:string,a:()=>Promise<unknown>)=>Promise<void> }) {
  const [apiKey, setApiKey] = useState(''); const [asr, setAsr] = useState(snapshot.settings.asrModel); const [chat, setChat] = useState(snapshot.settings.chatModel); const [timeout, setTimeoutValue] = useState(snapshot.settings.screenTimeoutSeconds); const [reconnect, setReconnect] = useState(snapshot.settings.autoReconnect);
  const save = () => run('save', () => invoke('settings.save', { apiKey: apiKey || undefined, asrModel: asr, chatModel: chat, screenTimeoutSeconds: timeout, autoReconnect: reconnect })).then(() => setApiKey(''));
  return <section className="page"><Header eyebrow="PREFERENCES" title="设置" description="模型配置保存在原 SQLite 数据库，密钥不会进入 Electron 渲染进程的持久存储。"/>
    <div className="form"><label><span>DashScope API Key <em>{snapshot.settings.hasApiKey ? '已安全配置' : '尚未配置'}</em></span><input type="password" value={apiKey} onChange={event => setApiKey(event.target.value)} placeholder="留空表示不更改" autoComplete="off"/></label><div className="row"><label><span>ASR 模型</span><input value={asr} onChange={event => setAsr(event.target.value)}/></label><label><span>聊天模型</span><input value={chat} onChange={event => setChat(event.target.value)}/></label></div><label><span>卡片屏幕超时（秒）</span><input type="number" min="0" max="86400" value={timeout} onChange={event => setTimeoutValue(Math.max(0, Math.min(86400, Number(event.target.value))))}/><small>0 表示永不熄屏。</small></label><label className="check"><input type="checkbox" checked={reconnect} onChange={event => setReconnect(event.target.checked)}/><span>记住设备并自动重连</span></label><button className="primary save" disabled={!!busy} onClick={() => void save()}>{busy === 'save' ? '保存中…' : '保存设置'}</button></div>
  </section>;
}

createRoot(document.getElementById('root')!).render(<React.StrictMode><App/></React.StrictMode>);
