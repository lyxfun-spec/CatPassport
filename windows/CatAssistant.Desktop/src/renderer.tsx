import { changeLanguage, useLanguage } from './language-store';
import type { LanguagePreference } from './i18n';
import { t } from './i18n';
import React, { createContext, memo, useCallback, useContext, useEffect, useLayoutEffect, useMemo, useReducer, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { createRoot } from 'react-dom/client';
import ReactMarkdown, { type Components } from 'react-markdown';
import remarkGfm from 'remark-gfm';
import type { BackendEvent, BackendInfo, BootstrapState, CatDevice, ChatToolCall, Citation, ConversationDetail, ConversationMessage, ConversationSource, ConversationSummary, PublicSettings, WorkApproval, WorkArtifact, WorkRequest, WorkToolCall } from './shared';
import { citationOrdinal, hidePartialCitationMarker, remarkCitations, stripCitationMarkers } from './citations';
import { hideSplash, showSplashError } from './boot-splash';
import { deviceDiscoveryMessage, initialState, reduceBackendEvent } from './state';
import { assistantCopyTargets, buildChatReplyParts, buildWorkTimeline, chatToolStatusLabel, clampTextareaHeight, draftGreetingForHour, greetingForHour, groupChatToolCalls, groupWorkRequests, isAtScrollEnd, planCitationJump, scrollThumbMetrics, searchOperationEmptyText, searchOperationStatus, shouldSubmitComposer, sourcesForOperation, summarizeWorkToolGroup, toolDisplayName, workTimelineCopyActions, type AnchoredChatTool, type AnchoredWorkRequest } from './ui-behavior';
import { textMenuEntries, type TextMenuAction, type TextMenuScene } from './text-context-menu';
import { AnchorRect, AnchoredSurface, rectOf, ScrollArea } from './ui-primitives';
import brandUrl from './assets/brand.png';
import './ui-tokens.css';
import './styles.css';

type IconName = 'new' | 'search' | 'chat' | 'work' | 'device' | 'settings' | 'menu' | 'plus' | 'arrowUp'
  | 'copy' | 'cut' | 'paste' | 'selectAll' | 'close' | 'check' | 'back' | 'rename' | 'delete' | 'refresh'
  | 'key' | 'shield' | 'info' | 'chevron' | 'pin' | 'external' | 'stop' | 'file' | 'terminal';

const iconPaths: Record<IconName, React.ReactNode> = {
  new: <path d="M12 5v14M5 12h14"/>,
  search: <><circle cx="10.5" cy="10.5" r="6.5"/><path d="m16 16 4 4"/></>,
  chat: <><path d="M5 18.2 3.8 21l3.7-1.2A9 9 0 1 0 5 18.2Z"/><path d="M8 11.5h8M8 15h5"/></>,
  work: <><path d="M4 5.5C4 4.67 4.67 4 5.5 4H18.5C19.33 4 20 4.67 20 5.5V16H4V5.5Z"/><path d="M2 16H22V17C22 18.66 20.66 20 19 20H5C3.34 20 2 18.66 2 17V16Z"/></>,
  device: <><rect x="6" y="3" width="12" height="18" rx="3"/><path d="M10 7h4M10 17h4"/></>,
  settings: <><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.9l.1.1-2.8 2.8-.1-.1a1.7 1.7 0 0 0-1.9-.3 1.7 1.7 0 0 0-1 1.6V21h-4v-.1a1.7 1.7 0 0 0-1-1.6 1.7 1.7 0 0 0-1.9.3l-.1.1L4.2 17l.1-.1a1.7 1.7 0 0 0 .3-1.9A1.7 1.7 0 0 0 3 14v-4a1.7 1.7 0 0 0 1.6-1 1.7 1.7 0 0 0-.3-1.9L4.2 7 7 4.2l.1.1A1.7 1.7 0 0 0 9 4.6 1.7 1.7 0 0 0 10 3h4a1.7 1.7 0 0 0 1 1.6 1.7 1.7 0 0 0 1.9-.3l.1-.1L19.8 7l-.1.1a1.7 1.7 0 0 0-.3 1.9 1.7 1.7 0 0 0 1.6 1v4a1.7 1.7 0 0 0-1.6 1Z"/></>,
  menu: <><circle cx="5" cy="12" r="1"/><circle cx="12" cy="12" r="1"/><circle cx="19" cy="12" r="1"/></>,
  plus: <path d="M12 5v14M5 12h14"/>,
  arrowUp: <path d="M12 19V5M5 12l7-7 7 7"/>,
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
  stop: <rect x="7" y="7" width="10" height="10" rx="1"/>,
  file: <><path d="M6 3h8l4 4v14H6Z"/><path d="M14 3v5h5"/></>,
  terminal: <><rect x="3" y="4" width="18" height="16" rx="3"/><path d="m7 9 3 3-3 3M12.5 15H17"/></>
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
  useLanguage();
  const [state, dispatch] = useReducer(reduceBackendEvent, initialState);
  const [requestedView, setRequestedView] = useState<AppView>('compose');
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
    if (copied) { dispatch({ event: 'ui.conversation', data: copied }); setRequestedView('conversation'); showNotice(t("已添加 {0} 个文件", files.length)); }
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
    showNotice(approved ? result.state === 'started' ? t("Work 任务已在后台开始") : t("正在创建 Work 任务") : t("已拒绝 Work 任务"));
  };
  const rename = async (title: string) => {
    const target = actionSurface?.conversation;
    if (!target || title === target.title) { setActionSurface(undefined); return; }
    const result = await run('rename', () => invoke('conversations.rename', { id: target.id, title }));
    if (result === undefined) return;
    setActionSurface(undefined); await refresh(); showNotice(t("对话名称已更新"));
  };
  const remove = async () => {
    const target = actionSurface?.conversation;
    if (!target) return;
    const result = await run('delete', () => invoke('conversations.delete', { id: target.id }));
    if (result === undefined) return;
    setActionSurface(undefined); await refresh(); showNotice(t("对话已删除"));
  };
  const setPinned = async (conversation: ConversationSummary) => {
    setConversationContext(undefined);
    const result = await run('pin', () => invoke('conversations.pin', { id: conversation.id, pinned: !conversation.isPinned }));
    if (result === undefined) return;
    await refresh(); showNotice(conversation.isPinned ? t("已取消置顶") : t("对话已置顶"));
  };
  const saveSettings = async (patch: Partial<PublicSettings> & { apiKey?: string; searchApiKey?: string; asrApiKey?: string }, successText: string) => {
    const current = bootstrap.settings;
    const result = await run('settings', () => invoke<PublicSettings>('settings.save', {
      apiKey: patch.apiKey || undefined,
      searchApiKey: patch.searchApiKey || undefined,
      asrApiKey: patch.asrApiKey || undefined,
      asrModel: patch.asrModel ?? current.asrModel,
      chatModel: patch.chatModel ?? current.chatModel,
      workModel: patch.workModel ?? current.workModel,
      modelApiUrl: patch.modelApiUrl ?? current.modelApiUrl,
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
          ? <SettingsPage settings={bootstrap.settings} backend={bootstrap.backend} onboarding={!bootstrap.settings.hasApiKey} busy={isBusy('settings')} onBack={() => setRequestedView('conversation')} onSave={saveSettings}/>
          : displayedView === 'compose' || !active
            ? <DraftConversation mode={draftMode} onMode={setDraftMode} value={draft} onChange={setDraft} onSend={() => void send()} onAttach={() => void attachFiles()} hasApiKey={bootstrap.settings.hasApiKey} sending={isBusy('send')} focusSignal={composerFocusSignal} onOpenSettings={() => setRequestedView('settings')}/>
            : <>
              <ConversationHeader conversation={active.conversation} origin={active.conversation.originConversationId ? bootstrap.conversations.find(item => item.id === active.conversation.originConversationId) : undefined} onOpenConversation={id => void selectConversation(id)} onAction={(kind, anchor) => setActionSurface({ kind, anchor, conversation: active.conversation })}/>
              <MessageList detail={active} runError={state.runError} busy={busy} tools={active.conversation.mode === 'work' ? bootstrap.workToolCalls ?? noWorkToolCalls : noWorkToolCalls} artifacts={active.conversation.mode === 'work' ? bootstrap.workArtifacts ?? noWorkArtifacts : noWorkArtifacts} onReveal={artifactId => void window.catAssistant.revealArtifact(active.conversation.id, artifactId)} onResolveWorkRequest={(request, approved) => void resolveWorkRequest(request, approved)} onOpenConversation={id => void selectConversation(id)} onNotice={showNotice}/>
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
  useLanguage();
  const filtered = useMemo(() => bootstrap.conversations.filter(item => item.title.toLocaleLowerCase().includes(query.toLocaleLowerCase())), [bootstrap.conversations, query]);
  const groups = groupConversations(filtered);
  const activeDeviceConversation = bootstrap.conversations.find(item => item.id === bootstrap.device.activeConversationId);
  return <aside className="sidebar" aria-label={t("会话导航")}>
    <div className="brand-row"><BrandMark/><strong className="brand-name">{t("小猫助手")}</strong></div>
    <button className={`new-conversation ${composeActive ? 'active' : ''}`} onClick={onNew} aria-label={t("新建对话")} aria-current={composeActive ? 'true' : undefined}><Icon name="new"/><span>{t("新对话")}</span></button>
    <label className="search-box"><Icon name="search"/><input value={query} onChange={event => onQuery(event.target.value)} placeholder={t("搜索对话")} aria-label={t("搜索对话")}/></label>
    <ScrollArea className="conversation-scroll" viewportClassName="conversation-list">
      {groups.map(group => <section className="conversation-group" key={group.label} aria-label={group.label}><h2>{group.label}</h2>{group.items.map(item => <button key={item.id} className={`conversation-item ${!settingsActive && !composeActive && bootstrap.activeConversationId === item.id ? 'active' : ''}`} onClick={() => onSelect(item.id)} onContextMenu={event => { event.preventDefault(); const rect = event.currentTarget.getBoundingClientRect(); onContext(item, event.clientX || rect.right, event.clientY || rect.top); }} aria-current={!settingsActive && !composeActive && bootstrap.activeConversationId === item.id ? 'page' : undefined} aria-label={`${item.title}${item.isPinned ? t("，已置顶") : ''}`} title={item.title}><Icon name={item.mode === 'work' ? 'work' : 'chat'} size={16}/><span>{item.title}</span></button>)}</section>)}
      {filtered.length === 0 && <div className="no-results">{query ? t("没有匹配的对话") : t("还没有对话")}</div>}
    </ScrollArea>
    <div className="sidebar-footer">
      <button className="footer-entry" onClick={onDevice}><span className={`status-dot ${bootstrap.device.isConnected ? 'online' : ''}`}/><Icon name="device"/><span className="footer-copy"><b>{bootstrap.device.isConnected ? t("AI 卡片") : t("连接 AI 卡片")}</b><small>{bootstrap.device.isConnected ? activeDeviceConversation?.title ?? t("已连接") : t("可选辅助硬件")}</small></span></button>
      <button className={`footer-entry ${settingsActive ? 'active' : ''}`} onClick={onSettings}><Icon name="settings"/><span className="footer-copy"><b>{t("设置")}</b><small>{bootstrap.settings.hasApiKey ? t("服务已配置") : t("需要配置 API Key")}</small></span></button>
    </div>
  </aside>;
}

const textMenuIcons: Record<TextMenuAction, IconName> = { cut: 'cut', copy: 'copy', paste: 'paste', selectAll: 'selectAll' };

function TextContextMenu({ value, onClose, onAction }: {
  value: TextMenuState; onClose(): void; onAction(action: TextMenuAction): void;
}) {
  useLanguage();
  const menu = useRef<HTMLDivElement>(null);
  const entries = value ? textMenuEntries(value.scene, value.hasSelection) : [];
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
  return <div ref={menu} className="text-context-menu" role="menu" aria-label={t("文字操作")} style={{ left: value.x, top: value.y }}>
    {entries.map((entry, index) => <button key={entry.id} role="menuitem" tabIndex={-1} className={index === active ? 'is-active' : ''} onMouseDown={event => event.preventDefault()} onClick={() => onAction(entry.id)}><Icon name={textMenuIcons[entry.id]} size={15}/>{entry.label}</button>)}
  </div>;
}

function ConversationContextMenu({ value, onClose, onPin, onAction }: {
  value: ConversationContext; onClose(): void; onPin(conversation: ConversationSummary): void;
  onAction(kind: 'rename' | 'delete', anchor: AnchorRect, conversation: ConversationSummary): void;
}) {
  useLanguage();
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
  return <div ref={menu} className="conversation-context-menu" role="menu" aria-label={t("对话操作")} style={{ left: value.left, top: value.top }}>
    <button ref={firstItem} role="menuitem" onClick={() => onPin(value.conversation)}><Icon name="pin"/>{value.conversation.isPinned ? t("取消置顶") : t("置顶")}</button>
    <button role="menuitem" onClick={event => choose('rename', event.currentTarget)}><Icon name="rename"/>{t("重命名")}</button>
    <button role="menuitem" className="danger" onClick={event => choose('delete', event.currentTarget)}><Icon name="delete"/>{t("删除")}</button>
  </div>;
}

function ConversationHeader({ conversation, origin, onOpenConversation, onAction }: {
  conversation: ConversationSummary; origin?: ConversationSummary; onOpenConversation(id: string): void;
  onAction(kind: 'rename' | 'delete', anchor: AnchorRect): void;
}) {
  useLanguage();
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
  return <header className="conversation-header"><div className="conversation-heading"><h1>{conversation.title}</h1>{origin && <button className="origin-link" onClick={() => onOpenConversation(origin.id)} title={t("返回来源 Chat：{0}", origin.title)}><Icon name="back" size={13}/>{t("来源：")}{origin.title}</button>}</div><div className="header-menu" ref={wrapper}><button ref={trigger} className="icon-button" onClick={toggleMenu} aria-label={t("对话选项")} aria-haspopup="menu" aria-expanded={menuOpen}><Icon name="menu"/></button>{menuRendered && <div className={`popover-menu ${menuOpen ? 'is-open' : 'is-closing'}`} role="menu" aria-hidden={!menuOpen} inert={!menuOpen}><button ref={firstItem} role="menuitem" onClick={event => choose('rename', event.currentTarget)}><Icon name="rename"/>{t("重命名")}</button><button role="menuitem" className="danger" onClick={event => choose('delete', event.currentTarget)}><Icon name="delete"/>{t("删除")}</button></div>}</div></header>;
}

function toolStateLabel(tool: WorkToolCall): string {
  if (tool.state === 'failed' && tool.errorCode === 'permission_rejected') return t("未执行");
  return ({ pending: t("等待中"), waiting_approval: t("等待批准"), running: t("执行中"), completed: t("已完成"), failed: t("失败"), cancelled: t("已取消"), rejected: t("未执行") } as Record<string, string>)[tool.state] ?? tool.state;
}

function ToolGroup({ tools }: { tools: WorkToolCall[] }) {
  useLanguage();
  const summary = summarizeWorkToolGroup(tools);
  return <details className={`timeline-tool-group ${summary.isActive ? 'is-running' : ''}`}>
    <summary><span className="timeline-disclosure"><Icon name="chevron" size={14}/></span><strong>{summary.label}</strong></summary>
    <div className="timeline-tool-list">{tools.map(tool => <details className="timeline-tool" key={tool.id}>
      <summary><span className="timeline-disclosure"><Icon name="chevron" size={13}/></span><span>{toolDisplayName(tool.toolName)}</span><em className={`tool-state state-${tool.errorCode === 'permission_rejected' ? 'rejected' : tool.state}`}>{toolStateLabel(tool)}</em>{tool.durationMilliseconds !== undefined && <small>{formatDuration(tool.durationMilliseconds)}</small>}</summary>
      <div className="timeline-tool-detail"><b>{t("工具")}</b><code>{tool.toolName}</code><b>{t("参数")}</b><ScrollArea className="tool-detail-scroll" viewportClassName="tool-detail-viewport"><pre>{maskSensitiveJson(tool.argumentsJson)}</pre></ScrollArea>{tool.resultJson && <><b>{t("结果")}</b><ScrollArea className="tool-detail-scroll" viewportClassName="tool-detail-viewport"><pre>{maskSensitiveJson(tool.resultJson)}</pre></ScrollArea></>}{tool.errorMessage && <p className="tool-error">{tool.errorMessage}</p>}</div>
    </details>)}</div>
  </details>;
}

function ArtifactGroup({ artifacts, onReveal }: { artifacts: WorkArtifact[]; onReveal(id: string): void }) {
  useLanguage();
  return <section className="timeline-artifacts" aria-label={artifacts.length === 1 ? t("交付成果") : t("交付 {0} 个成果", artifacts.length)}>
    {artifacts.length > 1 && <span className="artifact-group-label">{t("本次生成了 {0} 个文件", artifacts.length)}</span>}
    <div>{artifacts.map(artifact => <button key={artifact.id} onClick={() => onReveal(artifact.id)} title={artifact.label}><span className="artifact-file-icon"><Icon name="file"/></span><span><b>{artifact.label}</b><small>{t("在文件资源管理器中显示")}</small></span><Icon name="external" size={14}/></button>)}</div>
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
          : /(?:content|body|binary|base64)/i.test(key) && typeof child === 'string' ? t("[内容已隐藏，{0} 字符]", child.length)
          : redact(child)]));
      return item;
    };
    return JSON.stringify(redact(JSON.parse(value)), null, 2);
  } catch { return value.length > 2000 ? `${value.slice(0, 2000)}…` : value; }
}

