import React, { memo, useCallback, useEffect, useLayoutEffect, useMemo, useReducer, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { createRoot } from 'react-dom/client';
import ReactMarkdown, { type Components } from 'react-markdown';
import remarkGfm from 'remark-gfm';
import type { BackendEvent, BootstrapState, CatDevice, Citation, ConversationDetail, ConversationMessage, ConversationSummary, PublicSettings, WorkApproval, WorkArtifact, WorkRequest, WorkToolCall } from './shared';
import { citationOrdinal, hidePartialCitationMarker, remarkCitations, stripCitationMarkers } from './citations';
import { hideSplash, showSplashError } from './boot-splash';
import { deviceDiscoveryMessage, initialState, reduceBackendEvent } from './state';
import { assistantCopyTargets, buildWorkTimeline, clampTextareaHeight, greetingForHour, isNearScrollEnd, scrollThumbMetrics, shouldSubmitComposer, summarizeWorkToolGroup, workTimelineCopyActions } from './ui-behavior';
import { textMenuEntries, type TextMenuAction, type TextMenuScene } from './text-context-menu';
import { AnchorRect, AnchoredSurface, rectOf, ScrollArea } from './ui-primitives';
import brandUrl from './assets/brand.png';
import './ui-tokens.css';
import './styles.css';

type IconName = 'new' | 'search' | 'chat' | 'work' | 'device' | 'settings' | 'menu' | 'send'
  | 'copy' | 'cut' | 'paste' | 'selectAll' | 'close' | 'check' | 'back' | 'rename' | 'delete' | 'refresh'
  | 'key' | 'shield' | 'info' | 'chevron' | 'pin' | 'external' | 'attach' | 'stop' | 'file';

const iconPaths: Record<IconName, React.ReactNode> = {
  new: <path d="M12 5v14M5 12h14"/>,
  search: <><circle cx="10.5" cy="10.5" r="6.5"/><path d="m16 16 4 4"/></>,
  chat: <><path d="M5 18.2 3.8 21l3.7-1.2A9 9 0 1 0 5 18.2Z"/><path d="M8 11.5h8M8 15h5"/></>,
  work: <><path d="M4 5.5C4 4.67 4.67 4 5.5 4H18.5C19.33 4 20 4.67 20 5.5V16H4V5.5Z"/><path d="M2 16H22V17C22 18.66 20.66 20 19 20H5C3.34 20 2 18.66 2 17V16Z"/></>,
  device: <><rect x="6" y="3" width="12" height="18" rx="3"/><path d="M10 7h4M10 17h4"/></>,
  settings: <><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.9l.1.1-2.8 2.8-.1-.1a1.7 1.7 0 0 0-1.9-.3 1.7 1.7 0 0 0-1 1.6V21h-4v-.1a1.7 1.7 0 0 0-1-1.6 1.7 1.7 0 0 0-1.9.3l-.1.1L4.2 17l.1-.1a1.7 1.7 0 0 0 .3-1.9A1.7 1.7 0 0 0 3 14v-4a1.7 1.7 0 0 0 1.6-1 1.7 1.7 0 0 0-.3-1.9L4.2 7 7 4.2l.1.1A1.7 1.7 0 0 0 9 4.6 1.7 1.7 0 0 0 10 3h4a1.7 1.7 0 0 0 1 1.6 1.7 1.7 0 0 0 1.9-.3l.1-.1L19.8 7l-.1.1a1.7 1.7 0 0 0-.3 1.9 1.7 1.7 0 0 0 1.6 1v4a1.7 1.7 0 0 0-1.6 1Z"/></>,
  menu: <><circle cx="5" cy="12" r="1"/><circle cx="12" cy="12" r="1"/><circle cx="19" cy="12" r="1"/></>,
  send: <><path d="m4 12 16-8-6.5 16-2-6Z"/><path d="m11.5 14 3-3"/></>,
  copy: <><rect x="8" y="8" width="11" height="11" rx="2"/><path d="M16 8V6a2 2 0 0 0-2-2H6a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h2"/></>,
  cut: <><circle cx="6" cy="6" r="3"/><circle cx="6" cy="18" r="3"/><path d="M8.12 8.12 12 12M20 4 8.12 15.88M14.8 14.8 20 20"/></>,
  paste: <><rect x="8" y="2" width="8" height="4" rx="1"/><path d="M16 4h2a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2h2"/></>,
  selectAll: <><path d="M5 3a2 2 0 0 0-2 2M19 3a2 2 0 0 1 2 2M21 19a2 2 0 0 1-2 2M5 21a2 2 0 0 1-2-2"/><path d="M9 3h1M9 21h1M14 3h1M14 21h1M3 9v1M21 9v1M3 14v1M21 14v1"/></>,
  close: <path d="m6 6 12 12M18 6 6 18"/>,
  check: <path d="m5 12 4 4L19 6"/>,
  back: <path d="m15 18-6-6 6-6"/>,
  rename: <><path d="M12 20h9"/><path d="M16.5 3.5a2.1 2.1 0 0 1 3 3L8 18l-4 1 1-4Z"/></>,
  delete: <><path d="M4 7h16M9 7V4h6v3M7 7l1 13h8l1-13M10 11v5M14 11v5"/></>,
  refresh: <><path d="M20 11a8 8 0 1 0-2.3 5.7"/><path d="M20 4v7h-7"/></>,
  key: <><circle cx="8" cy="15" r="4"/><path d="m11 12 8-8M15 8l2 2M17 6l2 2"/></>,
  shield: <><path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10Z"/><path d="m9 12 2 2 4-4"/></>,
  info: <><circle cx="12" cy="12" r="9"/><path d="M12 11v5M12 8h.01"/></>,
  chevron: <path d="m9 18 6-6-6-6"/>,
  pin: <><path d="M12 17v5"/><path d="M5 17h14"/><path d="M6 3h12l-2 6v4l2 4H6l2-4V9Z"/></>,
  external: <><path d="M14 4h6v6M20 4l-9 9"/><path d="M18 14v4a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h4"/></>,
  attach: <path d="M21.4 11.6 12 21a6 6 0 0 1-8.5-8.5l10-10a4 4 0 1 1 5.7 5.7l-10 10a2 2 0 0 1-2.9-2.8l9.3-9.3"/>,
  stop: <rect x="7" y="7" width="10" height="10" rx="1"/>,
  file: <><path d="M6 3h8l4 4v14H6Z"/><path d="M14 3v5h5"/></>
};

function Icon({ name, size = 18 }: { name: IconName; size?: number }) {
  return <svg className="icon" width={size} height={size} viewBox="0 0 24 24" aria-hidden="true">{iconPaths[name]}</svg>;
}
function invoke<T>(method: string, params?: Record<string, unknown>) { return window.catAssistant.invoke<T>(method, params); }
interface NoticeState { id: number; text: string; tone?: 'default' | 'danger' }
type AppView = 'conversation' | 'compose' | 'settings';
type ActionSurface = { kind: 'rename' | 'delete'; anchor: AnchorRect; conversation: ConversationSummary } | undefined;
type ConversationContext = { conversation: ConversationSummary; left: number; top: number } | undefined;
type TextMenuField = HTMLInputElement | HTMLTextAreaElement;
type TextMenuState = {
  x: number; y: number; scene: TextMenuScene; scope: HTMLElement; field?: TextMenuField;
  selectedText: string; hasSelection: boolean; selectionStart: number | null; selectionEnd: number | null;
} | undefined;

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
  const [requestedView, setRequestedView] = useState<AppView>('conversation');
  const { displayed: displayedView, phase: viewPhase } = useViewTransition(requestedView);
  const [draftMode, setDraftMode] = useState<'chat' | 'work'>('chat');
  const [actionSurface, setActionSurface] = useState<ActionSurface>();
  const [conversationContext, setConversationContext] = useState<ConversationContext>();
  const [textMenu, setTextMenu] = useState<TextMenuState>();
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
    if (state.bootstrap) { hideSplash(); return; }
    const error = localError ?? state.backendError;
    if (error) showSplashError(error);
  }, [state.bootstrap, localError, state.backendError]);
  useEffect(() => {
    if (!notice) return;
    const timer = setTimeout(() => setNotice(current => current?.id === notice.id ? undefined : current), 2600);
    return () => clearTimeout(timer);
  }, [notice]);
  useEffect(() => {
    const openTextMenu = (event: MouseEvent) => {
      if (event.defaultPrevented) return;
      const target = event.target instanceof Element ? event.target : undefined;
      if (!target || target.closest('[role="menu"], .text-context-menu')) return;
      const field = target.closest<TextMenuField>('textarea, input:not([type]), input[type="text"], input[type="password"], input[type="search"], input[type="email"], input[type="url"], input[type="tel"]');
      const scope = target.closest<HTMLElement>('.message-body');
      if (field) {
        const start = field.selectionStart, end = field.selectionEnd;
        const selectedText = start === null || end === null ? '' : field.value.slice(start, end);
        event.preventDefault();
        setTextMenu({ x: event.clientX, y: event.clientY, scene: 'editable', field, scope: field, selectedText, hasSelection: selectedText.length > 0, selectionStart: start, selectionEnd: end });
      } else if (scope) {
        const selection = window.getSelection();
        const selectedText = selection?.rangeCount && selection.anchorNode && scope.contains(selection.anchorNode) ? selection.toString() : '';
        event.preventDefault();
        setTextMenu({ x: event.clientX, y: event.clientY, scene: selectedText ? 'selection' : 'message', scope, selectedText, hasSelection: Boolean(selectedText), selectionStart: null, selectionEnd: null });
      }
    };
    document.addEventListener('contextmenu', openTextMenu);
    return () => document.removeEventListener('contextmenu', openTextMenu);
  }, []);
  const applyTextAction = async (action: TextMenuAction) => {
    const menu = textMenu;
    if (!menu) return;
    setTextMenu(undefined);
    if (!menu.field) {
      if (action === 'copy') { try { await navigator.clipboard.writeText(menu.selectedText); } catch { /* clipboard unavailable */ } return; }
      const range = document.createRange();
      range.selectNodeContents(menu.scope);
      const selection = window.getSelection();
      selection?.removeAllRanges();
      selection?.addRange(range);
      return;
    }
    const field = menu.field;
    field.focus({ preventScroll: true });
    if (menu.selectionStart !== null && menu.selectionEnd !== null) field.setSelectionRange(menu.selectionStart, menu.selectionEnd);
    if (action === 'selectAll') { field.select(); return; }
    if (action === 'paste') {
      try { await window.catAssistant.paste(); }
      catch {
        try {
          const text = await navigator.clipboard.readText();
          document.execCommand('insertText', false, text);
        } catch { /* clipboard unavailable */ }
      }
      return;
    }
    document.execCommand(action);
  };

  if (!state.bootstrap) return null;
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
  const attachFiles = async () => {
    const files = await window.catAssistant.selectWorkFiles();
    if (!files.length) return;
    let conversationId = active?.conversation.mode === 'work' && requestedView !== 'compose' ? active.conversation.id : undefined;
    if (!conversationId)
    {
      const created = await run('attach', () => invoke<ConversationSummary>('conversations.create', { mode: 'work' }));
      if (!created) return;
      conversationId = created.id;
    }
    const id = conversationId;
    const copied = await run('attach', async () => {
      for (const sourcePath of files) await invoke('work.inputs.add', { id, sourcePath });
      return invoke<ConversationDetail>('conversations.get', { id });
    });
    if (copied) { dispatch({ event: 'ui.conversation', data: copied }); setRequestedView('conversation'); showNotice(`已添加 ${files.length} 个文件`); }
  };
  const stopWork = async () => {
    if (!active?.activeRunId) return;
    const detail = await run('stop', () => invoke<ConversationDetail>('conversations.cancel', { id: active.conversation.id }));
    if (detail) dispatch({ event: 'ui.conversation', data: detail });
  };
  const resolveApproval = async (approval: WorkApproval, approved: boolean) => {
    const result = await run(`approval-${approval.id}`, () => invoke('work.approvals.resolve', {
      id: approval.conversationId, approvalId: approval.id, approved
    }));
    if (result !== undefined) await refresh();
  };
  const resolveWorkRequest = async (request: WorkRequest, approved: boolean) => {
    const result = await run<WorkRequest>(`work-request-${request.id}`, () => invoke('work.requests.resolve', {
      requestId: request.id, approved
    }));
    if (!result) return;
    await refresh();
    showNotice(approved ? result.state === 'started' ? 'Work 任务已在后台开始' : '正在创建 Work 任务' : '已拒绝 Work 任务');
  };
  const rename = async (title: string) => {
    const target = actionSurface?.conversation;
    if (!target || title === target.title) { setActionSurface(undefined); return; }
    const result = await run('rename', () => invoke('conversations.rename', { id: target.id, title }));
    if (result === undefined) return;
    setActionSurface(undefined); await refresh(); showNotice('对话名称已更新');
  };
  const remove = async () => {
    const target = actionSurface?.conversation;
    if (!target) return;
    const result = await run('delete', () => invoke('conversations.delete', { id: target.id }));
    if (result === undefined) return;
    setActionSurface(undefined); await refresh(); showNotice('对话已删除');
  };
  const setPinned = async (conversation: ConversationSummary) => {
    setConversationContext(undefined);
    const result = await run('pin', () => invoke('conversations.pin', { id: conversation.id, pinned: !conversation.isPinned }));
    if (result === undefined) return;
    await refresh(); showNotice(conversation.isPinned ? '已取消置顶' : '对话已置顶');
  };
  const saveSettings = async (patch: Partial<PublicSettings> & { apiKey?: string; searchApiKey?: string }, successText: string) => {
    const current = bootstrap.settings;
    const result = await run('settings', () => invoke<PublicSettings>('settings.save', {
      apiKey: patch.apiKey || undefined,
      searchApiKey: patch.searchApiKey || undefined,
      asrModel: patch.asrModel ?? current.asrModel,
      chatModel: patch.chatModel ?? current.chatModel,
      workModel: patch.workModel ?? current.workModel,
      screenTimeoutSeconds: patch.screenTimeoutSeconds ?? current.screenTimeoutSeconds,
      autoReconnect: patch.autoReconnect ?? current.autoReconnect,
      webSearchEnabled: patch.webSearchEnabled ?? current.webSearchEnabled
    }));
    if (!result) return false;
    await refresh(); showNotice(successText); return true;
  };

  return <>
    <div className="app-frame">
      <Sidebar bootstrap={bootstrap} query={query} onQuery={setQuery} onNew={startCompose}
        onSelect={id => void selectConversation(id)} onDevice={() => setDeviceOpen(true)}
        onContext={(conversation, left, top) => setConversationContext({ conversation, left, top })}
        composeActive={requestedView === 'compose'}
        settingsActive={requestedView === 'settings'} onSettings={() => setRequestedView('settings')}/>
      <div className="titlebar" aria-hidden="true"/>
      <main className={`content-pane view-${displayedView} view-phase-${viewPhase}`}>
        {(localError || state.backendError) && <GlobalAlert
          text={localError ?? state.backendError!}
          onClose={() => { setLocalError(undefined); dispatch({ event: 'ui.clearError' }); }}/>
        }
        {displayedView === 'settings'
          ? <SettingsPage settings={bootstrap.settings} onboarding={!bootstrap.settings.hasApiKey} busy={isBusy('settings')} onBack={() => setRequestedView('conversation')} onSave={saveSettings}/>
          : displayedView === 'compose' || !active
            ? <DraftConversation mode={draftMode} onMode={setDraftMode} value={draft} onChange={setDraft} onSend={() => void send()} onAttach={() => void attachFiles()} hasApiKey={bootstrap.settings.hasApiKey} sending={isBusy('send')} focusSignal={composerFocusSignal} onOpenSettings={() => setRequestedView('settings')}/>
            : <>
              <ConversationHeader conversation={active.conversation} origin={active.conversation.originConversationId ? bootstrap.conversations.find(item => item.id === active.conversation.originConversationId) : undefined} onOpenConversation={id => void selectConversation(id)} onAction={(kind, anchor) => setActionSurface({ kind, anchor, conversation: active.conversation })}/>
              <MessageList detail={active} runError={state.runError} busy={busy} tools={active.conversation.mode === 'work' ? bootstrap.workToolCalls ?? noWorkToolCalls : noWorkToolCalls} artifacts={active.conversation.mode === 'work' ? bootstrap.workArtifacts ?? noWorkArtifacts : noWorkArtifacts} onReveal={artifactId => void window.catAssistant.revealArtifact(active.conversation.id, artifactId)} onResolveWorkRequest={(request, approved) => void resolveWorkRequest(request, approved)} onOpenConversation={id => void selectConversation(id)}/>
              {active.conversation.mode === 'work' && (bootstrap.pendingApprovals?.length ?? 0) > 0
                ? <ApprovalComposer approvals={bootstrap.pendingApprovals!} busy={busy} onResolve={(approval, approved) => void resolveApproval(approval, approved)} onStop={active.activeRunId ? () => void stopWork() : undefined}/>
                : <Composer value={draft} onChange={setDraft} onSend={() => void send()} onAttach={active.conversation.mode === 'work' ? () => void attachFiles() : undefined} onStop={active.conversation.mode === 'work' && active.activeRunId ? () => void stopWork() : undefined} hasApiKey={bootstrap.settings.hasApiKey} sending={isBusy('send')} focusSignal={composerFocusSignal} onOpenSettings={() => setRequestedView('settings')}/>}
            </>
        }
      </main>
    </div>
    <ConversationContextMenu value={conversationContext} onClose={() => setConversationContext(undefined)} onPin={conversation => void setPinned(conversation)} onAction={(kind, anchor, conversation) => { setConversationContext(undefined); setActionSurface({ kind, anchor, conversation }); }}/>
    <TextContextMenu value={textMenu} onClose={() => setTextMenu(undefined)} onAction={action => void applyTextAction(action)}/>
    <RenameDialog open={actionSurface?.kind === 'rename'} anchor={actionSurface?.anchor} conversation={actionSurface?.conversation} busy={isBusy('rename')} onClose={() => setActionSurface(undefined)} onRename={title => void rename(title)}/>
    <DeleteDialog open={actionSurface?.kind === 'delete'} anchor={actionSurface?.anchor} conversation={actionSurface?.conversation} busy={isBusy('delete')} onClose={() => setActionSurface(undefined)} onConfirm={() => void remove()}/>
    <DevicePanel open={deviceOpen} bootstrap={bootstrap} busy={isBusy('device')} run={run} onClose={() => setDeviceOpen(false)} onOpenConversation={id => { setDeviceOpen(false); void selectConversation(id); }} onRefresh={() => void refresh()} onNotice={showNotice}/>
    <Notice notice={notice}/>
  </>;
}

