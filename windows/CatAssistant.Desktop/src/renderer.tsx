import React, { useEffect, useLayoutEffect, useMemo, useReducer, useRef, useState } from 'react';
import { createRoot } from 'react-dom/client';
import ReactMarkdown from 'react-markdown';
import type { BackendEvent, BootstrapState, CatDevice, ConversationDetail, ConversationMessage, ConversationSummary, PublicSettings } from './shared';
import { deviceDiscoveryMessage, initialState, reduceBackendEvent } from './state';
import { clampTextareaHeight, greetingForHour, isNearScrollEnd, scrollThumbMetrics, shouldSubmitComposer } from './ui-behavior';
import { AnchorRect, AnchoredSurface, rectOf, ScrollArea } from './ui-primitives';
import './ui-tokens.css';
import './styles.css';

type IconName = 'new' | 'search' | 'chat' | 'work' | 'device' | 'settings' | 'menu' | 'send'
  | 'copy' | 'close' | 'sidebar' | 'check' | 'back' | 'rename' | 'delete' | 'refresh'
  | 'key' | 'shield' | 'info' | 'chevron' | 'cat';

const iconPaths: Record<IconName, React.ReactNode> = {
  new: <path d="M12 5v14M5 12h14"/>,
  search: <><circle cx="10.5" cy="10.5" r="6.5"/><path d="m16 16 4 4"/></>,
  chat: <><path d="M5 18.2 3.8 21l3.7-1.2A9 9 0 1 0 5 18.2Z"/><path d="M8 11.5h8M8 15h5"/></>,
  work: <><rect x="4" y="7" width="16" height="12" rx="2"/><path d="M9 7V5h6v2M4 12h16M10 12v2h4v-2"/></>,
  device: <><rect x="6" y="3" width="12" height="18" rx="3"/><path d="M10 7h4M10 17h4"/></>,
  settings: <><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.9l.1.1-2.8 2.8-.1-.1a1.7 1.7 0 0 0-1.9-.3 1.7 1.7 0 0 0-1 1.6V21h-4v-.1a1.7 1.7 0 0 0-1-1.6 1.7 1.7 0 0 0-1.9.3l-.1.1L4.2 17l.1-.1a1.7 1.7 0 0 0 .3-1.9A1.7 1.7 0 0 0 3 14v-4a1.7 1.7 0 0 0 1.6-1 1.7 1.7 0 0 0-.3-1.9L4.2 7 7 4.2l.1.1A1.7 1.7 0 0 0 9 4.6 1.7 1.7 0 0 0 10 3h4a1.7 1.7 0 0 0 1 1.6 1.7 1.7 0 0 0 1.9-.3l.1-.1L19.8 7l-.1.1a1.7 1.7 0 0 0-.3 1.9 1.7 1.7 0 0 0 1.6 1v4a1.7 1.7 0 0 0-1.6 1Z"/></>,
  menu: <><circle cx="5" cy="12" r="1"/><circle cx="12" cy="12" r="1"/><circle cx="19" cy="12" r="1"/></>,
  send: <><path d="m4 12 16-8-6.5 16-2-6Z"/><path d="m11.5 14 3-3"/></>,
  copy: <><rect x="8" y="8" width="11" height="11" rx="2"/><path d="M16 8V6a2 2 0 0 0-2-2H6a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h2"/></>,
  close: <path d="m6 6 12 12M18 6 6 18"/>,
  sidebar: <><rect x="3" y="4" width="18" height="16" rx="2"/><path d="M9 4v16"/></>,
  check: <path d="m5 12 4 4L19 6"/>,
  back: <path d="m15 18-6-6 6-6"/>,
  rename: <><path d="M12 20h9"/><path d="M16.5 3.5a2.1 2.1 0 0 1 3 3L8 18l-4 1 1-4Z"/></>,
  delete: <><path d="M4 7h16M9 7V4h6v3M7 7l1 13h8l1-13M10 11v5M14 11v5"/></>,
  refresh: <><path d="M20 11a8 8 0 1 0-2.3 5.7"/><path d="M20 4v7h-7"/></>,
  key: <><circle cx="8" cy="15" r="4"/><path d="m11 12 8-8M15 8l2 2M17 6l2 2"/></>,
  shield: <><path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10Z"/><path d="m9 12 2 2 4-4"/></>,
  info: <><circle cx="12" cy="12" r="9"/><path d="M12 11v5M12 8h.01"/></>,
  chevron: <path d="m9 18 6-6-6-6"/>,
  cat: <><path d="M6.5 8 4 4v9a8 8 0 0 0 16 0V4l-2.5 4"/><path d="M9 13h.01M15 13h.01M10 17c1.3 1 2.7 1 4 0"/></>
};

function Icon({ name, size = 18 }: { name: IconName; size?: number }) {
  return <svg className="icon" width={size} height={size} viewBox="0 0 24 24" aria-hidden="true">{iconPaths[name]}</svg>;
}
function invoke<T>(method: string, params?: Record<string, unknown>) { return window.catAssistant.invoke<T>(method, params); }
interface NoticeState { id: number; text: string; tone?: 'default' | 'danger' }
type AppView = 'conversation' | 'compose' | 'settings';
type ActionSurface = { kind: 'rename' | 'delete'; anchor: AnchorRect } | undefined;

function useViewTransition(target: AppView) {
  const [displayed, setDisplayed] = useState(target);
  const [phase, setPhase] = useState<'steady' | 'out' | 'in'>('steady');
  const version = useRef(0);
  useEffect(() => {
    if (target === displayed) return;
    const current = ++version.current;
    setPhase('out');
    const swap = setTimeout(() => {
      if (version.current !== current) return;
      setDisplayed(target);
      setPhase('in');
      requestAnimationFrame(() => { if (version.current === current) setPhase('steady'); });
    }, 90);
    return () => clearTimeout(swap);
  }, [target, displayed]);
  return { displayed, phase };
}

