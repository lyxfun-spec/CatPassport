import { afterEach, expect, it, vi } from 'vitest';

afterEach(() => { vi.unstubAllGlobals(); vi.resetModules(); });

it('applies the startup language and updates the document only after a preference is saved', async () => {
  const setLanguage = vi.fn().mockResolvedValue({ preference: 'zh-CN', language: 'zh-CN' });
  const document = { documentElement: { lang: '' }, title: '' };
  vi.stubGlobal('document', document);
  vi.stubGlobal('window', { catAssistant: { languageSettings: { preference: 'system', language: 'en' }, setLanguage } });
  const { changeLanguage } = await import('./language-store');
  expect(document.documentElement.lang).toBe('en');
  expect(document.title).toBe('Cat Assistant');
  await changeLanguage('zh-CN');
  expect(setLanguage).toHaveBeenCalledWith('zh-CN');
  expect(document.documentElement.lang).toBe('zh-CN');
  expect(document.title).toBe('小猫助手');
  setLanguage.mockRejectedValueOnce(new Error('disk full'));
  await expect(changeLanguage('en')).rejects.toThrow('disk full');
  expect(document.documentElement.lang).toBe('zh-CN');
  expect(document.title).toBe('小猫助手');
});