function BrandMark({ large = false }: { large?: boolean }) {
  return <img className={`brand-mark ${large ? 'large' : ''}`} src={brandUrl} alt="" aria-hidden="true"/>;
}

function Sidebar({ bootstrap, query, onQuery, onNew, onSelect, onDevice, onContext, composeActive, settingsActive, onSettings }: {
  bootstrap: BootstrapState; query: string; onQuery(value: string): void;
  onNew(): void; onSelect(id: string): void; onDevice(): void; composeActive: boolean; settingsActive: boolean; onSettings(): void;
  onContext(conversation: ConversationSummary, left: number, top: number): void;
}) {
  const filtered = useMemo(() => bootstrap.conversations.filter(item => item.title.toLocaleLowerCase().includes(query.toLocaleLowerCase())), [bootstrap.conversations, query]);
  const groups = groupConversations(filtered);
  const activeDeviceConversation = bootstrap.conversations.find(item => item.id === bootstrap.device.activeConversationId);
  return <aside className="sidebar" aria-label="会话导航">
    <div className="brand-row"><BrandMark/><strong className="brand-name">小猫助手</strong></div>
    <button className={`new-conversation ${composeActive ? 'active' : ''}`} onClick={onNew} aria-label="新建对话" aria-current={composeActive ? 'true' : undefined}><Icon name="new"/><span>新对话</span></button>
    <label className="search-box"><Icon name="search"/><input value={query} onChange={event => onQuery(event.target.value)} placeholder="搜索对话" aria-label="搜索对话"/></label>
    <ScrollArea className="conversation-scroll" viewportClassName="conversation-list">
      {groups.map(group => <section className="conversation-group" key={group.label} aria-label={group.label}><h2>{group.label}</h2>{group.items.map(item => <button key={item.id} className={`conversation-item ${!settingsActive && !composeActive && bootstrap.activeConversationId === item.id ? 'active' : ''}`} onClick={() => onSelect(item.id)} onContextMenu={event => { event.preventDefault(); const rect = event.currentTarget.getBoundingClientRect(); onContext(item, event.clientX || rect.right, event.clientY || rect.top); }} aria-current={!settingsActive && !composeActive && bootstrap.activeConversationId === item.id ? 'page' : undefined} aria-label={`${item.title}${item.isPinned ? '，已置顶' : ''}`} title={item.title}><Icon name={item.mode === 'work' ? 'work' : 'chat'} size={16}/><span>{item.title}</span></button>)}</section>)}
      {filtered.length === 0 && <div className="no-results">{query ? '没有匹配的对话' : '还没有对话'}</div>}
    </ScrollArea>
    <div className="sidebar-footer">
      <button className="footer-entry" onClick={onDevice}><span className={`status-dot ${bootstrap.device.isConnected ? 'online' : ''}`}/><Icon name="device"/><span className="footer-copy"><b>{bootstrap.device.isConnected ? 'AI 卡片' : '连接 AI 卡片'}</b><small>{bootstrap.device.isConnected ? activeDeviceConversation?.title ?? '已连接' : '可选辅助硬件'}</small></span></button>
      <button className={`footer-entry ${settingsActive ? 'active' : ''}`} onClick={onSettings}><Icon name="settings"/><span className="footer-copy"><b>设置</b><small>{bootstrap.settings.hasApiKey ? '服务已配置' : '需要配置 API Key'}</small></span></button>
    </div>
  </aside>;
}