function App() {
  const [state, dispatch] = useReducer(reduceBackendEvent, initialState);
  const [sidebarCollapsed, setSidebarCollapsed] = useState(false);
  const [requestedView, setRequestedView] = useState<AppView>('conversation');
  const { displayed: displayedView, phase: viewPhase } = useViewTransition(requestedView);
  const [draftMode, setDraftMode] = useState<'chat' | 'work'>('chat');
  const [actionSurface, setActionSurface] = useState<ActionSurface>();
  const [deviceOpen, setDeviceOpen] = useState(false);
  const [query, setQuery] = useState('');
  const [draft, setDraft] = useState('');
  const [busy, setBusy] = useState<Set<string>>(() => new Set());
  const [localError, setLocalError] = useState<string>();
  const [notice, setNotice] = useState<NoticeState>();
  const [composerFocusSignal, setComposerFocusSignal] = useState(0);
  const onboardingShown = useRef(false);
  const noticeSequence = useRef(0);

  const refresh = async () => dispatch({ event: 'ui.bootstrap', data: await invoke<BootstrapState>('app.bootstrap') });
  const isBusy = (name: string) => busy.has(name);
  const showNotice = (text: string, tone: NoticeState['tone'] = 'default') => setNotice({ id: ++noticeSequence.current, text, tone });
  const run = async <T,>(name: string, action: () => Promise<T>): Promise<T | undefined> => {
    setBusy(current => new Set(current).add(name));
    setLocalError(undefined);
    try { return await action(); }
    catch (reason) { setLocalError(errorText(reason)); return undefined; }
    finally { setBusy(current => { const next = new Set(current); next.delete(name); return next; }); }
  };

  useEffect(() => {
    let refreshTimer: ReturnType<typeof setTimeout> | undefined;
    const unsubscribe = window.catAssistant.onEvent((event: BackendEvent) => {
      dispatch(event);
      if (event.event === 'message.delta' || event.event === 'device.changed' || event.event === 'backend.ready' || event.event === 'backend.exited') return;
      clearTimeout(refreshTimer);
      refreshTimer = setTimeout(() => void refresh().catch(reason => setLocalError(errorText(reason))), 30);
    });
    invoke<BootstrapState>('app.bootstrap').then(data => dispatch({ event: 'ui.bootstrap', data })).catch(reason => setLocalError(errorText(reason)));
    return () => { clearTimeout(refreshTimer); unsubscribe(); };
  }, []);
  useEffect(() => {
    if (!onboardingShown.current && state.bootstrap && !state.bootstrap.settings.hasApiKey) {
      onboardingShown.current = true;
      setRequestedView('settings');
    }
  }, [state.bootstrap]);
  useEffect(() => {
    if (!notice) return;
    const timer = setTimeout(() => setNotice(current => current?.id === notice.id ? undefined : current), 2600);
    return () => clearTimeout(timer);
  }, [notice]);

  if (!state.bootstrap) return <Loading error={localError ?? state.backendError}/>;
  const bootstrap = state.bootstrap;
  const active = bootstrap.activeConversation;
  const selectConversation = async (id: string) => {
    const detail = await run('activate', () => invoke<ConversationDetail>('conversations.activate', { id }));
    if (detail) dispatch({ event: 'ui.conversation', data: detail });
    setRequestedView('conversation');
    void refresh().catch(reason => setLocalError(errorText(reason)));
  };
  const startCompose = () => {
    setDraftMode('chat');
    setRequestedView('compose');
    setComposerFocusSignal(value => value + 1);
  };
  const send = async () => {
    const text = draft.trim();
    if (!text || isBusy('send')) return;
    if (requestedView === 'compose') {
      if (draftMode !== 'chat') return;
      setDraft('');
      const detail = await run('send', () => invoke<ConversationDetail>('conversations.start', { mode: draftMode, text }));
      if (detail) { dispatch({ event: 'ui.conversation', data: detail }); setRequestedView('conversation'); }
      else setDraft(text);
      return;
    }
    if (!active) return;
    setDraft('');
    const detail = await run('send', () => invoke<ConversationDetail>('conversations.send', { id: active.conversation.id, text }));
    if (detail) dispatch({ event: 'ui.conversation', data: detail });
  };
  const rename = async (title: string) => {
    if (!active || title === active.conversation.title) { setActionSurface(undefined); return; }
    const result = await run('rename', () => invoke('conversations.rename', { id: active.conversation.id, title }));
    if (result === undefined) return;
    setActionSurface(undefined); await refresh(); showNotice('对话名称已更新');
  };
  const remove = async () => {
    if (!active) return;
    const result = await run('delete', () => invoke('conversations.delete', { id: active.conversation.id }));
    if (result === undefined) return;
    setActionSurface(undefined); await refresh(); showNotice('对话已删除');
  };
  const saveSettings = async (patch: Partial<PublicSettings> & { apiKey?: string }, successText: string) => {
    const current = bootstrap.settings;
    const result = await run('settings', () => invoke<PublicSettings>('settings.save', {
      apiKey: patch.apiKey || undefined,
      asrModel: patch.asrModel ?? current.asrModel,
      chatModel: patch.chatModel ?? current.chatModel,
      screenTimeoutSeconds: patch.screenTimeoutSeconds ?? current.screenTimeoutSeconds,
      autoReconnect: patch.autoReconnect ?? current.autoReconnect,
      webSearchEnabled: patch.webSearchEnabled ?? current.webSearchEnabled
    }));
    if (!result) return false;
    await refresh(); showNotice(successText); return true;
  };

  return <>
    <div className={`app-frame ${sidebarCollapsed ? 'sidebar-collapsed' : ''}`}>
      <Sidebar bootstrap={bootstrap} query={query} onQuery={setQuery} collapsed={sidebarCollapsed}
        onToggle={() => setSidebarCollapsed(value => !value)} onNew={startCompose}
        onSelect={id => void selectConversation(id)} onDevice={() => setDeviceOpen(true)}
        composeActive={requestedView === 'compose'}
        settingsActive={requestedView === 'settings'} onSettings={() => setRequestedView('settings')}/>
      <main className={`content-pane view-${displayedView} view-phase-${viewPhase}`}>
        {(localError || state.backendError) && <GlobalAlert
          text={localError ?? state.backendError!}
          onClose={() => { setLocalError(undefined); dispatch({ event: 'ui.clearError' }); }}/>
        }
        {displayedView === 'settings'
          ? <SettingsPage settings={bootstrap.settings} onboarding={!bootstrap.settings.hasApiKey} busy={isBusy('settings')} onBack={() => setRequestedView('conversation')} onSave={saveSettings}/>
          : displayedView === 'compose' || !active
            ? <DraftConversation mode={draftMode} onMode={setDraftMode} value={draft} onChange={setDraft} onSend={() => void send()} hasApiKey={bootstrap.settings.hasApiKey} sending={isBusy('send')} focusSignal={composerFocusSignal} onOpenSettings={() => setRequestedView('settings')}/>
            : <><ConversationHeader conversation={active.conversation} onAction={(kind, anchor) => setActionSurface({ kind, anchor })}/><MessageList detail={active} runError={state.runError}/><Composer value={draft} onChange={setDraft} onSend={() => void send()} hasApiKey={bootstrap.settings.hasApiKey} sending={isBusy('send')} focusSignal={composerFocusSignal} onOpenSettings={() => setRequestedView('settings')}/></>
        }
      </main>
    </div>
    <RenameDialog open={actionSurface?.kind === 'rename'} anchor={actionSurface?.anchor} conversation={active?.conversation} busy={isBusy('rename')} onClose={() => setActionSurface(undefined)} onRename={title => void rename(title)}/>
    <DeleteDialog open={actionSurface?.kind === 'delete'} anchor={actionSurface?.anchor} conversation={active?.conversation} busy={isBusy('delete')} onClose={() => setActionSurface(undefined)} onConfirm={() => void remove()}/>
    <DevicePanel open={deviceOpen} bootstrap={bootstrap} busy={isBusy('device')} run={run} onClose={() => setDeviceOpen(false)} onOpenConversation={id => { setDeviceOpen(false); void selectConversation(id); }} onRefresh={() => void refresh()} onNotice={showNotice}/>
    <Notice notice={notice}/>
  </>;
}