const noCitations: Citation[] = [];
const noWorkToolCalls: WorkToolCall[] = [];
const noWorkArtifacts: WorkArtifact[] = [];
const noAnchoredRequests: AnchoredWorkRequest[] = [];
const noAnchoredChatTools: AnchoredChatTool[] = [];
const noConversationSources: ConversationSource[] = [];

interface SearchSourceTarget { operationId: string; sourceId: string; nonce: number }
interface ChatSourceContextValue {
  sourcesByOrdinal: Map<number, ConversationSource>;
  openOperations: ReadonlySet<string>;
  setOperationOpen(id: string, open: boolean): void;
  target?: SearchSourceTarget;
  sourcesFor(tool: ChatToolCall): ConversationSource[];
  onCite(ordinal: number): void;
}

const ChatSourceContext = createContext<ChatSourceContextValue>({
  sourcesByOrdinal: new Map(),
  openOperations: new Set(),
  setOperationOpen: () => {},
  sourcesFor: () => [],
  onCite: () => {}
});

const WorkRequestCardContext = createContext<{
  busy: Set<string>;
  resolve(request: WorkRequest, approved: boolean): void;
  openConversation(id: string): void;
}>({ busy: new Set(), resolve: () => {}, openConversation: () => {} });

function MessageList({ detail, runError, busy, tools, artifacts, onReveal, onResolveWorkRequest, onOpenConversation, onNotice }: {
  detail: ConversationDetail; runError?: string; busy: Set<string>;
  tools: WorkToolCall[]; artifacts: WorkArtifact[]; onReveal(id: string): void;
  onResolveWorkRequest(request: WorkRequest, approved: boolean): void; onOpenConversation(id: string): void;
  onNotice(text: string, tone?: 'default' | 'danger'): void;
}) {
  useLanguage();
  const scroller = useRef<HTMLDivElement>(null);
  const pinned = useRef(true);
  const lastIntent = useRef<'up' | 'down'>('up');
  const pendingScroll = useRef(0);
  const lastContent = detail.messages.at(-1)?.content;
  const citationsByMessage = useMemo(() => {
    const grouped = new Map<string, Citation[]>();
    for (const citation of detail.citations) grouped.set(citation.messageId, [...(grouped.get(citation.messageId) ?? []), citation]);
    return grouped;
  }, [detail.citations]);
  const requestsByMessage = useMemo(() => groupWorkRequests(detail.messages, detail.workRequests ?? [], detail.conversation.id), [detail.conversation.id, detail.messages, detail.workRequests]);
  const chatToolsByMessage = useMemo(() => groupChatToolCalls(detail.messages, detail.chatToolCalls ?? []), [detail.messages, detail.chatToolCalls]);
  const anchoredOperations = useMemo(() => [...chatToolsByMessage.values()].flatMap(items => items.map(item => item.tool)), [chatToolsByMessage]);
  const chatToolCount = detail.chatToolCalls?.length ?? 0;
  const conversationSources = detail.conversationSources ?? noConversationSources;
  const sourcesByOrdinal = useMemo(() => new Map(conversationSources.map(source => [source.ordinal, source])), [conversationSources]);
  const sourcesByOperation = useMemo(() => {
    const grouped = new Map<string, ConversationSource[]>();
    for (const tool of detail.chatToolCalls ?? [])
      if (tool.toolName === 'web_search') grouped.set(tool.id, sourcesForOperation(conversationSources, tool));
    return grouped;
  }, [conversationSources, detail.chatToolCalls]);
  const [openOperations, setOpenOperations] = useState<Set<string>>(() => new Set());
  const [sourceTarget, setSourceTarget] = useState<SearchSourceTarget>();
  useEffect(() => { setOpenOperations(new Set()); setSourceTarget(undefined); }, [detail.conversation.id]);
  const setOperationOpen = useCallback((id: string, open: boolean) => setOpenOperations(current => {
    const next = new Set(current);
    if (open) next.add(id); else next.delete(id);
    return next;
  }), []);
  const handleCitation = useCallback((ordinal: number) => {
    const jump = planCitationJump(conversationSources, anchoredOperations, ordinal);
    if (!jump) { onNotice(t("未找到对应搜索资料")); return; }
    setOpenOperations(current => new Set(current).add(jump.operationId));
    setSourceTarget({ operationId: jump.operationId, sourceId: jump.sourceId, nonce: Date.now() });
  }, [conversationSources, anchoredOperations, onNotice]);
  const sourceContext = useMemo<ChatSourceContextValue>(() => ({
    sourcesByOrdinal,
    openOperations,
    setOperationOpen,
    target: sourceTarget,
    sourcesFor: tool => sourcesByOperation.get(tool.id) ?? [],
    onCite: handleCitation
  }), [sourcesByOrdinal, openOperations, setOperationOpen, sourceTarget, sourcesByOperation, handleCitation]);
  const timeline = useMemo(() => detail.conversation.mode === 'work' ? buildWorkTimeline(detail.messages, tools, artifacts) : undefined, [detail.conversation.mode, detail.messages, tools, artifacts]);
  const copyTargets = useMemo(() => assistantCopyTargets(detail.messages, detail.activeRunId), [detail.messages, detail.activeRunId]);
  const timelineCopyActions = useMemo(() => timeline ? workTimelineCopyActions(timeline, detail.messages, detail.activeRunId) : new Map<string, string>(), [timeline, detail.messages, detail.activeRunId]);
  const lastTimelineKey = timeline?.at(-1)?.key;
  const showRunning = Boolean(detail.activeRunId);
  const cancelPendingScroll = () => { cancelAnimationFrame(pendingScroll.current); pendingScroll.current = 0; };
  const scrollToLatest = () => {
    if (pendingScroll.current) return;
    pendingScroll.current = requestAnimationFrame(() => {
      pendingScroll.current = 0;
      if (pinned.current) scroller.current?.scrollTo({ top: scroller.current.scrollHeight });
    });
  };
  const followLatest = () => { pinned.current = true; scrollToLatest(); };
  const releaseScroll = () => {
    const element = scroller.current;
    if (!element || element.scrollHeight <= element.clientHeight) return;
    lastIntent.current = 'up';
    pinned.current = false;
    cancelPendingScroll();
  };
  const requestFollow = () => {
    lastIntent.current = 'down';
    const element = scroller.current;
    if (element && isAtScrollEnd(element.scrollHeight, element.scrollTop, element.clientHeight)) followLatest();
  };
  useEffect(() => { pinned.current = true; lastIntent.current = 'up'; cancelPendingScroll(); scrollToLatest(); }, [detail.conversation.id]);
  useEffect(() => { if (pinned.current) scrollToLatest(); }, [detail.messages.length, tools.length, artifacts.length, chatToolCount, lastContent, showRunning, runError]);
  const onScroll = () => {
    const element = scroller.current;
    if (!element || pinned.current || lastIntent.current !== 'down') return;
    if (isAtScrollEnd(element.scrollHeight, element.scrollTop, element.clientHeight)) followLatest();
  };
  const renderMessage = (message: ConversationMessage, showCopy = true) => <React.Fragment key={message.id}><MessageItem message={message} citations={citationsByMessage.get(message.id) ?? noCitations} chatTools={chatToolsByMessage.get(message.id) ?? noAnchoredChatTools} copyContent={showCopy ? copyTargets.get(message.id) : undefined} workRequests={requestsByMessage.anchored.get(message.id) ?? noAnchoredRequests}/>{(requestsByMessage.trailing.get(message.id) ?? []).map(request => <WorkRequestCard key={request.id} request={request}/>)}</React.Fragment>;
  return <ChatSourceContext.Provider value={sourceContext}><WorkRequestCardContext.Provider value={{ busy, resolve: onResolveWorkRequest, openConversation: onOpenConversation }}><ScrollArea ref={scroller} className="message-scroll" viewportClassName={`messages ${detail.messages.length ? '' : 'empty-chat'}`} onScroll={onScroll} onScrollIntent={direction => direction === 'up' ? releaseScroll() : requestFollow()}>
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
  </ScrollArea></WorkRequestCardContext.Provider></ChatSourceContext.Provider>;
}