const textMenuIcons: Record<TextMenuAction, IconName> = { cut: 'cut', copy: 'copy', paste: 'paste', selectAll: 'selectAll' };

function TextContextMenu({ value, onClose, onAction }: {
  value: TextMenuState; onClose(): void; onAction(action: TextMenuAction): void;
}) {
  const menu = useRef<HTMLDivElement>(null);
  const entries = useMemo(() => value ? textMenuEntries(value.scene, value.hasSelection) : [], [value?.scene, value?.hasSelection]);
  const [active, setActive] = useState(-1);
  useLayoutEffect(() => {
    setActive(-1);
    if (!value || !menu.current) return;
    const box = menu.current.getBoundingClientRect();
    menu.current.style.left = `${Math.max(8, Math.min(value.x, window.innerWidth - box.width - 8))}px`;
    menu.current.style.top = `${Math.max(8, Math.min(value.y, window.innerHeight - box.height - 8))}px`;
  }, [value]);
  useEffect(() => {
    if (!value) return;
    const closeOnPointer = (event: PointerEvent) => { if (!menu.current?.contains(event.target as Node)) onClose(); };
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); onClose(); return; }
      if (event.key === 'Enter' || event.key === ' ') {
        event.preventDefault(); event.stopPropagation();
        const entry = entries[active];
        if (entry) onAction(entry.id);
        return;
      }
      if (!['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key) || !entries.length) return;
      event.preventDefault(); event.stopPropagation();
      const step = event.key === 'ArrowUp' ? -1 : 1;
      setActive(previous => event.key === 'Home' ? 0 : event.key === 'End' ? entries.length - 1
        : previous < 0 ? (event.key === 'ArrowUp' ? entries.length - 1 : 0)
        : (previous + step + entries.length) % entries.length);
    };
    window.addEventListener('resize', onClose); document.addEventListener('pointerdown', closeOnPointer); document.addEventListener('keydown', onKey, true);
    return () => { window.removeEventListener('resize', onClose); document.removeEventListener('pointerdown', closeOnPointer); document.removeEventListener('keydown', onKey, true); };
  }, [value, onClose, onAction, entries, active]);
  if (!value) return null;
  return <div ref={menu} className="text-context-menu" role="menu" aria-label="文字操作" style={{ left: value.x, top: value.y }}>
    {entries.map((entry, index) => <button key={entry.id} role="menuitem" tabIndex={-1} className={index === active ? 'is-active' : ''} onMouseDown={event => event.preventDefault()} onClick={() => onAction(entry.id)}><Icon name={textMenuIcons[entry.id]} size={15}/>{entry.label}</button>)}
  </div>;
}