function BrandMark({ large = false }: { large?: boolean }) {
  return <span className={`brand-mark ${large ? 'large' : ''}`} aria-hidden="true"><Icon name="cat" size={large ? 28 : 21}/></span>;
}

function Sidebar({ bootstrap, query, onQuery, collapsed, onToggle, onNew, onSelect, onDevice, composeActive, settingsActive, onSettings }: {
  bootstrap: BootstrapState; query: string; onQuery(value: string): void; collapsed: boolean; onToggle(): void;
  onNew(): void; onSelect(id: string): void; onDevice(): void; composeActive: boolean; settingsActive: boolean; onSettings(): void;
}) {
  const filtered = useMemo(() => bootstrap.conversations.filter(item => item.title.toLocaleLowerCase().includes(query.toLocaleLowerCase())), [bootstrap.conversations, query]);
  const groups = groupConversations(filtered);
  const activeDeviceConversation = bootstrap.conversations.find(item => item.id === bootstrap.device.activeConversationId);
  return <aside className="sidebar" aria-label="会话导航">
    <div className="brand-row"><BrandMark/><strong className="brand-name">小猫助手</strong><button className="icon-button sidebar-toggle" onClick={onToggle} aria-label={collapsed ? '展开侧边栏' : '收起侧边栏'}><Icon name="sidebar"/></button></div>
    <button className={`new-conversation ${composeActive ? 'active' : ''}`} onClick={onNew} aria-label="新建对话" aria-current={composeActive ? 'true' : undefined}><Icon name="new"/><span>新对话</span></button>
    {!collapsed && <label className="search-box"><Icon name="search"/><input value={query} onChange={event => onQuery(event.target.value)} placeholder="搜索对话" aria-label="搜索对话"/></label>}
    <ScrollArea className="conversation-scroll" viewportClassName="conversation-list">
      {groups.map(group => <section className="conversation-group" key={group.label} aria-label={group.label}><h2>{group.label}</h2>{group.items.map(item => <button key={item.id} className={`conversation-item ${!settingsActive && !composeActive && bootstrap.activeConversationId === item.id ? 'active' : ''}`} onClick={() => onSelect(item.id)} aria-current={!settingsActive && !composeActive && bootstrap.activeConversationId === item.id ? 'page' : undefined} title={item.title}><Icon name="chat" size={16}/><span>{item.title}</span></button>)}</section>)}
      {filtered.length === 0 && <div className="no-results">{query ? '没有匹配的对话' : '还没有对话'}</div>}
    </ScrollArea>
    <div className="sidebar-footer">
      <button className="footer-entry" onClick={onDevice}><span className={`status-dot ${bootstrap.device.isConnected ? 'online' : ''}`}/><Icon name="device"/><span className="footer-copy"><b>{bootstrap.device.isConnected ? 'AI 卡片' : '连接 AI 卡片'}</b><small>{bootstrap.device.isConnected ? activeDeviceConversation?.title ?? '已连接' : '可选辅助硬件'}</small></span></button>
      <button className={`footer-entry ${settingsActive ? 'active' : ''}`} onClick={onSettings}><Icon name="settings"/><span className="footer-copy"><b>设置</b><small>{bootstrap.settings.hasApiKey ? '服务已配置' : '需要配置 API Key'}</small></span></button>
    </div>
  </aside>;
}