function WorkRequestCard({ request }: { request: WorkRequest }) {
  useLanguage();
  const actions = useContext(WorkRequestCardContext);
  const busy = actions.busy.has(`work-request-${request.id}`);
  const stateLabel = ({ proposed: t("等待批准"), approved: t("正在创建"), rejected: t("已拒绝"), started: t("已开始"), failed: t("创建失败") } as const)[request.state];
  // The backend stores an empty prompt when the model's proposal could not be parsed at all.
  const retryable = request.prompt.trim().length > 0;
  return <section className={`work-request-card state-${request.state}`} aria-label={t("Work 任务：{0}", request.title)}>
    <div className="work-request-heading"><span className="work-request-icon"><Icon name="work"/></span><span><small>{t("Chat 建议交给 Work")}</small><b>{request.title}</b></span><em>{stateLabel}</em></div>
    {retryable ? <p>{request.prompt}</p> : <p className="work-request-hint">{t("模型没有给出可执行的任务内容，无法创建 Work 对话。可以让它重新提议一次。")}</p>}
    {retryable && <details><summary>{t("查看传递的背景摘要")}</summary><div>{request.contextSummary}</div></details>}
    {request.errorMessage && <div className="work-request-error" role="alert">{request.errorMessage}</div>}
    <div className="work-request-actions">
      {request.state === 'proposed' && <><button className="text-button" disabled={busy} onClick={() => actions.resolve(request, false)}>{t("拒绝")}</button><button className="primary-button" disabled={busy} onClick={() => actions.resolve(request, true)}>{busy ? t("正在创建…") : t("批准并开始")}</button></>}
      {request.state === 'failed' && retryable && <button className="primary-button" disabled={busy} onClick={() => actions.resolve(request, true)}>{busy ? t("正在重试…") : t("重试创建")}</button>}
      {request.state === 'started' && request.targetConversationId && <button className="primary-button" onClick={() => actions.openConversation(request.targetConversationId!)}>{t("打开 Work")}<Icon name="chevron" size={14}/></button>}
    </div>
  </section>;
}