function ConversationContextMenu({ value, onClose, onPin, onAction }: {
  value: ConversationContext; onClose(): void; onPin(conversation: ConversationSummary): void;
  onAction(kind: 'rename' | 'delete', anchor: AnchorRect, conversation: ConversationSummary): void;
}) {
  const menu = useRef<HTMLDivElement>(null);
  const firstItem = useRef<HTMLButtonElement>(null);
  useLayoutEffect(() => {
    if (!value || !menu.current) return;
    const box = menu.current.getBoundingClientRect();
    menu.current.style.left = `${Math.max(8, Math.min(value.left, window.innerWidth - box.width - 8))}px`;
    menu.current.style.top = `${Math.max(8, Math.min(value.top, window.innerHeight - box.height - 8))}px`;
    firstItem.current?.focus({ preventScroll: true });
  }, [value]);
  useEffect(() => {
    if (!value) return;
    const closeOnPointer = (event: PointerEvent) => { if (!menu.current?.contains(event.target as Node)) onClose(); };
    const closeOnKey = (event: KeyboardEvent) => { if (event.key === 'Escape') onClose(); };
    window.addEventListener('resize', onClose); document.addEventListener('pointerdown', closeOnPointer); document.addEventListener('keydown', closeOnKey);
    return () => { window.removeEventListener('resize', onClose); document.removeEventListener('pointerdown', closeOnPointer); document.removeEventListener('keydown', closeOnKey); };
  }, [value, onClose]);
  if (!value) return null;
  const choose = (kind: 'rename' | 'delete', element: HTMLButtonElement) => onAction(kind, rectOf(element), value.conversation);
  return <div ref={menu} className="conversation-context-menu" role="menu" aria-label="对话操作" style={{ left: value.left, top: value.top }}>
    <button ref={firstItem} role="menuitem" onClick={() => onPin(value.conversation)}><Icon name="pin"/>{value.conversation.isPinned ? '取消置顶' : '置顶'}</button>
    <button role="menuitem" onClick={event => choose('rename', event.currentTarget)}><Icon name="rename"/>重命名</button>
    <button role="menuitem" className="danger" onClick={event => choose('delete', event.currentTarget)}><Icon name="delete"/>删除</button>
  </div>;
}

function ConversationHeader({ conversation, origin, onOpenConversation, onAction }: {
  conversation: ConversationSummary; origin?: ConversationSummary; onOpenConversation(id: string): void;
  onAction(kind: 'rename' | 'delete', anchor: AnchorRect): void;
}) {
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
  return <header className="conversation-header"><div className="conversation-heading"><h1>{conversation.title}</h1>{origin && <button className="origin-link" onClick={() => onOpenConversation(origin.id)} title={`返回来源 Chat：${origin.title}`}><Icon name="back" size={13}/>来源：{origin.title}</button>}</div><div className="header-menu" ref={wrapper}><button ref={trigger} className="icon-button" onClick={toggleMenu} aria-label="对话选项" aria-haspopup="menu" aria-expanded={menuOpen}><Icon name="menu"/></button>{menuRendered && <div className={`popover-menu ${menuOpen ? 'is-open' : 'is-closing'}`} role="menu" aria-hidden={!menuOpen} inert={!menuOpen}><button ref={firstItem} role="menuitem" onClick={event => choose('rename', event.currentTarget)}><Icon name="rename"/>重命名</button><button role="menuitem" className="danger" onClick={event => choose('delete', event.currentTarget)}><Icon name="delete"/>删除</button></div>}</div></header>;
}

function toolStateLabel(tool: WorkToolCall): string {
  if (tool.state === 'failed' && tool.errorCode === 'permission_rejected') return '未执行';
  return ({ pending: '等待中', waiting_approval: '等待批准', running: '执行中', completed: '已完成', failed: '失败', cancelled: '已取消', rejected: '未执行' } as Record<string, string>)[tool.state] ?? tool.state;
}

function toolDisplayName(name: string): string {
  return ({ execute_command: '运行命令', read_file: '读取文件', write_file: '写入文件', list_directory: '查看文件夹', web_search: '搜索网络', deliver_files: '交付成果', load_skill: '读取技能' } as Record<string, string>)[name] ?? name;
}

function ToolGroup({ tools }: { tools: WorkToolCall[] }) {
  const summary = summarizeWorkToolGroup(tools);
  return <details className={`timeline-tool-group ${summary.isActive ? 'is-running' : ''}`}>
    <summary><span className="timeline-disclosure"><Icon name="chevron" size={14}/></span><strong>{summary.label}</strong></summary>
    <div className="timeline-tool-list">{tools.map(tool => <details className="timeline-tool" key={tool.id}>
      <summary><span className="timeline-disclosure"><Icon name="chevron" size={13}/></span><span>{toolDisplayName(tool.toolName)}</span><em className={`tool-state state-${tool.errorCode === 'permission_rejected' ? 'rejected' : tool.state}`}>{toolStateLabel(tool)}</em>{tool.durationMilliseconds !== undefined && <small>{formatDuration(tool.durationMilliseconds)}</small>}</summary>
      <div className="timeline-tool-detail"><b>工具</b><code>{tool.toolName}</code><b>参数</b><pre>{maskSensitiveJson(tool.argumentsJson)}</pre>{tool.resultJson && <><b>结果</b><pre>{maskSensitiveJson(tool.resultJson)}</pre></>}{tool.errorMessage && <p className="tool-error">{tool.errorMessage}</p>}</div>
    </details>)}</div>
  </details>;
}

function ArtifactGroup({ artifacts, onReveal }: { artifacts: WorkArtifact[]; onReveal(id: string): void }) {
  return <section className="timeline-artifacts" aria-label={artifacts.length === 1 ? '交付成果' : `交付 ${artifacts.length} 个成果`}>
    {artifacts.length > 1 && <span className="artifact-group-label">本次生成了 {artifacts.length} 个文件</span>}
    <div>{artifacts.map(artifact => <button key={artifact.id} onClick={() => onReveal(artifact.id)} title={artifact.label}><span className="artifact-file-icon"><Icon name="file"/></span><span><b>{artifact.label}</b><small>在文件资源管理器中显示</small></span><Icon name="external" size={14}/></button>)}</div>
  </section>;
}

function formatDuration(milliseconds: number): string {
  return milliseconds < 1000 ? `${milliseconds} ms` : `${(milliseconds / 1000).toFixed(milliseconds < 10000 ? 1 : 0)} s`;
}