function ConversationHeader({ conversation, onAction }: { conversation: ConversationSummary; onAction(kind: 'rename' | 'delete', anchor: AnchorRect): void }) {
  const [menuOpen, setMenuOpen] = useState(false);
  const [menuRendered, setMenuRendered] = useState(false);
  const [keyboardOpen, setKeyboardOpen] = useState(false);
  const wrapper = useRef<HTMLDivElement>(null);
  const trigger = useRef<HTMLButtonElement>(null);
  const firstItem = useRef<HTMLButtonElement>(null);
  const menuCloseTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const menuOpenFrames = useRef<{ first?: number; second?: number }>({});
  const cancelMenuOpen = () => {
    if (menuOpenFrames.current.first !== undefined) cancelAnimationFrame(menuOpenFrames.current.first);
    if (menuOpenFrames.current.second !== undefined) cancelAnimationFrame(menuOpenFrames.current.second);
    menuOpenFrames.current = {};
  };
  const closeMenu = (restoreFocus = false) => {
    cancelMenuOpen();
    clearTimeout(menuCloseTimer.current);
    setMenuOpen(false);
    menuCloseTimer.current = setTimeout(() => setMenuRendered(false), 260);
    if (restoreFocus) trigger.current?.focus({ preventScroll: true });
  };
  const toggleMenu = (event: React.MouseEvent<HTMLButtonElement>) => {
    if (menuOpen) { closeMenu(); return; }
    const fromKeyboard = event.detail === 0;
    cancelMenuOpen();
    clearTimeout(menuCloseTimer.current);
    setKeyboardOpen(fromKeyboard);
    setMenuRendered(true);
    menuOpenFrames.current.first = requestAnimationFrame(() => {
      menuOpenFrames.current.second = requestAnimationFrame(() => {
        menuOpenFrames.current = {};
        setMenuOpen(true);
      });
    });
  };
  useEffect(() => () => { cancelMenuOpen(); clearTimeout(menuCloseTimer.current); }, []);
  useEffect(() => {
    if (!menuOpen) return;
    if (keyboardOpen) requestAnimationFrame(() => firstItem.current?.focus({ preventScroll: true }));
    const onPointer = (event: PointerEvent) => { if (!wrapper.current?.contains(event.target as Node)) closeMenu(); };
    const onKey = (event: KeyboardEvent) => { if (event.key === 'Escape') closeMenu(true); };
    document.addEventListener('pointerdown', onPointer); document.addEventListener('keydown', onKey);
    return () => { document.removeEventListener('pointerdown', onPointer); document.removeEventListener('keydown', onKey); };
  }, [menuOpen, keyboardOpen]);
  const choose = (kind: 'rename' | 'delete', element: HTMLButtonElement) => { const anchor = rectOf(element); closeMenu(true); onAction(kind, anchor); };
  return <header className="conversation-header"><h1>{conversation.title}</h1><div className="header-menu" ref={wrapper}><button ref={trigger} className="icon-button" onClick={toggleMenu} aria-label="对话选项" aria-haspopup="menu" aria-expanded={menuOpen}><Icon name="menu"/></button>{menuRendered && <div className={`popover-menu ${menuOpen ? 'is-open' : 'is-closing'}`} role="menu" aria-hidden={!menuOpen} inert={!menuOpen}><button ref={firstItem} role="menuitem" onClick={event => choose('rename', event.currentTarget)}><Icon name="rename"/>重命名</button><button role="menuitem" className="danger" onClick={event => choose('delete', event.currentTarget)}><Icon name="delete"/>删除</button></div>}</div></header>;
}

function MessageList({ detail, runError }: { detail: ConversationDetail; runError?: string }) {
  const scroller = useRef<HTMLDivElement>(null);
  const pinned = useRef(true);
  const lastContent = detail.messages.at(-1)?.content;
  useEffect(() => { pinned.current = true; requestAnimationFrame(() => scroller.current?.scrollTo({ top: scroller.current.scrollHeight })); }, [detail.conversation.id]);
  useEffect(() => { if (pinned.current) requestAnimationFrame(() => scroller.current?.scrollTo({ top: scroller.current.scrollHeight })); }, [detail.messages.length, lastContent]);
  const onScroll = () => { const element = scroller.current; if (element) pinned.current = isNearScrollEnd(element.scrollHeight, element.scrollTop, element.clientHeight); };
  return <ScrollArea ref={scroller} className="message-scroll" viewportClassName={`messages ${detail.messages.length ? '' : 'empty-chat'}`} onScroll={onScroll}>
    {detail.messages.length === 0 ? <div className="empty-greeting"><BrandMark large/><h2>{greetingForHour(new Date().getHours())}</h2></div> : <div className="message-column">{detail.messages.map(message => <MessageItem key={message.id} message={message} citations={detail.citations.filter(item => item.messageId === message.id)}/>)}{runError && <div className="run-error" role="alert"><Icon name="info"/><span>{runError}</span></div>}</div>}
  </ScrollArea>;
}