function ThinkingBubble() {
  useLanguage();
  return <article className="message assistant"><div className="thinking" role="status"><i/><i/><i/><span className="sr-only">{t("任务进行中")}</span></div></article>;
}

function TimelineCopyAction({ content }: { content: string }) {
  useLanguage();
  const [copyState, setCopyState] = useState<'idle' | 'copied' | 'failed'>('idle');
  const copyTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  useEffect(() => () => clearTimeout(copyTimer.current), []);
  const copy = async () => {
    clearTimeout(copyTimer.current);
    try { await navigator.clipboard.writeText(stripCitationMarkers(content)); setCopyState('copied'); }
    catch { setCopyState('failed'); }
    copyTimer.current = setTimeout(() => setCopyState('idle'), 1650);
  };
  return <div className="timeline-actions"><button onClick={() => void copy()} aria-label={t("复制本轮回复")}><Icon name={copyState === 'copied' ? 'check' : 'copy'} size={15}/>{copyState === 'copied' ? t("已复制") : copyState === 'failed' ? t("复制失败") : t("复制")}</button></div>;
}

const MessageItem = memo(function MessageItem({ message, citations, chatTools, copyContent, workRequests }: {
  message: ConversationMessage; citations: ConversationDetail['citations']; chatTools: AnchoredChatTool[];
  copyContent?: string; workRequests?: AnchoredWorkRequest[];
}) {
  useLanguage();
  const [copyState, setCopyState] = useState<'idle' | 'copied' | 'failed'>('idle');
  const copyTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  useEffect(() => () => clearTimeout(copyTimer.current), []);
  const copy = async () => { clearTimeout(copyTimer.current); try { await navigator.clipboard.writeText(stripCitationMarkers(copyContent ?? message.content)); setCopyState('copied'); } catch { setCopyState('failed'); } copyTimer.current = setTimeout(() => setCopyState('idle'), 1650); };
  const cards = workRequests ?? noAnchoredRequests;
  if (message.role === 'assistant' && message.status === 'streaming' && !message.content && cards.length === 0 && chatTools.length === 0) return null;
  const safeCitations = citations.filter(citation => safeHttpUrl(citation.url));
  const showStatus = message.status === 'interrupted' || message.status === 'failed';
  return <article className={`message ${message.role} ${message.status}`}><div className="message-body">{message.role === 'assistant' ? <AssistantContent content={message.content} streaming={message.status === 'streaming'} citations={safeCitations} cards={cards} chatTools={chatTools}/> : <p>{message.content}</p>}</div>{message.role === 'assistant' && message.content && (copyContent || showStatus) && <div className="message-actions">{message.status === 'interrupted' && <span>{t("已中断")}</span>}{message.status === 'failed' && <span className="failed-label">{t("生成失败")}</span>}{copyContent && <button onClick={() => void copy()} aria-label={t("复制本轮回复")}><Icon name={copyState === 'copied' ? 'check' : 'copy'} size={15}/>{copyState === 'copied' ? t("已复制") : copyState === 'failed' ? t("复制失败") : t("复制")}</button>}</div>}</article>;
});

// Cards and search operations are split into the reply at the exact offsets where the model acted,
// so text emitted before a tool call stays above it and text emitted after stays below it.
function AssistantContent({ content, streaming, citations, cards, chatTools }: {
  content: string; streaming: boolean; citations: Citation[];
  cards: AnchoredWorkRequest[]; chatTools: AnchoredChatTool[];
}) {
  useLanguage();
  const parts = useMemo(() => buildChatReplyParts(content, cards, chatTools), [content, cards, chatTools]);
  if (cards.length === 0 && chatTools.length === 0)
    return <AssistantMarkdown content={content} streaming={streaming} citations={citations}/>;
  return <>{parts.map((part, index) => part.kind === 'text'
    ? <AssistantMarkdown key={`text-${index}`} content={part.text} streaming={streaming} citations={citations}/>
    : <React.Fragment key={`anchor-${index}`}>
      {part.anchor.tools.filter(tool => tool.toolName === 'web_search').map(tool => <SearchOperation key={tool.id} tool={tool}/>)}
      {part.anchor.tools.some(tool => tool.toolName !== 'web_search') && <ChatToolStatus tools={part.anchor.tools.filter(tool => tool.toolName !== 'web_search')}/>}
      {part.anchor.cards.map(request => <WorkRequestCard key={request.id} request={request}/>)}
    </React.Fragment>)}</>;
}

function ChatToolStatus({ tools }: { tools: ChatToolCall[] }) {
  useLanguage();
  return <div className="chat-tool-status" role="status" aria-live="polite">
    {tools.map(tool => {
      const status = chatToolStatusLabel(tool);
      return <div className={`chat-tool-row tone-${status.tone}`} key={tool.id}>
        {status.tone === 'running'
          ? <span className="chat-tool-spinner" aria-hidden="true"/>
          : <Icon name={status.tone === 'success' ? 'check' : status.tone === 'failed' ? 'info' : 'search'} size={14}/>}
        <span>{status.label}</span>
      </div>;
    })}
  </div>;
}

interface SearchSourceMenuState { source: ConversationSource; anchor: AnchorRect; trigger: HTMLButtonElement; keyboard: boolean }

function SearchOperation({ tool }: { tool: ChatToolCall }) {
  useLanguage();
  const { openOperations, setOperationOpen, target, sourcesFor } = useContext(ChatSourceContext);
  const sources = sourcesFor(tool);
  const open = openOperations.has(tool.id);
  const status = searchOperationStatus(tool);
  const stateClass = status.tone === 'success' ? 'completed' : status.tone === 'failed' ? 'failed' : status.tone === 'muted' ? 'cancelled' : 'running';
  const activeTarget = target?.operationId === tool.id ? target : undefined;
  const details = useRef<HTMLDetailsElement>(null);
  const items = useRef(new Map<string, HTMLButtonElement>());
  const [highlighted, setHighlighted] = useState<string>();
  const [menu, setMenu] = useState<SearchSourceMenuState>();
  const [menuOpen, setMenuOpen] = useState(false);
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
    menuCloseTimer.current = setTimeout(() => setMenu(undefined), 260);
    if (restoreFocus) menu?.trigger.focus({ preventScroll: true });
  };
  const chooseSource = (event: React.MouseEvent<HTMLButtonElement>, source: ConversationSource) => {
    if (menuOpen && menu?.source.id === source.id) { closeMenu(); return; }
    cancelMenuOpen();
    clearTimeout(menuCloseTimer.current);
    setMenuOpen(false);
    setMenu({ source, anchor: rectOf(event.currentTarget), trigger: event.currentTarget, keyboard: event.detail === 0 });
    menuOpenFrames.current.first = requestAnimationFrame(() => {
      menuOpenFrames.current.second = requestAnimationFrame(() => {
        menuOpenFrames.current = {};
        setMenuOpen(true);
      });
    });
  };
  const openSource = () => {
    if (!menu) return;
    window.open(menu.source.url, '_blank', 'noopener,noreferrer');
    closeMenu(true);
  };
  useEffect(() => () => { cancelMenuOpen(); clearTimeout(menuCloseTimer.current); }, []);
  useEffect(() => {
    if (details.current && details.current.open !== open) details.current.open = open;
  }, [open]);
  useEffect(() => {
    if (!open || !activeTarget) return;
    setHighlighted(activeTarget.sourceId);
    const reveal = setTimeout(() => {
      const item = items.current.get(activeTarget.sourceId);
      item?.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
      item?.focus({ preventScroll: true });
    }, 40);
    const fade = setTimeout(() => setHighlighted(undefined), 1600);
    return () => { clearTimeout(reveal); clearTimeout(fade); setHighlighted(undefined); };
  }, [open, activeTarget?.sourceId, activeTarget?.nonce]);
  return <details ref={details}
    className={`timeline-tool-group search-operation ${tool.state === 'running' ? 'is-running' : ''}`}
    onToggle={event => { const next = event.currentTarget.open; setOperationOpen(tool.id, next); if (!next) closeMenu(); }}>
    <summary>
      <span className="timeline-disclosure"><Icon name="chevron" size={14}/></span>
      <strong>{t("搜索资料")}</strong>
      {status.tone !== 'success' && <em className={`tool-state state-${stateClass}`}>{status.label}</em>}
    </summary>
    <div className="search-operation-body">
      {status.tone === 'failed' && tool.errorMessage && <p className="search-operation-error">{tool.errorMessage}</p>}
      {sources.length > 0
        ? <div className="sources-list">{sources.map(source => <button type="button" key={source.id}
          ref={element => { if (element) items.current.set(source.id, element); else items.current.delete(source.id); }}
          className={`source-item ${highlighted === source.id ? 'is-targeted' : ''} ${menuOpen && menu?.source.id === source.id ? 'is-active' : ''}`}
          onClick={event => chooseSource(event, source)} title={source.url}
          aria-haspopup="menu" aria-expanded={menuOpen && menu?.source.id === source.id}
          aria-label={t("查看资料 {0}：{1}", source.ordinal, source.title)}>
          <b>{source.ordinal}</b><span>{source.title}</span>{source.domain && <small>{source.domain}</small>}
        </button>)}</div>
        : <p className="search-operation-empty">{searchOperationEmptyText(tool)}</p>}
    </div>
    {menu && <SearchSourceMenu state={menu} open={menuOpen} onClose={closeMenu} onOpen={openSource}/>}
  </details>;
}