function maskSensitiveJson(value: string): string {
  try {
    const redact = (item: unknown): unknown => {
      if (Array.isArray(item)) return item.map(redact);
      if (item && typeof item === 'object') return Object.fromEntries(Object.entries(item).map(([key, child]) => [key,
        /(?:api.?key|token|password|secret|authorization)/i.test(key) ? '••••••'
          : /(?:content|body|binary|base64)/i.test(key) && typeof child === 'string' ? `[内容已隐藏，${child.length} 字符]`
          : redact(child)]));
      return item;
    };
    return JSON.stringify(redact(JSON.parse(value)), null, 2);
  } catch { return value.length > 2000 ? `${value.slice(0, 2000)}…` : value; }
}

const noCitations: Citation[] = [];
const noWorkToolCalls: WorkToolCall[] = [];
const noWorkArtifacts: WorkArtifact[] = [];

function MessageList({ detail, runError, busy, tools, artifacts, onReveal, onResolveWorkRequest, onOpenConversation }: {
  detail: ConversationDetail; runError?: string; busy: Set<string>;
  tools: WorkToolCall[]; artifacts: WorkArtifact[]; onReveal(id: string): void;
  onResolveWorkRequest(request: WorkRequest, approved: boolean): void; onOpenConversation(id: string): void;
}) {
  const scroller = useRef<HTMLDivElement>(null);
  const pinned = useRef(true);
  const lastContent = detail.messages.at(-1)?.content;
  const citationsByMessage = useMemo(() => {
    const grouped = new Map<string, Citation[]>();
    for (const citation of detail.citations) grouped.set(citation.messageId, [...(grouped.get(citation.messageId) ?? []), citation]);
    return grouped;
  }, [detail.citations]);
  const requestsByMessage = useMemo(() => {
    const grouped = new Map<string, WorkRequest[]>();
    for (const request of detail.workRequests ?? []) {
      if (request.sourceConversationId !== detail.conversation.id) continue;
      grouped.set(request.sourceMessageId, [...(grouped.get(request.sourceMessageId) ?? []), request]);
    }
    return grouped;
  }, [detail.conversation.id, detail.workRequests]);
  const timeline = useMemo(() => detail.conversation.mode === 'work' ? buildWorkTimeline(detail.messages, tools, artifacts) : undefined, [detail.conversation.mode, detail.messages, tools, artifacts]);
  const copyTargets = useMemo(() => assistantCopyTargets(detail.messages, detail.activeRunId), [detail.messages, detail.activeRunId]);
  const timelineCopyActions = useMemo(() => timeline ? workTimelineCopyActions(timeline, detail.messages, detail.activeRunId) : new Map<string, string>(), [timeline, detail.messages, detail.activeRunId]);
  const lastTimelineKey = timeline?.at(-1)?.key;
  const showRunning = Boolean(detail.activeRunId);
  useEffect(() => { pinned.current = true; requestAnimationFrame(() => scroller.current?.scrollTo({ top: scroller.current.scrollHeight })); }, [detail.conversation.id]);
  useEffect(() => { if (pinned.current) requestAnimationFrame(() => scroller.current?.scrollTo({ top: scroller.current.scrollHeight })); }, [detail.messages.length, tools.length, artifacts.length, lastContent, showRunning, runError]);
  const onScroll = () => { const element = scroller.current; if (element) pinned.current = isNearScrollEnd(element.scrollHeight, element.scrollTop, element.clientHeight); };
  const renderMessage = (message: ConversationMessage, showCopy = true) => <React.Fragment key={message.id}><MessageItem message={message} citations={citationsByMessage.get(message.id) ?? noCitations} copyContent={showCopy ? copyTargets.get(message.id) : undefined}/>{(requestsByMessage.get(message.id) ?? []).map(request => <WorkRequestCard key={request.id} request={request} busy={busy.has(`work-request-${request.id}`)} onResolve={onResolveWorkRequest} onOpenConversation={onOpenConversation}/>)}</React.Fragment>;
  return <ScrollArea ref={scroller} className="message-scroll" viewportClassName={`messages ${detail.messages.length ? '' : 'empty-chat'}`} onScroll={onScroll}>
    {detail.messages.length === 0
      ? <div className="empty-greeting"><BrandMark large/><h2>{greetingForHour(new Date().getHours())}</h2></div>
      : <div className="message-column">
        {timeline ? timeline.map(item => <React.Fragment key={item.key}>
          {item.kind === 'message' ? renderMessage(item.message, false) : item.kind === 'tools' ? <ToolGroup tools={item.tools}/> : <ArtifactGroup artifacts={item.artifacts} onReveal={onReveal}/>}
          {item.key === lastTimelineKey && runError && <div className="run-error" role="alert"><Icon name="info"/><span>{runError}</span></div>}
          {timelineCopyActions.has(item.key) && <TimelineCopyAction content={timelineCopyActions.get(item.key)!}/>}
        </React.Fragment>) : detail.messages.map(message => renderMessage(message))}
        {showRunning && <ThinkingBubble/>}
        {!timeline && runError && <div className="run-error" role="alert"><Icon name="info"/><span>{runError}</span></div>}
      </div>}
  </ScrollArea>;
}

function WorkRequestCard({ request, busy, onResolve, onOpenConversation }: {
  request: WorkRequest; busy: boolean; onResolve(request: WorkRequest, approved: boolean): void; onOpenConversation(id: string): void;
}) {
  const stateLabel = ({ proposed: '等待批准', approved: '正在创建', rejected: '已拒绝', started: '已开始', failed: '创建失败' } as const)[request.state];
  // The backend stores an empty prompt when the model's proposal could not be parsed at all.
  const retryable = request.prompt.trim().length > 0;
  return <section className={`work-request-card state-${request.state}`} aria-label={`Work 任务：${request.title}`}>
    <div className="work-request-heading"><span className="work-request-icon"><Icon name="work"/></span><span><small>Chat 建议交给 Work</small><b>{request.title}</b></span><em>{stateLabel}</em></div>
    {retryable ? <p>{request.prompt}</p> : <p className="work-request-hint">模型没有给出可执行的任务内容，无法创建 Work 对话。可以让它重新提议一次。</p>}
    {retryable && <details><summary>查看传递的背景摘要</summary><div>{request.contextSummary}</div></details>}
    {request.errorMessage && <div className="work-request-error" role="alert">{request.errorMessage}</div>}
    <div className="work-request-actions">
      {request.state === 'proposed' && <><button className="text-button" disabled={busy} onClick={() => onResolve(request, false)}>拒绝</button><button className="primary-button" disabled={busy} onClick={() => onResolve(request, true)}>{busy ? '正在创建…' : '批准并开始'}</button></>}
      {request.state === 'failed' && retryable && <button className="primary-button" disabled={busy} onClick={() => onResolve(request, true)}>{busy ? '正在重试…' : '重试创建'}</button>}
      {request.state === 'started' && request.targetConversationId && <button className="primary-button" onClick={() => onOpenConversation(request.targetConversationId!)}>打开 Work<Icon name="chevron" size={14}/></button>}
    </div>
  </section>;
}

function ThinkingBubble() {
  return <article className="message assistant"><div className="thinking" role="status"><i/><i/><i/><span className="sr-only">任务进行中</span></div></article>;
}

function TimelineCopyAction({ content }: { content: string }) {
  const [copyState, setCopyState] = useState<'idle' | 'copied' | 'failed'>('idle');
  const copyTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  useEffect(() => () => clearTimeout(copyTimer.current), []);
  const copy = async () => {
    clearTimeout(copyTimer.current);
    try { await navigator.clipboard.writeText(stripCitationMarkers(content)); setCopyState('copied'); }
    catch { setCopyState('failed'); }
    copyTimer.current = setTimeout(() => setCopyState('idle'), 1650);
  };
  return <div className="timeline-actions"><button onClick={() => void copy()} aria-label="复制本轮回复"><Icon name={copyState === 'copied' ? 'check' : 'copy'} size={15}/>{copyState === 'copied' ? '已复制' : copyState === 'failed' ? '复制失败' : '复制'}</button></div>;
}