function MessageItem({ message, citations }: { message: ConversationMessage; citations: ConversationDetail['citations'] }) {
  const [copyState, setCopyState] = useState<'idle' | 'copied' | 'failed'>('idle');
  const copyTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  useEffect(() => () => clearTimeout(copyTimer.current), []);
  const copy = async () => { clearTimeout(copyTimer.current); try { await navigator.clipboard.writeText(message.content); setCopyState('copied'); } catch { setCopyState('failed'); } copyTimer.current = setTimeout(() => setCopyState('idle'), 1650); };
  if (message.role === 'assistant' && message.status === 'streaming' && !message.content) return <article className="message assistant"><div className="thinking" role="status"><i/><i/><i/><span className="sr-only">正在生成回复</span></div></article>;
  const safeCitations = citations.filter(citation => safeHttpUrl(citation.url));
  return <article className={`message ${message.role} ${message.status}`}><div className="message-body">{message.role === 'assistant' ? <ReactMarkdown components={{ a: ({ href, children }) => safeHttpUrl(href) ? <a href={href} target="_blank" rel="noreferrer">{children}</a> : <span>{children}</span> }}>{message.content}</ReactMarkdown> : <p>{message.content}</p>}</div>{safeCitations.length > 0 && <div className="sources"><span>参考</span>{safeCitations.map(citation => <a key={citation.id} href={citation.url} target="_blank" rel="noreferrer"><b>{citation.ordinal}</b><span>{citation.title}</span></a>)}</div>}{message.role === 'assistant' && message.content && <div className="message-actions">{message.status === 'interrupted' && <span>已中断</span>}{message.status === 'failed' && <span className="failed-label">生成失败</span>}<button onClick={() => void copy()} aria-label="复制回复"><Icon name={copyState === 'copied' ? 'check' : 'copy'} size={15}/>{copyState === 'copied' ? '已复制' : copyState === 'failed' ? '复制失败' : '复制'}</button></div>}</article>;
}

function Composer({ value, onChange, onSend, hasApiKey, sending, disabled = false, focusSignal, onOpenSettings }: { value: string; onChange(value: string): void; onSend(): void; hasApiKey: boolean; sending: boolean; disabled?: boolean; focusSignal: number; onOpenSettings(): void }) {
  const textarea = useRef<HTMLTextAreaElement>(null);
  const rail = useRef<HTMLDivElement>(null);
  const [scrollActive, setScrollActive] = useState(false);
  const [thumb, setThumb] = useState({ visible: false, size: 0, offset: 0 });
  const hideTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const canSend = hasApiKey && Boolean(value.trim()) && !sending && !disabled;
  const updateThumb = () => { const element = textarea.current, track = rail.current; if (element && track) setThumb(scrollThumbMetrics(element.scrollHeight, element.clientHeight, element.scrollTop, track.clientHeight, 18)); };
  const revealThumb = () => { setScrollActive(true); clearTimeout(hideTimer.current); hideTimer.current = setTimeout(() => setScrollActive(false), 820); };
  useEffect(() => { const element = textarea.current; if (!element) return; element.style.height = '0px'; element.style.height = `${clampTextareaHeight(element.scrollHeight)}px`; element.style.overflowY = element.scrollHeight > 152 ? 'auto' : 'hidden'; requestAnimationFrame(updateThumb); }, [value]);
  useEffect(() => { if (focusSignal) textarea.current?.focus({ preventScroll: true }); }, [focusSignal]);
  useEffect(() => () => clearTimeout(hideTimer.current), []);
  return <div className="composer-wrap"><div className="composer"><div className={`composer-editor ${scrollActive ? 'scroll-active' : ''}`}><textarea ref={textarea} value={value} rows={1} placeholder="输入消息" aria-label="输入消息" onScroll={() => { updateThumb(); revealThumb(); }} onChange={event => onChange(event.target.value)} onKeyDown={event => { if (shouldSubmitComposer(event.key, event.shiftKey, event.nativeEvent.isComposing)) { event.preventDefault(); if (canSend) onSend(); } }}/><div ref={rail} className={`textarea-rail ${thumb.visible ? 'has-overflow' : ''}`} aria-hidden="true"><i style={{ height: thumb.size, transform: `translateY(${thumb.offset}px)` }}/></div></div><button className={`send-button ${canSend ? 'ready' : ''}`} disabled={!canSend} onClick={onSend} aria-label={sending ? '正在发送' : '发送消息'}><Icon name="send"/></button></div>{!hasApiKey && value.trim() ? <button className="key-hint" onClick={onOpenSettings}><Icon name="key" size={14}/>配置 API Key 后即可发送</button> : <p>AI 生成的内容可能不准确，请核实重要信息。</p>}</div>;
}