function SearchSourceMenu({ state, open, onClose, onOpen }: {
  state: SearchSourceMenuState; open: boolean; onClose(restoreFocus?: boolean): void; onOpen(): void;
}) {
  useLanguage();
  const menu = useRef<HTMLDivElement>(null);
  const firstItem = useRef<HTMLButtonElement>(null);
  const host = state.source.domain;
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
    aria-label={t("来源 {0}：{1}", state.source.ordinal, state.source.title)} aria-hidden={!open} inert={!open}>
    <div className="source-menu-info" aria-hidden="true"><b>{state.source.title}</b><small>{host}</small></div>
    <button ref={firstItem} role="menuitem" onClick={onOpen}><Icon name="external"/>{t("打开网页")}</button>
  </div>, document.body);
}

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
  if (!raw) return t("代码");
  return t(codeLanguageLabels[raw.toLowerCase()] ?? raw.charAt(0).toUpperCase() + raw.slice(1));
}

function CodeBlock({ language, children }: { language?: string; children: React.ReactNode }) {
  useLanguage();
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
      <button type="button" className="code-copy" onClick={() => void copy()} aria-label={t("复制代码")}>
        <Icon name={copyState === 'copied' ? 'check' : 'copy'} size={13}/>
        {copyState === 'copied' ? t("已复制") : copyState === 'failed' ? t("复制失败") : t("复制")}
      </button>
    </div>}
    <div className="code-scroll"><pre>{children}</pre></div>
  </div>;
}

function AssistantMarkdown({ content, streaming, citations }: { content: string; streaming: boolean; citations: Citation[] }) {
  useLanguage();
  const { sourcesByOrdinal, onCite } = useContext(ChatSourceContext);
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
        const source = sourcesByOrdinal.get(ordinal);
        const citation = byOrdinal.get(ordinal);
        const title = source?.title ?? citation?.title;
        const site = source?.domain ?? source?.siteName ?? citation?.sourceName;
        if (!title && streaming) return <span className="citation-ref pending" aria-hidden="true"><span>{ordinal}</span></span>;
        return <span className={`citation-ref ${title ? '' : 'is-missing'}`}>
          <button type="button" onClick={() => onCite(ordinal)}
            title={title ? site ? `${title} · ${site}` : title : undefined}
            aria-label={title ? t("查看资料 {0}：{1}", ordinal, title) : t("查看引用 {0}", ordinal)}>{ordinal}</button>
        </span>;
      }
    };
  }, [citations, streaming, sourcesByOrdinal, onCite]);
  return <ReactMarkdown remarkPlugins={markdownPlugins} components={components}>{streaming ? hidePartialCitationMarker(content) : content}</ReactMarkdown>;
}

function Composer({ value, onChange, onSend, onAttach, onStop, hasApiKey, sending, disabled = false, focusSignal, placeholder = t("输入消息"), onOpenSettings }: { value: string; onChange(value: string): void; onSend(): void; onAttach?: () => void; onStop?: () => void; hasApiKey: boolean; sending: boolean; disabled?: boolean; focusSignal: number; placeholder?: string; onOpenSettings(): void }) {
  useLanguage();
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
  return <div className="composer-wrap"><div className={`composer ${onAttach ? 'has-tool' : ''}`}>{onAttach && <button className="composer-tool" onClick={onAttach} aria-label={t("添加本地文件")} title={t("添加本地文件")}><Icon name="plus"/></button>}<div className={`composer-editor ${scrollActive ? 'scroll-active' : ''}`}><textarea ref={textarea} value={value} rows={1} placeholder={placeholder} aria-label={t("输入消息")} onScroll={() => { updateThumb(); revealThumb(); }} onChange={event => onChange(event.target.value)} onKeyDown={event => { if (shouldSubmitComposer(event.key, event.shiftKey, event.nativeEvent.isComposing)) { event.preventDefault(); if (canSend) onSend(); } }}/><div ref={rail} className={`textarea-rail ${thumb.visible ? 'has-overflow' : ''}`} aria-hidden="true"><i style={{ height: thumb.size, transform: `translateY(${thumb.offset}px)` }}/></div></div><div className="composer-actions">{onStop && <button className="composer-stop" onClick={onStop} aria-label={t("停止任务")} title={t("停止任务")}><svg width="12" height="12" viewBox="0 0 12 12" aria-hidden="true"><rect x="1" y="1" width="10" height="10" rx="2.5" fill="currentColor"/></svg></button>}<button className={`composer-send ${canSend ? 'ready' : ''}`} disabled={!canSend} onClick={onSend} aria-label={sending ? t("正在发送") : t("发送消息")} title={sending ? t("正在发送") : t("发送")}><Icon name="arrowUp"/></button></div></div>{!hasApiKey && value.trim() ? <button className="key-hint" onClick={onOpenSettings}><Icon name="key" size={14}/>{t("配置 API Key 后即可发送")}</button> : <p>{t("AI 生成的内容可能不准确，请核实重要信息。")}</p>}</div>;
}

function ApprovalComposer({ approvals, busy, onResolve, onStop }: {
  approvals: WorkApproval[]; busy: Set<string>; onResolve(approval: WorkApproval, approved: boolean): void; onStop?: () => void;
}) {
  useLanguage();
  const approval = approvals[0];
  const resolving = busy.has(`approval-${approval.id}`);
  const action = approval.capability === 'execute' ? t("运行外部命令") : approval.capability === 'write' ? t("修改外部文件") : t("读取外部文件");
  return <div className="composer-wrap approval-composer-wrap">
    <section className="approval-composer" role="dialog" aria-label={t("操作确认")} aria-live="polite">
      <div className="approval-composer-head">
        <span className="approval-composer-icon"><Icon name="shield"/></span>
        <strong>{t("允许小猫助手{0}吗？", action)}</strong>
        {approvals.length > 1 && <em>{t("确认请求 1 / {0}", approvals.length)}</em>}
      </div>
      {approval.targetPath && <div className="approval-composer-target"><Icon name={approval.capability === 'execute' ? 'terminal' : 'file'} size={13}/><code title={approval.targetPath}>{approval.targetPath}</code></div>}
      <div className="approval-composer-actions">{onStop && <button className="stop-button" onClick={onStop} disabled={resolving} aria-label={t("停止任务")} title={t("停止任务")}><Icon name="stop"/></button>}<button className="secondary-button" disabled={resolving} onClick={() => onResolve(approval, false)}>{t("拒绝")}</button><button className="primary-button" disabled={resolving} onClick={() => onResolve(approval, true)}>{resolving ? t("处理中…") : t("允许")}</button></div>
    </section>
    <p>{t("确认后任务会自动继续。")}</p>
  </div>;
}