const MessageItem = memo(function MessageItem({ message, citations, copyContent }: { message: ConversationMessage; citations: ConversationDetail['citations']; copyContent?: string }) {
  const [copyState, setCopyState] = useState<'idle' | 'copied' | 'failed'>('idle');
  const copyTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  useEffect(() => () => clearTimeout(copyTimer.current), []);
  const [sourcesOpen, setSourcesOpen] = useState(false);
  const [sourceTarget, setSourceTarget] = useState<SourceTarget>();
  const sourcesOpenRef = useRef(sourcesOpen);
  sourcesOpenRef.current = sourcesOpen;
  const revealSource = useCallback((ordinal: number) => {
    setSourceTarget({ ordinal, nonce: Date.now(), delay: sourcesOpenRef.current ? 0 : 300 });
    setSourcesOpen(true);
  }, []);
  const copy = async () => { clearTimeout(copyTimer.current); try { await navigator.clipboard.writeText(stripCitationMarkers(copyContent ?? message.content)); setCopyState('copied'); } catch { setCopyState('failed'); } copyTimer.current = setTimeout(() => setCopyState('idle'), 1650); };
  if (message.role === 'assistant' && message.status === 'streaming' && !message.content) return null;
  const safeCitations = citations.filter(citation => safeHttpUrl(citation.url));
  const showStatus = message.status === 'interrupted' || message.status === 'failed';
  return <article className={`message ${message.role} ${message.status}`}><div className="message-body">{message.role === 'assistant' ? <AssistantMarkdown content={message.content} streaming={message.status === 'streaming'} citations={safeCitations} onCite={revealSource}/> : <p>{message.content}</p>}</div>{safeCitations.length > 0 && <SourceList messageId={message.id} citations={safeCitations} open={sourcesOpen} onToggle={() => setSourcesOpen(value => !value)} target={sourceTarget}/>}{message.role === 'assistant' && message.content && (copyContent || showStatus) && <div className="message-actions">{message.status === 'interrupted' && <span>已中断</span>}{message.status === 'failed' && <span className="failed-label">生成失败</span>}{copyContent && <button onClick={() => void copy()} aria-label="复制本轮回复"><Icon name={copyState === 'copied' ? 'check' : 'copy'} size={15}/>{copyState === 'copied' ? '已复制' : copyState === 'failed' ? '复制失败' : '复制'}</button>}</div>}</article>;
});

const markdownPlugins = [remarkGfm, remarkCitations];

const codeLanguageLabels: Record<string, string> = {
  c: 'C', cpp: 'C++', 'c++': 'C++', csharp: 'C#', 'c#': 'C#', cs: 'C#',
  js: 'JavaScript', javascript: 'JavaScript', jsx: 'JSX', ts: 'TypeScript', typescript: 'TypeScript', tsx: 'TSX',
  py: 'Python', python: 'Python', sh: 'Shell', bash: 'Shell', shell: 'Shell', zsh: 'Shell',
  ps1: 'PowerShell', powershell: 'PowerShell', json: 'JSON', yaml: 'YAML', yml: 'YAML', xml: 'XML',
  html: 'HTML', css: 'CSS', sql: 'SQL', go: 'Go', rust: 'Rust', java: 'Java', kotlin: 'Kotlin',
  swift: 'Swift', lua: 'Lua', ruby: 'Ruby', php: 'PHP', dart: 'Dart', diff: 'Diff',
  md: 'Markdown', markdown: 'Markdown', text: '纯文本', txt: '纯文本', plaintext: '纯文本'
};

function codeText(node: React.ReactNode): string {
  if (typeof node === 'string' || typeof node === 'number') return String(node);
  if (Array.isArray(node)) return node.map(codeText).join('');
  if (React.isValidElement<{ children?: React.ReactNode }>(node)) return codeText(node.props.children);
  return '';
}

function codeLanguageLabel(raw: string | undefined): string {
  if (!raw) return '代码';
  return codeLanguageLabels[raw.toLowerCase()] ?? raw.charAt(0).toUpperCase() + raw.slice(1);
}

function CodeBlock({ language, children }: { language?: string; children: React.ReactNode }) {
  const [copyState, setCopyState] = useState<'idle' | 'copied' | 'failed'>('idle');
  const copyTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  useEffect(() => () => clearTimeout(copyTimer.current), []);
  const code = codeText(children).replace(/\n$/, '');
  const copy = async () => {
    clearTimeout(copyTimer.current);
    try {
      await navigator.clipboard.writeText(code);
      setCopyState('copied');
    } catch {
      setCopyState('failed');
    }
    copyTimer.current = setTimeout(() => setCopyState('idle'), 1650);
  };
  return <div className="code-block">
    {code.trim() !== '' && <div className="code-head">
      <span className="code-lang">{codeLanguageLabel(language)}</span>
      <button type="button" className="code-copy" onClick={() => void copy()} aria-label="复制代码">
        <Icon name={copyState === 'copied' ? 'check' : 'copy'} size={13}/>
        {copyState === 'copied' ? '已复制' : copyState === 'failed' ? '复制失败' : '复制'}
      </button>
    </div>}
    <div className="code-scroll"><pre>{children}</pre></div>
  </div>;
}