function DraftConversation({ mode, onMode, value, onChange, onSend, hasApiKey, sending, focusSignal, onOpenSettings }: {
  mode: 'chat' | 'work'; onMode(mode: 'chat' | 'work'): void; value: string; onChange(value: string): void;
  onSend(): void; hasApiKey: boolean; sending: boolean; focusSignal: number; onOpenSettings(): void;
}) {
  const workSelected = mode === 'work';
  return <div className="draft-conversation">
    <div className="draft-hero">
      <BrandMark large/>
      <h1>{greetingForHour(new Date().getHours())}</h1>
      <div className={`mode-selector mode-${mode}`} role="group" aria-label="对话模式">
        <span className="mode-slider" aria-hidden="true"/>
        <button type="button" className={mode === 'chat' ? 'selected' : ''} onClick={() => onMode('chat')} aria-pressed={mode === 'chat'}><Icon name="chat"/><span><b>Chat</b><small>日常对话</small></span></button>
        <button type="button" className={mode === 'work' ? 'selected' : ''} onClick={() => onMode('work')} aria-pressed={mode === 'work'}><Icon name="work"/><span><b>Work</b><small>本地任务</small></span></button>
      </div>
      <p className="mode-note">{workSelected ? 'Work 模式即将推出。' : '适合问答、写作和日常交流。'}</p>
    </div>
    <Composer value={value} onChange={onChange} onSend={onSend} hasApiKey={hasApiKey} sending={sending} disabled={workSelected} focusSignal={focusSignal} onOpenSettings={onOpenSettings}/>
  </div>;
}
function RenameDialog({ open, anchor, conversation, busy, onClose, onRename }: { open: boolean; anchor?: AnchorRect; conversation?: ConversationSummary; busy: boolean; onClose(): void; onRename(title: string): void }) {
  const [title, setTitle] = useState('');
  useEffect(() => { if (open) setTitle(conversation?.title ?? ''); }, [open, conversation?.id]);
  const valid = Boolean(title.trim()) && title.trim() !== conversation?.title;
  return <AnchoredSurface open={open} anchor={anchor} onClose={onClose} titleId="rename-title" className="compact-dialog"><form onSubmit={event => { event.preventDefault(); if (valid) onRename(title.trim()); }}><div className="surface-heading"><div><h2 id="rename-title">重命名</h2><p>为这段对话取一个更容易识别的名称。</p></div><button type="button" className="icon-button" onClick={onClose} aria-label="关闭"><Icon name="close"/></button></div><label className="line-field"><span>对话名称</span><input data-autofocus value={title} maxLength={80} onChange={event => setTitle(event.target.value)}/></label><div className="surface-actions"><button type="button" className="text-button" onClick={onClose}>取消</button><button className="primary-button" disabled={!valid || busy}>{busy ? '保存中…' : '保存'}</button></div></form></AnchoredSurface>;
}
function DeleteDialog({ open, anchor, conversation, busy, onClose, onConfirm }: { open: boolean; anchor?: AnchorRect; conversation?: ConversationSummary; busy: boolean; onClose(): void; onConfirm(): void }) {
  return <AnchoredSurface open={open} anchor={anchor} onClose={onClose} titleId="delete-title" className="compact-dialog delete-dialog"><div className="surface-heading"><div><h2 id="delete-title">删除对话？</h2><p>“{conversation?.title}”及其中的消息将无法恢复。</p></div></div><div className="surface-actions"><button className="text-button" data-autofocus onClick={onClose}>取消</button><button className="danger-button" disabled={busy} onClick={onConfirm}>{busy ? '删除中…' : '删除'}</button></div></AnchoredSurface>;
}

