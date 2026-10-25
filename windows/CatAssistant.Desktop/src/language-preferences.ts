import fs from 'node:fs';
import path from 'node:path';
import { isLanguagePreference, type LanguagePreference } from './i18n';

export function readLanguagePreference(filePath: string): LanguagePreference {
  try {
    const value: unknown = JSON.parse(fs.readFileSync(filePath, 'utf8'));
    return isLanguagePreference(value) ? value : 'system';
  } catch { return 'system'; }
}

export function saveLanguagePreference(filePath: string, preference: LanguagePreference): void {
  fs.mkdirSync(path.dirname(filePath), { recursive: true });
  const temporaryPath = `${filePath}.tmp`;
  fs.writeFileSync(temporaryPath, JSON.stringify(preference), 'utf8');
  fs.renameSync(temporaryPath, filePath);
}