function AssistantMarkdown({ content, streaming, citations, onCite }: { content: string; streaming: boolean; citations: Citation[]; onCite(ordinal: number): void }) {
  const components = useMemo<Components>(() => {
    const byOrdinal = new Map(citations.map(citation => [citation.ordinal, citation]));
    return {
      pre: ({ children }) => {
        const child = React.Children.toArray(children)[0];
        if (!React.isValidElement<{ className?: string }>(child)) return <pre>{children}</pre>;
        const language = /language-([\w+#-]+)/.exec(child.props.className ?? '')?.[1];
        return <CodeBlock language={language}>{child}</CodeBlock>;
      },
      a: ({ href, children }) => {
        const ordinal = citationOrdinal(href);
        if (ordinal === undefined) return safeHttpUrl(href) ? <a href={href} target="_blank" rel="noreferrer">{children}</a> : <span>{children}</span>;
        const citation = byOrdinal.get(ordinal);
        if (citation) return <span className="citation-ref"><button type="button" onClick={() => onCite(ordinal)} title={citation.sourceName ? `${citation.title} · ${citation.sourceName}` : citation.title} aria-label={`查看来源 ${ordinal}：${citation.title}`}>{ordinal}</button></span>;
        return streaming ? <span className="citation-ref pending" aria-hidden="true"><span>{ordinal}</span></span> : null;
      }
    };
  }, [citations, streaming, onCite]);
  return <ReactMarkdown remarkPlugins={markdownPlugins} components={components}>{streaming ? hidePartialCitationMarker(content) : content}</ReactMarkdown>;
}

interface SourceTarget { ordinal: number; nonce: number; delay: number }
interface SourceMenuState { citation: Citation; anchor: AnchorRect; trigger: HTMLButtonElement; keyboard: boolean }

function SourceList({ messageId, citations, open, onToggle, target }: { messageId: string; citations: Citation[]; open: boolean; onToggle(): void; target?: SourceTarget }) {
  const items = useRef(new Map<number, HTMLButtonElement>());
  const [highlighted, setHighlighted] = useState<number>();
  const [menu, setMenu] = useState<SourceMenuState>();
  const [menuOpen, setMenuOpen] = useState(false);
  const menuCloseTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const menuOpenFrames = useRef<{ first?: number; second?: number }>({});
  const contentId = `sources-${messageId}`;
  const cancelMenuOpen = () => {
    if (menuOpenFrames.current.first !== undefined) cancelAnimationFrame(menuOpenFrames.current.first);
    if (menuOpenFrames.current.second !== undefined) cancelAnimationFrame(menuOpenFrames.current.second);
    menuOpenFrames.current = {};
  };
  const closeMenu = (restoreFocus = false) => {
    cancelMenuOpen();
    clearTimeout(menuCloseTimer.current);
    setMenuOpen(false);
    menuCloseTimer.current = setTimeout(() => setMenu(undefined), 260);
    if (restoreFocus) menu?.trigger.focus({ preventScroll: true });
  };
  const chooseSource = (event: React.MouseEvent<HTMLButtonElement>, citation: Citation) => {
    if (menuOpen && menu?.citation.id === citation.id) { closeMenu(); return; }
    cancelMenuOpen();
    clearTimeout(menuCloseTimer.current);
    setMenuOpen(false);
    setMenu({ citation, anchor: rectOf(event.currentTarget), trigger: event.currentTarget, keyboard: event.detail === 0 });
    menuOpenFrames.current.first = requestAnimationFrame(() => {
      menuOpenFrames.current.second = requestAnimationFrame(() => {
        menuOpenFrames.current = {};
        setMenuOpen(true);
      });
    });
  };
  const openSource = () => {
    if (!menu) return;
    window.open(menu.citation.url, '_blank', 'noopener,noreferrer');
    closeMenu(true);
  };
  useEffect(() => () => { cancelMenuOpen(); clearTimeout(menuCloseTimer.current); }, []);
  useEffect(() => {
    if (!target) return;
    setHighlighted(target.ordinal);
    const reveal = setTimeout(() => {
      const item = items.current.get(target.ordinal);
      item?.focus({ preventScroll: true });
      item?.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
    }, target.delay);
    const fade = setTimeout(() => setHighlighted(undefined), target.delay + 1400);
    return () => { clearTimeout(reveal); clearTimeout(fade); };
  }, [target]);
  return <section className={`sources ${open ? 'is-open' : ''}`}>
    <button type="button" className="sources-toggle" onClick={onToggle} aria-expanded={open} aria-controls={contentId}>
      <span>参考来源</span><Icon name="chevron" size={13}/>
    </button>
    <div className="sources-disclosure" id={contentId} inert={!open}><div><div className="sources-list">{citations.map(citation => <button type="button" key={citation.id}
      ref={element => { if (element) items.current.set(citation.ordinal, element); else items.current.delete(citation.ordinal); }}
      className={`source-item ${highlighted === citation.ordinal ? 'is-targeted' : ''} ${menuOpen && menu?.citation.id === citation.id ? 'is-active' : ''}`}
      onClick={event => chooseSource(event, citation)} title={citation.url} aria-haspopup="menu" aria-expanded={menuOpen && menu?.citation.id === citation.id}>
      <b>{citation.ordinal}</b><span>{citation.title}</span>{citation.sourceName && <small>{citation.sourceName}</small>}
    </button>)}</div></div></div>
    {menu && <SourceMenu state={menu} open={menuOpen} onClose={closeMenu} onOpen={openSource}/>}
  </section>;
}

function SourceMenu({ state, open, onClose, onOpen }: { state: SourceMenuState; open: boolean; onClose(restoreFocus?: boolean): void; onOpen(): void }) {
  const menu = useRef<HTMLDivElement>(null);
  const firstItem = useRef<HTMLButtonElement>(null);
  const host = new URL(state.citation.url).hostname;
  useLayoutEffect(() => {
    const element = menu.current;
    if (!element) return;
    const { anchor } = state;
    const gap = 6;
    const width = element.offsetWidth;
    const height = element.offsetHeight;
    const left = Math.max(8, Math.min(anchor.left, window.innerWidth - width - 8));
    const below = anchor.top + anchor.height + gap;
    const above = below + height > window.innerHeight - 8 && anchor.top - gap - height >= 8;
    const top = above ? anchor.top - gap - height : Math.min(below, window.innerHeight - height - 8);
    const originX = anchor.left + Math.min(anchor.width / 2, 20) - left;
    const originY = above ? height + gap + anchor.height / 2 : -(gap + anchor.height / 2);
    element.style.left = `${left}px`;
    element.style.top = `${top}px`;
    element.style.transformOrigin = `${originX}px ${originY}px`;
  }, [state]);
  useEffect(() => {
    if (!open) return;
    if (state.keyboard) requestAnimationFrame(() => firstItem.current?.focus({ preventScroll: true }));
    const onPointer = (event: PointerEvent) => {
      const node = event.target as Node;
      if (!menu.current?.contains(node) && !state.trigger.contains(node)) onClose();
    };
    const onKey = (event: KeyboardEvent) => { if (event.key === 'Escape') onClose(true); };
    const onViewportChange = () => onClose();
    document.addEventListener('pointerdown', onPointer);
    document.addEventListener('keydown', onKey);
    document.addEventListener('scroll', onViewportChange, true);
    window.addEventListener('resize', onViewportChange);
    return () => {
      document.removeEventListener('pointerdown', onPointer);
      document.removeEventListener('keydown', onKey);
      document.removeEventListener('scroll', onViewportChange, true);
      window.removeEventListener('resize', onViewportChange);
    };
  }, [open, state]);
  return createPortal(<div ref={menu} className={`popover-menu source-menu ${open ? 'is-open' : 'is-closing'}`} role="menu"
    aria-label={`来源 ${state.citation.ordinal}：${state.citation.title}`} aria-hidden={!open} inert={!open}>
    <div className="source-menu-info" aria-hidden="true"><b>{state.citation.title}</b><small>{state.citation.sourceName ? `${state.citation.sourceName} · ${host}` : host}</small></div>
    <button ref={firstItem} role="menuitem" onClick={onOpen}><Icon name="external"/>打开网页</button>
    <button role="menuitem" onClick={() => onClose(true)}><Icon name="close"/>取消</button>
  </div>, document.body);
}

function Composer({ value, onChange, onSend, onAttach, onStop, hasApiKey, sending, disabled = false, focusSignal, onOpenSettings }: { value: string; onChange(value: string): void; onSend(): void; onAttach?: () => void; onStop?: () => void; hasApiKey: boolean; sending: boolean; disabled?: boolean; focusSignal: number; onOpenSettings(): void }) {
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
  return <div className="composer-wrap"><div className="composer">{onAttach && <button className="composer-tool" onClick={onAttach} aria-label="添加本地文件" title="添加本地文件"><Icon name="attach"/></button>}<div className={`composer-editor ${scrollActive ? 'scroll-active' : ''}`}><textarea ref={textarea} value={value} rows={1} placeholder="输入消息" aria-label="输入消息" onScroll={() => { updateThumb(); revealThumb(); }} onChange={event => onChange(event.target.value)} onKeyDown={event => { if (shouldSubmitComposer(event.key, event.shiftKey, event.nativeEvent.isComposing)) { event.preventDefault(); if (canSend) onSend(); } }}/><div ref={rail} className={`textarea-rail ${thumb.visible ? 'has-overflow' : ''}`} aria-hidden="true"><i style={{ height: thumb.size, transform: `translateY(${thumb.offset}px)` }}/></div></div>{onStop && <button className="stop-button" onClick={onStop} aria-label="停止任务" title="停止任务"><Icon name="stop"/></button>}<button className={`send-button ${canSend ? 'ready' : ''}`} disabled={!canSend} onClick={onSend} aria-label={sending ? '正在发送' : '发送消息'}><Icon name="send"/></button></div>{!hasApiKey && value.trim() ? <button className="key-hint" onClick={onOpenSettings}><Icon name="key" size={14}/>配置 API Key 后即可发送</button> : <p>AI 生成的内容可能不准确，请核实重要信息。</p>}</div>;
}

function ApprovalComposer({ approvals, busy, onResolve, onStop }: {
  approvals: WorkApproval[]; busy: Set<string>; onResolve(approval: WorkApproval, approved: boolean): void; onStop?: () => void;
}) {
  const approval = approvals[0];
  const resolving = busy.has(`approval-${approval.id}`);
  const action = approval.capability === 'execute' ? '运行外部命令' : approval.capability === 'write' ? '修改外部文件' : '读取外部文件';
  const scope = approval.scope === 'task_directory' ? '本次任务期间' : '仅限这一次操作';
  return <div className="composer-wrap approval-composer-wrap">
    <section className="approval-composer" role="dialog" aria-label="操作确认" aria-live="polite">
      <span className="approval-composer-icon"><Icon name="shield"/></span>
      <div className="approval-composer-copy"><div><strong>允许小猫助手{action}吗？</strong>{approvals.length > 1 && <em>确认请求 1 / {approvals.length}</em>}</div>{approval.targetPath && <code title={approval.targetPath}>{approval.targetPath}</code>}<small>{approval.capability === 'read' ? '只读' : approval.capability === 'write' ? '读写' : '执行'} · {scope}</small></div>
      <div className="approval-composer-actions">{onStop && <button className="stop-button" onClick={onStop} disabled={resolving} aria-label="停止任务" title="停止任务"><Icon name="stop"/></button>}<button className="secondary-button" disabled={resolving} onClick={() => onResolve(approval, false)}>拒绝</button><button className="primary-button" disabled={resolving} onClick={() => onResolve(approval, true)}>{resolving ? '处理中…' : '允许'}</button></div>
    </section>
    <p>确认后任务会自动继续。</p>
  </div>;
}

function DraftConversation({ mode, onMode, value, onChange, onSend, onAttach, hasApiKey, sending, focusSignal, onOpenSettings }: {
  mode: 'chat' | 'work'; onMode(mode: 'chat' | 'work'): void; value: string; onChange(value: string): void;
  onSend(): void; onAttach(): void; hasApiKey: boolean; sending: boolean; focusSignal: number; onOpenSettings(): void;
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
      <p className="mode-note">{workSelected ? '处理本地文件、运行受隔离的工具，并交付成果。' : '适合问答、写作和日常交流。'}</p>
    </div>
    <Composer value={value} onChange={onChange} onSend={onSend} onAttach={workSelected ? onAttach : undefined} hasApiKey={hasApiKey} sending={sending} focusSignal={focusSignal} onOpenSettings={onOpenSettings}/>
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
function SettingsPage({ settings, onboarding, busy, onBack, onSave }: { settings: PublicSettings; onboarding: boolean; busy: boolean; onBack(): void; onSave(patch: Partial<PublicSettings> & { apiKey?: string; searchApiKey?: string }, successText: string): Promise<boolean> }) {
  const [requestedSection, setRequestedSection] = useState<SettingsSection>('service');
  const [displayedSection, setDisplayedSection] = useState<SettingsSection>('service');
  const [sectionPhase, setSectionPhase] = useState<'steady' | 'out' | 'in'>('steady');
  const sectionVersion = useRef(0);
  const [apiKey, setApiKey] = useState('');
  const [searchApiKey, setSearchApiKey] = useState('');
  const [chatModel, setChatModel] = useState(settings.chatModel);
  const [workModel, setWorkModel] = useState(settings.workModel);
  const [asrModel, setAsrModel] = useState(settings.asrModel);
  const [screenTimeout, setScreenTimeout] = useState(settings.screenTimeoutSeconds);
  const [webSearch, setWebSearch] = useState(settings.webSearchEnabled);
  const [autoReconnect, setAutoReconnect] = useState(settings.autoReconnect);
  useEffect(() => { setChatModel(settings.chatModel); setWorkModel(settings.workModel); setAsrModel(settings.asrModel); setScreenTimeout(settings.screenTimeoutSeconds); setWebSearch(settings.webSearchEnabled); setAutoReconnect(settings.autoReconnect); }, [settings]);
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
    {displayedSection === 'service' && <section><SettingsHeading eyebrow="AI 服务" title="模型与联网" description="凭据只保存在这台电脑上。"/><div className="settings-group"><form className="setting-row editable-row" onSubmit={async event => { event.preventDefault(); if (await onSave({ apiKey: apiKey || undefined, chatModel, workModel }, 'AI 服务设置已保存')) setApiKey(''); }}><span><b>模型与 API Key</b><small>{settings.hasApiKey ? 'Chat 与 Work 使用独立模型' : '需要配置 API Key'}</small></span><div className="inline-fields"><input value={chatModel} onChange={event => setChatModel(event.target.value)} aria-label="Chat 模型" placeholder="Chat 模型"/><input value={workModel} onChange={event => setWorkModel(event.target.value)} aria-label="Work 模型" placeholder="Work 模型"/><input type="password" value={apiKey} onChange={event => setApiKey(event.target.value)} placeholder={settings.hasApiKey ? '留空不更改 Key' : '输入 API Key'} aria-label="API Key" autoComplete="off"/><button className="line-save" disabled={busy}>保存</button></div></form><Switch checked={webSearch} disabled={busy} onChange={value => void toggleWeb(value)} label="联网搜索" description="需要最新信息时允许模型搜索网络"/></div><div className="settings-group"><form className="setting-row editable-row" onSubmit={async event => { event.preventDefault(); if (await onSave({ searchApiKey: searchApiKey || undefined }, '联网搜索服务已保存')) setSearchApiKey(''); }}><span><b>联网搜索服务</b><small>{settings.hasSearchApiKey ? 'Work 通过独立搜索接口检索网页' : 'Work 需要填写 IQS API Key'}</small></span><div className="inline-fields compact"><input type="password" value={searchApiKey} onChange={event => setSearchApiKey(event.target.value)} placeholder={settings.hasSearchApiKey ? '留空不更改 Key' : '输入 IQS API Key'} aria-label="联网搜索 API Key" autoComplete="off"/><button className="line-save" disabled={busy}>保存</button></div></form></div></section>}
    {displayedSection === 'device' && <section><SettingsHeading eyebrow="AI 卡片" title="语音与显示" description="卡片连接后会自动同步这些选项。"/><div className="settings-group"><form className="setting-row editable-row" onSubmit={event => { event.preventDefault(); void onSave({ asrModel, screenTimeoutSeconds: screenTimeout }, '卡片设置已保存'); }}><span><b>识别与休眠</b><small>休眠时间设为 0 时保持亮屏</small></span><div className="inline-fields compact"><input value={asrModel} onChange={event => setAsrModel(event.target.value)} aria-label="语音识别模型"/><input type="number" min="0" max="86400" value={screenTimeout} onChange={event => setScreenTimeout(Math.max(0, Math.min(86400, Number(event.target.value))))} aria-label="屏幕休眠秒数"/><button className="line-save" disabled={busy}>保存</button></div></form><Switch checked={autoReconnect} disabled={busy} onChange={value => void toggleReconnect(value)} label="自动重连" description="启动后连接已绑定的卡片"/></div></section>}
    {displayedSection === 'privacy' && <section><SettingsHeading eyebrow="数据与隐私" title="本地保存" description="小猫助手不会自动同步你的对话。"/><div className="settings-group privacy-rows"><InfoRow icon="shield" title="API Key" detail="AI 服务与联网搜索的 API Key 使用 Windows DPAPI 加密，仅当前用户可读取。"/><InfoRow icon="chat" title="会话历史" detail="保存在本机 SQLite 数据库中。"/><InfoRow icon="device" title="AI 卡片" detail="只接收当前对话所需的文本和控制数据。"/></div></section>}
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
function groupConversations(items: ConversationSummary[]) { const result = new Map<string, ConversationSummary[]>(), now = new Date(), startToday = new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime(); for (const item of items) { const updated = new Date(item.updatedAt).getTime(), age = startToday - updated, label = item.isPinned ? '已置顶' : updated >= startToday ? '今天' : age < 2 * 86400000 ? '昨天' : age < 7 * 86400000 ? '最近 7 天' : '更早'; result.set(label, [...(result.get(label) ?? []), item]); } return [...result].map(([label, groupItems]) => ({ label, items: groupItems })); }
function safeHttpUrl(value?: string): value is string { if (!value) return false; try { return ['http:', 'https:'].includes(new URL(value).protocol); } catch { return false; } }
function errorText(reason: unknown) { return reason instanceof Error ? reason.message : String(reason); }

createRoot(document.getElementById('root')!).render(<React.StrictMode><App/></React.StrictMode>);