type SettingsSection = 'service' | 'device' | 'privacy';
function SettingsPage({ settings, onboarding, busy, onBack, onSave }: { settings: PublicSettings; onboarding: boolean; busy: boolean; onBack(): void; onSave(patch: Partial<PublicSettings> & { apiKey?: string }, successText: string): Promise<boolean> }) {
  const [requestedSection, setRequestedSection] = useState<SettingsSection>('service');
  const [displayedSection, setDisplayedSection] = useState<SettingsSection>('service');
  const [sectionPhase, setSectionPhase] = useState<'steady' | 'out' | 'in'>('steady');
  const sectionVersion = useRef(0);
  const [apiKey, setApiKey] = useState('');
  const [chatModel, setChatModel] = useState(settings.chatModel);
  const [asrModel, setAsrModel] = useState(settings.asrModel);
  const [screenTimeout, setScreenTimeout] = useState(settings.screenTimeoutSeconds);
  const [webSearch, setWebSearch] = useState(settings.webSearchEnabled);
  const [autoReconnect, setAutoReconnect] = useState(settings.autoReconnect);
  useEffect(() => { setChatModel(settings.chatModel); setAsrModel(settings.asrModel); setScreenTimeout(settings.screenTimeoutSeconds); setWebSearch(settings.webSearchEnabled); setAutoReconnect(settings.autoReconnect); }, [settings]);
  const chooseSection = (section: SettingsSection) => {
    const current = ++sectionVersion.current;
    setRequestedSection(section);
    if (section === displayedSection) { setSectionPhase('steady'); return; }
    setSectionPhase('out');
    setTimeout(() => { if (sectionVersion.current !== current) return; setDisplayedSection(section); setSectionPhase('in'); requestAnimationFrame(() => { if (sectionVersion.current === current) setSectionPhase('steady'); }); }, 90);
  };
  const toggleWeb = async (value: boolean) => { const previous = webSearch; setWebSearch(value); if (!await onSave({ webSearchEnabled: value }, value ? '联网搜索已开启' : '联网搜索已关闭')) setWebSearch(previous); };
  const toggleReconnect = async (value: boolean) => { const previous = autoReconnect; setAutoReconnect(value); if (!await onSave({ autoReconnect: value }, value ? '自动重连已开启' : '自动重连已关闭')) setAutoReconnect(previous); };
  return <section className="settings-page" aria-labelledby="settings-title"><header className="settings-page-header"><button className="icon-button" onClick={onBack} aria-label="返回对话"><Icon name="back"/></button><div><h1 id="settings-title">设置</h1>{onboarding && <p>先配置 AI 服务，即可开始对话。</p>}</div></header><div className="settings-shell"><nav className="settings-nav" aria-label="设置分类"><span className={`settings-indicator section-${requestedSection}`} aria-hidden="true"/><button className={requestedSection === 'service' ? 'active' : ''} onClick={() => chooseSection('service')}><Icon name="key"/><span>AI 服务</span></button><button className={requestedSection === 'device' ? 'active' : ''} onClick={() => chooseSection('device')}><Icon name="device"/><span>AI 卡片</span></button><button className={requestedSection === 'privacy' ? 'active' : ''} onClick={() => chooseSection('privacy')}><Icon name="shield"/><span>数据与隐私</span></button></nav><ScrollArea className="settings-scroll" viewportClassName="settings-content"><div className={`settings-section-content phase-${sectionPhase}`}>
    {displayedSection === 'service' && <section><SettingsHeading eyebrow="AI 服务" title="模型与联网" description="凭据只保存在这台电脑上。"/><div className="settings-group"><form className="setting-row editable-row" onSubmit={async event => { event.preventDefault(); if (await onSave({ apiKey: apiKey || undefined, chatModel }, 'AI 服务设置已保存')) setApiKey(''); }}><span><b>模型与 API Key</b><small>{settings.hasApiKey ? 'API Key 已安全配置' : '需要配置 API Key'}</small></span><div className="inline-fields"><input value={chatModel} onChange={event => setChatModel(event.target.value)} aria-label="Chat 模型"/><input type="password" value={apiKey} onChange={event => setApiKey(event.target.value)} placeholder={settings.hasApiKey ? '留空不更改 Key' : '输入 API Key'} aria-label="API Key" autoComplete="off"/><button className="line-save" disabled={busy}>保存</button></div></form><Switch checked={webSearch} disabled={busy} onChange={value => void toggleWeb(value)} label="联网搜索" description="需要最新信息时允许模型搜索网络"/></div></section>}
    {displayedSection === 'device' && <section><SettingsHeading eyebrow="AI 卡片" title="语音与显示" description="卡片连接后会自动同步这些选项。"/><div className="settings-group"><form className="setting-row editable-row" onSubmit={event => { event.preventDefault(); void onSave({ asrModel, screenTimeoutSeconds: screenTimeout }, '卡片设置已保存'); }}><span><b>识别与休眠</b><small>休眠时间设为 0 时保持亮屏</small></span><div className="inline-fields compact"><input value={asrModel} onChange={event => setAsrModel(event.target.value)} aria-label="语音识别模型"/><input type="number" min="0" max="86400" value={screenTimeout} onChange={event => setScreenTimeout(Math.max(0, Math.min(86400, Number(event.target.value))))} aria-label="屏幕休眠秒数"/><button className="line-save" disabled={busy}>保存</button></div></form><Switch checked={autoReconnect} disabled={busy} onChange={value => void toggleReconnect(value)} label="自动重连" description="启动后连接已绑定的卡片"/></div></section>}
    {displayedSection === 'privacy' && <section><SettingsHeading eyebrow="数据与隐私" title="本地保存" description="小猫助手不会自动同步你的对话。"/><div className="settings-group privacy-rows"><InfoRow icon="shield" title="API Key" detail="使用 Windows DPAPI 加密，仅当前用户可读取。"/><InfoRow icon="chat" title="会话历史" detail="保存在本机 SQLite 数据库中。"/><InfoRow icon="device" title="AI 卡片" detail="只接收当前对话所需的文本和控制数据。"/></div></section>}
  </div></ScrollArea></div></section>;
}
function SettingsHeading({ eyebrow, title, description }: { eyebrow: string; title: string; description: string }) { return <div className="settings-heading"><span>{eyebrow}</span><h2>{title}</h2><p>{description}</p></div>; }
function Switch({ checked, disabled, onChange, label, description }: { checked: boolean; disabled?: boolean; onChange(value: boolean): void; label: string; description: string }) { return <label className="setting-row switch-row"><span><b>{label}</b><small>{description}</small></span><input type="checkbox" checked={checked} disabled={disabled} onChange={event => onChange(event.target.checked)}/><i aria-hidden="true"><u/></i></label>; }
function InfoRow({ icon, title, detail }: { icon: IconName; title: string; detail: string }) { return <div className="setting-row info-row"><Icon name={icon}/><span><b>{title}</b><small>{detail}</small></span></div>; }