function DraftConversation({ mode, onMode, value, onChange, onSend, onAttach, hasApiKey, sending, focusSignal, onOpenSettings }: {
  mode: 'chat' | 'work'; onMode(mode: 'chat' | 'work'): void; value: string; onChange(value: string): void;
  onSend(): void; onAttach(): void; hasApiKey: boolean; sending: boolean; focusSignal: number; onOpenSettings(): void;
}) {
  useLanguage();
  const workSelected = mode === 'work';
  return <div className="draft-conversation">
    <div className="draft-hero">
      <BrandMark large/>
      <h1>{draftGreetingForHour(mode, new Date().getHours())}</h1>
      <div className={`mode-selector mode-${mode}`} role="group" aria-label={t("对话模式")}>
        <span className="mode-slider" aria-hidden="true"/>
        <button type="button" className={mode === 'chat' ? 'selected' : ''} onClick={() => onMode('chat')} aria-pressed={mode === 'chat'}><Icon name="chat"/><span><b>Chat</b><small>{t("日常对话")}</small></span></button>
        <button type="button" className={mode === 'work' ? 'selected' : ''} onClick={() => onMode('work')} aria-pressed={mode === 'work'}><Icon name="work"/><span><b>Work</b><small>{t("本地任务")}</small></span></button>
      </div>
      <p className="mode-note">{workSelected ? t("文件整理、资料汇总和成果交付") : t("问答、写作和日常交流")}</p>
    </div>
    <Composer value={value} onChange={onChange} onSend={onSend} onAttach={workSelected ? onAttach : undefined} hasApiKey={hasApiKey} sending={sending} focusSignal={focusSignal} placeholder={workSelected ? t("要完成什么，交给我") : t("想聊什么，告诉我")} onOpenSettings={onOpenSettings}/>
  </div>;
}
function RenameDialog({ open, anchor, conversation, busy, onClose, onRename }: { open: boolean; anchor?: AnchorRect; conversation?: ConversationSummary; busy: boolean; onClose(): void; onRename(title: string): void }) {
  useLanguage();
  const [title, setTitle] = useState('');
  useEffect(() => { if (open) setTitle(conversation?.title ?? ''); }, [open, conversation?.id]);
  const valid = Boolean(title.trim()) && title.trim() !== conversation?.title;
  return <AnchoredSurface open={open} anchor={anchor} onClose={onClose} titleId="rename-title" className="compact-dialog"><form onSubmit={event => { event.preventDefault(); if (valid) onRename(title.trim()); }}><div className="surface-heading"><div><h2 id="rename-title">{t("重命名")}</h2><p>{t("为这段对话取一个更容易识别的名称。")}</p></div><button type="button" className="icon-button" onClick={onClose} aria-label={t("关闭")}><Icon name="close"/></button></div><label className="line-field"><span>{t("对话名称")}</span><input data-autofocus value={title} maxLength={80} onChange={event => setTitle(event.target.value)}/></label><div className="surface-actions"><button type="button" className="text-button" onClick={onClose}>{t("取消")}</button><button className="primary-button" disabled={!valid || busy}>{busy ? t("保存中…") : t("保存")}</button></div></form></AnchoredSurface>;
}
function DeleteDialog({ open, anchor, conversation, busy, onClose, onConfirm }: { open: boolean; anchor?: AnchorRect; conversation?: ConversationSummary; busy: boolean; onClose(): void; onConfirm(): void }) {
  useLanguage();
  return <AnchoredSurface open={open} anchor={anchor} onClose={onClose} titleId="delete-title" className="compact-dialog delete-dialog"><div className="surface-heading"><div><h2 id="delete-title">{t("删除对话？")}</h2><p>{t("“{0}”及其中的消息将无法恢复。", conversation?.title)}</p></div></div><div className="surface-actions"><button className="text-button" data-autofocus onClick={onClose}>{t("取消")}</button><button className="danger-button" disabled={busy} onClick={onConfirm}>{busy ? t("删除中…") : t("删除")}</button></div></AnchoredSurface>;
}

function LanguageSelector({ value, disabled, onChange }: { value: LanguagePreference; disabled: boolean; onChange(value: LanguagePreference): void }) {
  useLanguage();
  const [open, setOpen] = useState(false);
  const anchor = useRef<HTMLDivElement>(null);
  const trigger = useRef<HTMLButtonElement>(null);
  const menu = useRef<HTMLDivElement>(null);
  const menuId = React.useId();
  const options: { value: LanguagePreference; label: string }[] = [
    { value: 'system', label: t('跟随系统') },
    { value: 'zh-CN', label: '简体中文' },
    { value: 'en', label: 'English' }
  ];
  const close = (restoreFocus = false) => {
    setOpen(false);
    if (restoreFocus) trigger.current?.focus({ preventScroll: true });
  };
  useEffect(() => {
    if (!open) return;
    // Let Chromium apply visibility and remove inert before focusing the menu.
    let focusFrame = 0;
    const frame = requestAnimationFrame(() => {
      focusFrame = requestAnimationFrame(() => {
        menu.current?.querySelector<HTMLElement>('[aria-checked="true"]')?.focus({ preventScroll: true });
      });
    });
    const outside = (event: PointerEvent) => {
      if (event.target instanceof Node && !anchor.current?.contains(event.target)) close();
    };
    document.addEventListener('pointerdown', outside);
    return () => { cancelAnimationFrame(frame); cancelAnimationFrame(focusFrame); document.removeEventListener('pointerdown', outside); };
  }, [open]);
  const navigate = (event: React.KeyboardEvent) => {
    if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); close(true); return; }
    if (event.key === 'Tab') { close(true); return; }
    if (!['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) return;
    event.preventDefault();
    const items = Array.from(menu.current?.querySelectorAll<HTMLButtonElement>('[role="menuitemradio"]') ?? []);
    const current = items.findIndex(item => item === document.activeElement);
    const next = event.key === 'Home' ? 0 : event.key === 'End' ? items.length - 1
      : (current + (event.key === 'ArrowUp' ? -1 : 1) + items.length) % items.length;
    items[next]?.focus({ preventScroll: true });
  };
  return <div ref={anchor} className="language-dropdown" onBlur={event => {
    if (!event.currentTarget.contains(event.relatedTarget as Node | null)) close();
  }}>
    <button ref={trigger} type="button" className="language-select" disabled={disabled}
      aria-label={t('界面语言')} aria-haspopup="menu" aria-expanded={open} aria-controls={menuId}
      onClick={() => setOpen(current => !current)} onKeyDown={event => {
        if (event.key === 'ArrowDown' || event.key === 'ArrowUp') { event.preventDefault(); setOpen(true); }
        if (event.key === 'Escape') close();
      }}><span>{options.find(option => option.value === value)?.label}</span><span className={`language-chevron ${open ? 'is-open' : ''}`}><Icon name="chevron" size={18}/></span></button>
    <div ref={menu} id={menuId} className={`language-menu ${open ? 'is-open' : ''}`} role="menu"
      aria-label={t('界面语言')} aria-hidden={!open} inert={!open} onKeyDown={navigate}>
      {options.map(option => <button key={option.value} type="button" role="menuitemradio" tabIndex={-1}
        aria-checked={value === option.value} className={value === option.value ? 'selected' : ''}
        onClick={() => { close(true); if (option.value !== value) onChange(option.value); }}>
        <span>{option.label}</span>{value === option.value ? <Icon name="check" size={18}/> : <span className="language-check-space"/>}
      </button>)}
    </div>
  </div>;
}

