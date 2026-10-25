import { useSyncExternalStore } from 'react';
import { setTranslationLanguage, t, type LanguagePreference } from './i18n';

let settings = window.catAssistant.languageSettings;
function applyLanguage(): void {
  setTranslationLanguage(settings.language);
  document.documentElement.lang = settings.language;
  document.title = t('小猫助手');
}
applyLanguage();
const listeners = new Set<() => void>();
const subscribe = (listener: () => void) => { listeners.add(listener); return () => { listeners.delete(listener); }; };

export function useLanguage() {
  return useSyncExternalStore(subscribe, () => settings);
}

export async function changeLanguage(preference: LanguagePreference): Promise<void> {
  settings = await window.catAssistant.setLanguage(preference);
  applyLanguage();
  listeners.forEach(listener => listener());
}