function DevicePanel({ open, bootstrap, busy, run, onClose, onOpenConversation, onRefresh, onNotice }: { open: boolean; bootstrap: BootstrapState; busy: boolean; run<T>(name: string, action: () => Promise<T>): Promise<T | undefined>; onClose(): void; onOpenConversation(id: string): void; onRefresh(): void; onNotice(text: string, tone?: NoticeState['tone']): void }) {
  const [rendered, setRendered] = useState(open);
  const [visible, setVisible] = useState(false);
  const [confirmForget, setConfirmForget] = useState(false);
  const closeTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const device = bootstrap.device;
  const active = bootstrap.conversations.find(item => item.id === device.activeConversationId);
  useEffect(() => {
    clearTimeout(closeTimer.current);
    if (open) { setRendered(true); return; }
    setVisible(false);
    setConfirmForget(false);
    closeTimer.current = setTimeout(() => setRendered(false), 310);
    return () => clearTimeout(closeTimer.current);
  }, [open]);
  useLayoutEffect(() => {
    if (!rendered || !open) return;
    let secondFrame = 0;
    const firstFrame = requestAnimationFrame(() => { secondFrame = requestAnimationFrame(() => setVisible(true)); });
    return () => { cancelAnimationFrame(firstFrame); cancelAnimationFrame(secondFrame); };
  }, [rendered, open]);
  useEffect(() => { if (!rendered) return; const onKey = (event: KeyboardEvent) => { if (event.key === 'Escape') onClose(); }; document.addEventListener('keydown', onKey); return () => document.removeEventListener('keydown', onKey); }, [rendered]);
  if (!rendered) return null;
  const connect = async (item: CatDevice) => { const result = await run('device', () => invoke('devices.connect', { address: item.address, name: item.name })); onRefresh(); if (result !== undefined) onNotice('正在连接 AI 卡片'); };
  const follow = async () => { const result = await run('device', () => invoke('devices.followDesktop')); onRefresh(); if (result !== undefined) onNotice('AI 卡片将跟随当前对话'); };
  const forget = async () => { const result = await run('device', () => invoke('devices.forget')); onRefresh(); if (result !== undefined) { setConfirmForget(false); onNotice('已忘记这张卡片'); } };
  const scan = async () => { await run('device', () => invoke('devices.scan')); onRefresh(); };
  return <div className={`drawer-backdrop ${visible ? 'is-open' : ''}`} onPointerDown={event => { if (event.target === event.currentTarget) onClose(); }}><aside className="device-panel" role="dialog" aria-modal="true" aria-labelledby="device-panel-title"><ScrollArea className="drawer-scroll" viewportClassName="drawer-content"><div className="panel-header"><button className="icon-button" onClick={onClose} aria-label="关闭"><Icon name="back"/></button><div><h2 id="device-panel-title">AI 卡片</h2><p>语音输入与随身显示</p></div></div><section className="device-status"><div className={`device-visual ${device.isConnected ? 'online' : ''}`}><Icon name="device" size={25}/><span/></div><div><span className="eyebrow">连接状态</span><h3>{device.isConnected ? device.pairedDeviceName ?? 'AI 卡片' : device.hasPairedDevice ? '正在等待已绑定卡片' : '尚未连接卡片'}</h3><p>{device.isConnected ? '已连接，可以开始对话' : device.detail}</p></div></section>{device.isConnected && <section className="panel-section"><h3>卡片当前对话</h3><div className="bound-conversation"><Icon name="chat"/><span><b>{active?.title ?? '对话'}</b><small>{device.followDesktop ? '正在跟随电脑' : '卡片独立对话'}</small></span></div><div className="panel-actions">{active && <button className="secondary-button" onClick={() => onOpenConversation(active.id)}>打开</button>}<button className="primary-button" disabled={device.followDesktop || bootstrap.activeConversation?.conversation.mode !== 'chat' || busy} onClick={() => void follow()}>跟随当前对话</button></div></section>}{!device.isConnected && <section className="panel-section"><div className="section-title"><h3>附近的设备</h3><button className="text-button" disabled={busy} onClick={() => void scan()}><Icon name="refresh" size={15}/>{busy ? '扫描中…' : '重新扫描'}</button></div><div className="device-list">{device.devices.length ? device.devices.map(item => <button key={item.address} disabled={busy} onClick={() => void connect(item)}><span><b>{item.name}</b><small>{item.addressText}</small></span><em>{item.rssi} dBm</em><Icon name="chevron" size={16}/></button>) : <div className="device-empty"><Icon name="device"/><span>{deviceDiscoveryMessage(device)}</span></div>}</div></section>}<Diagnostics device={device}/>{device.hasPairedDevice && !confirmForget && <button className="text-danger" onClick={() => setConfirmForget(true)}>忘记这张卡片</button>}{confirmForget && <div className="inline-confirm"><p>忘记后需要重新扫描和绑定。</p><div><button className="text-button" onClick={() => setConfirmForget(false)}>取消</button><button className="danger-button" disabled={busy} onClick={() => void forget()}>确认忘记</button></div></div>}</ScrollArea></aside></div>;
}

function Diagnostics({ device }: { device: BootstrapState['device'] }) {
  const [open, setOpen] = useState(false);
  return <section className={`diagnostics ${open ? 'is-open' : ''}`}>
    <button className="diagnostics-toggle" onClick={() => setOpen(value => !value)} aria-expanded={open} aria-controls="device-diagnostics-content">
      <span>高级诊断</span><Icon name="chevron" size={15}/>
    </button>
    <div className="diagnostics-disclosure" id="device-diagnostics-content" aria-hidden={!open}><div><dl><div><dt>状态</dt><dd>{device.status}</dd></div><div><dt>详情</dt><dd>{device.detail}</dd></div><div><dt>PDU</dt><dd>{device.maxPduSize || '—'}</dd></div></dl></div></div>
  </section>;
}

function GlobalAlert({ text, onClose }: { text: string; onClose(): void }) { return <div className="global-alert" role="alert"><span>{text}</span><button className="icon-button" onClick={onClose} aria-label="关闭错误提示"><Icon name="close"/></button></div>; }
function Notice({ notice }: { notice?: NoticeState }) { return <div className={`notice ${notice ? 'visible' : ''} ${notice?.tone === 'danger' ? 'danger' : ''}`} role="status" aria-live="polite"><Icon name={notice?.tone === 'danger' ? 'info' : 'check'}/><span>{notice?.text ?? ''}</span></div>; }
function Loading({ error }: { error?: string }) { return <div className="loading-screen"><BrandMark large/><h1>小猫助手</h1><p>{error ?? '正在准备…'}</p></div>; }
function groupConversations(items: ConversationSummary[]) { const result = new Map<string, ConversationSummary[]>(), now = new Date(), startToday = new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime(); for (const item of items) { const updated = new Date(item.updatedAt).getTime(), age = startToday - updated, label = updated >= startToday ? '今天' : age < 2 * 86400000 ? '昨天' : age < 7 * 86400000 ? '最近 7 天' : '更早'; result.set(label, [...(result.get(label) ?? []), item]); } return [...result].map(([label, groupItems]) => ({ label, items: groupItems })); }
function safeHttpUrl(value?: string): value is string { if (!value) return false; try { return ['http:', 'https:'].includes(new URL(value).protocol); } catch { return false; } }
function errorText(reason: unknown) { return reason instanceof Error ? reason.message : String(reason); }

createRoot(document.getElementById('root')!).render(<React.StrictMode><App/></React.StrictMode>);