type SettingsSection = 'general' | 'service' | 'device';
function SettingsPage({ settings, backend, onboarding, busy, onBack, onSave }: { settings: PublicSettings; backend?: BackendInfo; onboarding: boolean; busy: boolean; onBack(): void; onSave(patch: Partial<PublicSettings> & { apiKey?: string; searchApiKey?: string; asrApiKey?: string }, successText: string): Promise<boolean> }) {
  const { preference } = useLanguage();
  const [languageError, onSaveError] = useState<string>();
  const [languageSaving, setLanguageSaving] = useState(false);
  const saveLanguage = async (value: LanguagePreference) => {
    onSaveError(undefined);
    setLanguageSaving(true);
    try { await changeLanguage(value); }
    catch (reason) { onSaveError(errorText(reason)); }
    finally { setLanguageSaving(false); }
  };
  const initialSection: SettingsSection = onboarding ? 'service' : 'general';
  const [requestedSection, setRequestedSection] = useState<SettingsSection>(initialSection);
  const [displayedSection, setDisplayedSection] = useState<SettingsSection>(initialSection);
  const [sectionPhase, setSectionPhase] = useState<'steady' | 'out' | 'in'>('steady');
  const sectionVersion = useRef(0);
  const [apiKey, setApiKey] = useState('');
  const [searchApiKey, setSearchApiKey] = useState('');
  const [asrApiKey, setAsrApiKey] = useState('');
  const [chatModel, setChatModel] = useState(settings.chatModel);
  const [workModel, setWorkModel] = useState(settings.workModel);
  const [asrModel, setAsrModel] = useState(settings.asrModel);
  const [screenTimeout, setScreenTimeout] = useState(settings.screenTimeoutSeconds);
  const [webSearch, setWebSearch] = useState(settings.webSearchEnabled);
  const [modelApiUrl, setModelApiUrl] = useState(settings.modelApiUrl);
  const [autoReconnect, setAutoReconnect] = useState(settings.autoReconnect);
  useEffect(() => { setChatModel(settings.chatModel); setWorkModel(settings.workModel); setAsrModel(settings.asrModel); setModelApiUrl(settings.modelApiUrl); setScreenTimeout(settings.screenTimeoutSeconds); setWebSearch(settings.webSearchEnabled); setAutoReconnect(settings.autoReconnect); }, [settings]);
  const chooseSection = (section: SettingsSection) => {
    const current = ++sectionVersion.current;
    setRequestedSection(section);
    if (section === displayedSection) { setSectionPhase('steady'); return; }
    setSectionPhase('out');
    setTimeout(() => { if (sectionVersion.current !== current) return; setDisplayedSection(section); setSectionPhase('in'); requestAnimationFrame(() => { if (sectionVersion.current === current) setSectionPhase('steady'); }); }, 90);
  };
  const toggleWeb = async (value: boolean) => { const previous = webSearch; setWebSearch(value); if (!await onSave({ webSearchEnabled: value }, value ? t("联网搜索已开启") : t("联网搜索已关闭"))) setWebSearch(previous); };
  const toggleReconnect = async (value: boolean) => { const previous = autoReconnect; setAutoReconnect(value); if (!await onSave({ autoReconnect: value }, value ? t("自动重连已开启") : t("自动重连已关闭"))) setAutoReconnect(previous); };
  const saveAi = async () => {
    if (await onSave({ apiKey: apiKey || undefined, searchApiKey: searchApiKey || undefined, chatModel: chatModel.trim(), workModel: workModel.trim(), modelApiUrl: modelApiUrl.trim() }, t("AI 服务设置已保存"))) {
      setApiKey(''); setSearchApiKey('');
    }
  };
  const changeScreenTimeout = async (value: number) => {
    const previous = screenTimeout; setScreenTimeout(value);
    if (!await onSave({ screenTimeoutSeconds: value }, t("卡片设置已保存"))) setScreenTimeout(previous);
  };
  const aiDirty = Boolean(apiKey || searchApiKey || chatModel.trim() !== settings.chatModel || workModel.trim() !== settings.workModel || modelApiUrl.trim() !== settings.modelApiUrl);
  const asrDirty = Boolean(asrApiKey || (asrModel.trim() && asrModel.trim() !== settings.asrModel));
  const timeoutOptions = [0, 30, 60, 300, 600].includes(screenTimeout) ? [0, 30, 60, 300, 600] : [0, 30, 60, 300, 600, screenTimeout].sort((a, b) => a - b);
  return <section className="settings-page" aria-labelledby="settings-title"><header className="settings-page-header"><button className="icon-button" onClick={onBack} aria-label={t("返回对话")}><Icon name="back"/></button><div><h1 id="settings-title">{t("设置")}</h1>{onboarding && <p>{t("先配置 AI 服务，即可开始对话。")}</p>}</div></header><div className="settings-shell"><nav className="settings-nav" aria-label={t("设置分类")}><span className={`settings-indicator section-${requestedSection}`} aria-hidden="true"/><button className={requestedSection === 'general' ? 'active' : ''} onClick={() => chooseSection('general')}><Icon name="settings"/><span>{t("常规")}</span></button><button className={requestedSection === 'service' ? 'active' : ''} onClick={() => chooseSection('service')}><Icon name="key"/><span>{t("AI 服务")}</span></button><button className={requestedSection === 'device' ? 'active' : ''} onClick={() => chooseSection('device')}><Icon name="device"/><span>{t("AI 卡片")}</span></button></nav><ScrollArea className="settings-scroll" viewportClassName="settings-content"><div className={`settings-section-content phase-${sectionPhase}`}>
    {displayedSection === 'general' && <section><SettingsHeading title={t("应用偏好")} /><SettingsSection title={t("界面")}><div className="setting-row"><div className="setting-name"><b>{t('界面语言')}</b></div><div className="setting-control"><LanguageSelector value={preference} disabled={languageSaving} onChange={value => void saveLanguage(value)}/></div></div>{languageError && <p className="language-error" role="alert">{languageError}</p>}</SettingsSection>{backend && <SettingsSection title={t("运行诊断")}><div className="setting-row"><div className="setting-name"><b>{t("后端版本")}</b></div><div className="setting-control"><code className="diagnostic-value">{backend.version}</code></div></div><div className="setting-row"><div className="setting-name"><b>{t("后端 PID")}</b></div><div className="setting-control"><code className="diagnostic-value">{backend.processId}</code></div></div><div className="setting-row"><div className="setting-name"><b>{t("SQLite 数据库")}</b><small>{t("开发版与安装版可能使用不同位置")}</small></div><div className="setting-control"><code className="diagnostic-value" title={backend.databasePath}>{backend.databasePath}</code></div></div></SettingsSection>}</section>}
    {displayedSection === 'service' && <section><SettingsHeading title={t("服务与模型")} /><form className="settings-form" onSubmit={event => { event.preventDefault(); void saveAi(); }}><SettingsSection title={t("服务账号")}><label className="setting-row"><span className="setting-name"><b>{t("DeepSeek API Key")}</b><small>{settings.hasApiKey ? t("已配置") : t("未配置")}</small></span><span className="setting-control"><span className="settings-input"><input type="password" value={apiKey} onChange={event => setApiKey(event.target.value)} placeholder={settings.hasApiKey ? t("更换 API Key") : t("输入 API Key")} autoComplete="off"/></span></span></label><label className="setting-row"><span className="setting-name"><b>{t("模型 API 地址")}</b><small>{t("兼容 OpenAI Chat Completions")}</small></span><span className="setting-control"><span className="settings-input"><input type="url" value={modelApiUrl} onChange={event => setModelApiUrl(event.target.value)} placeholder="https://api.deepseek.com/chat/completions" autoComplete="off"/></span></span></label></SettingsSection><SettingsSection title={t("默认模型")}><label className="setting-row"><span className="setting-name"><b>{t("Chat 模型")}</b></span><span className="setting-control"><span className="settings-input"><input value={chatModel} onChange={event => setChatModel(event.target.value)}/></span></span></label><label className="setting-row"><span className="setting-name"><b>{t("Work 模型")}</b></span><span className="setting-control"><span className="settings-input"><input value={workModel} onChange={event => setWorkModel(event.target.value)}/></span></span></label></SettingsSection><SettingsSection title={t("联网搜索")}><Switch checked={webSearch} disabled={busy} onChange={value => void toggleWeb(value)} label={t("联网搜索")} description={t("需要最新信息时允许模型搜索网络")}/>{webSearch && <label className="setting-row"><span className="setting-name"><b>{t("DeepSeek 搜索 API Key")}</b><small>{settings.hasSearchApiKey ? t("已配置，可与模型共用") : t("未配置")}</small></span><span className="setting-control"><span className="settings-input"><input type="password" value={searchApiKey} onChange={event => setSearchApiKey(event.target.value)} placeholder={settings.hasSearchApiKey ? t("更换 API Key") : t("留空则使用模型 API Key")} autoComplete="off"/></span></span></label>}</SettingsSection><div className="settings-actions"><button className="primary-button" disabled={busy || !aiDirty || !chatModel.trim() || !workModel.trim() || !modelApiUrl.trim()}>{t("保存更改")}</button></div></form></section>}
    {displayedSection === 'device' && <section><SettingsHeading title={t("语音与显示")} /><SettingsSection title={t("连接偏好")}><Switch checked={autoReconnect} disabled={busy} onChange={value => void toggleReconnect(value)} label={t("自动重连")} description={t("启动后连接已绑定的卡片")}/></SettingsSection><SettingsSection title={t("显示")}><div className="setting-row"><div className="setting-name"><b>{t("自动息屏")}</b><small>{t("选择卡片无操作多久后关闭屏幕")}</small></div><div className="setting-control"><SettingsSelect value={screenTimeout} options={timeoutOptions.map(value => ({ value, label: screenTimeoutLabel(value) }))} disabled={busy} label={t("自动息屏")} onChange={value => void changeScreenTimeout(value)}/></div></div></SettingsSection><SettingsSection title={`${t("语音")} · ${t("高级设置")}`}><label className="setting-row"><span className="setting-name"><b>{t("语音识别 API Key")}</b><small>{settings.hasAsrApiKey ? t("已配置") : t("未配置")}</small></span><span className="setting-control"><span className="settings-input"><input type="password" value={asrApiKey} onChange={event => setAsrApiKey(event.target.value)} placeholder={settings.hasAsrApiKey ? t("更换 API Key") : t("输入 API Key")} autoComplete="off"/></span></span></label><label className="setting-row"><span className="setting-name"><b>{t("语音识别模型")}</b></span><span className="setting-control setting-control-with-action"><span className="settings-input"><input value={asrModel} onChange={event => setAsrModel(event.target.value)}/></span><button className="quiet-save" disabled={busy || !asrDirty} onClick={event => { event.preventDefault(); void onSave({ asrApiKey: asrApiKey || undefined, asrModel: asrModel.trim() }, t("卡片设置已保存")).then(saved => { if (saved) setAsrApiKey(''); }); }}>{t("保存")}</button></span></label></SettingsSection></section>}
  </div></ScrollArea></div></section>;
}
function SettingsHeading({ title }: { title: string }) {
  useLanguage(); return <div className="settings-heading"><h2>{title}</h2></div>; }
