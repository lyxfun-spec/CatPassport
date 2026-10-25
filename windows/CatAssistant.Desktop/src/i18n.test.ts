import { afterEach, describe, expect, it } from 'vitest';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { english, isLanguagePreference, resolveLanguage, setTranslationLanguage, t } from './i18n';
import { readLanguagePreference, saveLanguagePreference } from './language-preferences';
import { draftGreetingForHour, summarizeWorkToolGroup } from './ui-behavior';
import { textMenuEntries } from './text-context-menu';
import { deviceDiscoveryMessage } from './state';

afterEach(() => setTranslationLanguage('zh-CN'));

describe('interface language', () => {
  it.each(['zh', 'zh-CN', 'zh-SG', 'zh-Hans-CN', 'ZH_cn', 'zh-TW'])('uses Simplified Chinese for system locale %s', locale => {
    expect(resolveLanguage('system', locale)).toBe('zh-CN');
  });
  it.each(['en-US', 'en-GB', 'fr-FR', 'ja-JP', '', 'zho'])('falls back to English for system locale %s', locale => {
    expect(resolveLanguage('system', locale)).toBe('en');
  });
  it('honors an explicit choice regardless of the system language', () => {
    expect(resolveLanguage('en', 'zh-CN')).toBe('en');
    expect(resolveLanguage('zh-CN', 'en-US')).toBe('zh-CN');
    for (const invalid of [undefined, null, 'fr', {}, 'zh', 1]) expect(isLanguagePreference(invalid)).toBe(false);
  });
  it('prefers the actual Windows locale when Electron reports a conflicting preferred language', () => {
    expect(resolveLanguage('system', 'zh-CN', ['en-US'])).toBe('zh-CN');
    expect(resolveLanguage('system', 'en-US', ['zh-CN'])).toBe('en');
    expect(resolveLanguage('system', '', ['zh-Hans-CN'])).toBe('zh-CN');
    expect(resolveLanguage('en', 'zh-CN', ['zh-CN'])).toBe('en');
  });
  it('updates greetings, menus, tool summaries, and device discovery in both languages', () => {
    setTranslationLanguage('en');
    expect(draftGreetingForHour('chat', 9)).toBe('Good morning! What would you like to talk about?');
    expect(draftGreetingForHour('work', 19)).toBe('Good evening! What would you like to accomplish today?');
    expect(textMenuEntries('editable', true).map(entry => entry.label)).toEqual(['Cut', 'Copy', 'Paste', 'Select all']);
    expect(summarizeWorkToolGroup([]).label).toBe('Tool calls · Finished');
    expect(deviceDiscoveryMessage({ status: '未连接', detail: '', isConnected: false, hasPairedDevice: false, maxPduSize: 0, devices: [], followDesktop: true })).toBe('Looking for nearby AI Cards…');
    setTranslationLanguage('zh-CN');
    expect(draftGreetingForHour('chat', 9)).toBe('早上好，想聊些什么？');
    expect(textMenuEntries('message', false)[0].label).toBe('全选');
  });
  it('inserts user text literally and preserves unknown diagnostics', () => {
    setTranslationLanguage('en');
    expect(t('“{0}”及其中的消息将无法恢复。', '中文 {1} $&')).toBe('“中文 {1} $&” and its messages cannot be recovered.');
    expect(t('raw diagnostic 0x123')).toBe('raw diagnostic 0x123');
    expect(t('raw diagnostic {0}')).toBe('raw diagnostic {0}');
  });
  it('provides translations for all literal UI translation calls with matching placeholders', () => {
    for (const file of ['renderer.tsx', 'ui-behavior.ts', 'text-context-menu.ts', 'state.ts', 'main.ts']) {
      const source = fs.readFileSync(new URL(file, import.meta.url), 'utf8');
      for (const match of source.matchAll(/\bt\((['"])(.*?)\1/g)) expect(english[match[2]], `${file}: ${match[2]}`).toBeTruthy();
    }
    for (const [source, translated] of Object.entries(english)) {
      expect(translated.match(/\{\d+\}/g)?.sort() ?? [], source).toEqual(source.match(/\{\d+\}/g)?.sort() ?? []);
    }
  });
});

describe('language preference persistence', () => {
  it('defaults to system and restores explicit choices on subsequent launches', () => {
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'cat-language-'));
    const file = path.join(directory, 'settings', 'ui-language.json');
    try {
      expect(readLanguagePreference(file)).toBe('system');
      for (const choice of ['en', 'zh-CN', 'system'] as const) {
        saveLanguagePreference(file, choice);
        expect(readLanguagePreference(file)).toBe(choice);
      }
      for (const invalid of ['{"language":"en"}', '"fr"', 'broken json']) {
        fs.writeFileSync(file, invalid);
        expect(readLanguagePreference(file)).toBe('system');
      }
    } finally { fs.rmSync(directory, { recursive: true, force: true }); }
  });
});