function SettingsSection({ title, children }: { title: string; children: React.ReactNode }) {
  useLanguage(); return <section className="settings-group"><h3>{title}</h3><div>{children}</div></section>;
}
function Switch({ checked, disabled, onChange, label, description }: { checked: boolean; disabled?: boolean; onChange(value: boolean): void; label: string; description: string }) {
  useLanguage(); return <label className="setting-row switch-row"><span className="setting-name"><b>{label}</b><small>{description}</small></span><span className="setting-control"><input type="checkbox" role="switch" checked={checked} disabled={disabled} onChange={event => onChange(event.target.checked)}/><i aria-hidden="true"><u/></i></span></label>; }
function SettingsSelect({ value, options, disabled, label, onChange }: { value: number; options: { value: number; label: string }[]; disabled?: boolean; label: string; onChange(value: number): void }) {
  const [open, setOpen] = useState(false);
  const close = () => setOpen(false);
  return <div className="settings-dropdown" onBlur={event => { if (!event.currentTarget.contains(event.relatedTarget as Node | null)) close(); }} onKeyDown={event => { if (event.key === 'Escape') { event.preventDefault(); close(); } }}><button type="button" className="settings-select" disabled={disabled} aria-label={label} aria-haspopup="menu" aria-expanded={open} onClick={() => setOpen(current => !current)} onKeyDown={event => { if (event.key === 'ArrowDown' || event.key === 'ArrowUp') { event.preventDefault(); setOpen(true); } }}><span>{options.find(option => option.value === value)?.label}</span><Icon name="chevron" size={17}/></button><div className={`settings-menu ${open ? 'is-open' : ''}`} role="menu" aria-label={label} aria-hidden={!open} inert={!open}>{options.map(option => <button key={option.value} type="button" role="menuitemradio" tabIndex={open ? 0 : -1} aria-checked={option.value === value} className={option.value === value ? 'selected' : ''} onClick={() => { close(); if (option.value !== value) onChange(option.value); }}><span>{option.label}</span>{option.value === value ? <Icon name="check" size={17}/> : <span className="menu-check-space"/>}</button>)}</div></div>;
}
function screenTimeoutLabel(seconds: number) {
  if (seconds === 0) return t("永不");
  if (seconds === 30) return t("30 秒");
  if (seconds === 60) return t("1 分钟");
  if (seconds === 300) return t("5 分钟");
  if (seconds === 600) return t("10 分钟");
  return `${seconds} ${t("秒")}`;
}

function DevicePanel({ open, bootstrap, busy, run, onClose, onOpenConversation, onRefresh, onNotice }: { open: boolean; bootstrap: BootstrapState; busy: boolean; run<T>(name: string, action: () => Promise<T>): Promise<T | undefined>; onClose(): void; onOpenConversation(id: string): void; onRefresh(): void; onNotice(text: string, tone?: NoticeState['tone']): void }) {
  useLanguage();
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
  const connect = async (item: CatDevice) => { const result = await run('device', () => invoke('devices.connect', { address: item.address, name: item.name })); onRefresh(); if (result !== undefined) onNotice(t("正在连接 AI 卡片")); };
  const follow = async () => { const result = await run('device', () => invoke('devices.followDesktop')); onRefresh(); if (result !== undefined) onNotice(t("AI 卡片将跟随当前对话")); };
  const forget = async () => { const result = await run('device', () => invoke('devices.forget')); onRefresh(); if (result !== undefined) { setConfirmForget(false); onNotice(t("已忘记这张卡片")); } };
  const scan = async () => { await run('device', () => invoke('devices.scan')); onRefresh(); };
  return <div className={`drawer-backdrop ${visible ? 'is-open' : ''}`} onPointerDown={event => { if (event.target === event.currentTarget) onClose(); }}><aside className="device-panel" role="dialog" aria-modal="true" aria-labelledby="device-panel-title"><ScrollArea className="drawer-scroll" viewportClassName="drawer-content"><div className="panel-header"><div><h2 id="device-panel-title">{t("AI 卡片")}</h2><p>{t("语音输入与随身显示")}</p></div></div><section className="device-status"><div className={`device-visual ${device.isConnected ? 'online' : ''}`}><Icon name="device" size={25}/><span/></div><div><span className="eyebrow">{t("连接状态")}</span><h3>{device.isConnected ? device.pairedDeviceName ?? t("AI 卡片") : device.hasPairedDevice ? t("正在等待已绑定卡片") : t("尚未连接卡片")}</h3><p>{device.isConnected ? t("已连接，可以开始对话") : t(device.detail)}</p></div></section>{device.isConnected && <section className="panel-section"><h3>{t("卡片当前对话")}</h3><div className="bound-conversation"><Icon name="chat"/><span><b>{active?.title ?? t("对话")}</b><small>{device.followDesktop ? t("正在跟随电脑") : t("卡片独立对话")}</small></span></div><div className="panel-actions">{active && <button className="secondary-button" onClick={() => onOpenConversation(active.id)}>{t("打开")}</button>}<button className="primary-button" disabled={device.followDesktop || bootstrap.activeConversation?.conversation.mode !== 'chat' || busy} onClick={() => void follow()}>{t("跟随当前对话")}</button></div></section>}{!device.isConnected && <section className="panel-section"><div className="section-title"><h3>{t("附近的设备")}</h3><button className="text-button" disabled={busy} onClick={() => void scan()}><Icon name="refresh" size={15}/>{busy ? t("扫描中…") : t("重新扫描")}</button></div><div className="device-list">{device.devices.length ? device.devices.map(item => <button key={item.address} disabled={busy} onClick={() => void connect(item)}><span><b>{item.name}</b><small>{item.addressText}</small></span><em>{item.rssi} dBm</em><Icon name="chevron" size={16}/></button>) : <div className="device-empty"><Icon name="device"/><span>{deviceDiscoveryMessage(device)}</span></div>}</div></section>}<Diagnostics device={device}/>{device.hasPairedDevice && !confirmForget && <button className="text-danger" onClick={() => setConfirmForget(true)}>{t("忘记这张卡片")}</button>}{confirmForget && <div className="inline-confirm"><p>{t("忘记后需要重新扫描和绑定。")}</p><div><button className="text-button" onClick={() => setConfirmForget(false)}>{t("取消")}</button><button className="danger-button" disabled={busy} onClick={() => void forget()}>{t("确认忘记")}</button></div></div>}</ScrollArea></aside></div>;
}

function Diagnostics({ device }: { device: BootstrapState['device'] }) {
  useLanguage();
  const [open, setOpen] = useState(false);
  return <section className={`diagnostics ${open ? 'is-open' : ''}`}>
    <button className="diagnostics-toggle" onClick={() => setOpen(value => !value)} aria-expanded={open} aria-controls="device-diagnostics-content">
      <span>{t("高级诊断")}</span><Icon name="chevron" size={15}/>
    </button>
    <div className="diagnostics-disclosure" id="device-diagnostics-content" aria-hidden={!open}><div><dl><div><dt>{t("状态")}</dt><dd>{t(device.status)}</dd></div><div><dt>{t("详情")}</dt><dd>{t(device.detail)}</dd></div><div><dt>PDU</dt><dd>{device.maxPduSize || '—'}</dd></div></dl></div></div>
  </section>;
}

function GlobalAlert({ text, onClose }: { text: string; onClose(): void }) {
  useLanguage(); return <div className="global-alert" role="alert"><span>{t(text)}</span><button className="icon-button" onClick={onClose} aria-label={t("关闭错误提示")}><Icon name="close"/></button></div>; }
function Notice({ notice }: { notice?: NoticeState }) {
  useLanguage(); return <div className={`notice ${notice ? 'visible' : ''} ${notice?.tone === 'danger' ? 'danger' : ''}`} role="status" aria-live="polite"><Icon name={notice?.tone === 'danger' ? 'info' : 'check'}/><span>{notice?.text ?? ''}</span></div>; }
function groupConversations(items: ConversationSummary[]) { const result = new Map<string, ConversationSummary[]>(), now = new Date(), startToday = new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime(); for (const item of items) { const updated = new Date(item.updatedAt).getTime(), age = startToday - updated, label = item.isPinned ? t("已置顶") : updated >= startToday ? t("今天") : age < 2 * 86400000 ? t("昨天") : age < 7 * 86400000 ? t("最近 7 天") : t("更早"); result.set(label, [...(result.get(label) ?? []), item]); } return [...result].map(([label, groupItems]) => ({ label, items: groupItems })); }
function safeHttpUrl(value?: string): value is string { if (!value) return false; try { return ['http:', 'https:'].includes(new URL(value).protocol); } catch { return false; } }
function errorText(reason: unknown) { return reason instanceof Error ? reason.message : String(reason); }

createRoot(document.getElementById('root')!).render(<React.StrictMode><App/></React.StrictMode>);
