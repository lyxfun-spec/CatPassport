import { t } from './i18n';
export type TextMenuScene = 'editable' | 'selection' | 'message';
export type TextMenuAction = 'cut' | 'copy' | 'paste' | 'selectAll';

export interface TextMenuEntry { id: TextMenuAction; label: string }

export function textMenuEntries(scene: TextMenuScene, hasSelection: boolean): TextMenuEntry[] {
  if (scene === 'editable') return [
    ...(hasSelection ? [{ id: 'cut' as const, label: t("剪切") }, { id: 'copy' as const, label: t("复制") }] : []),
    { id: 'paste', label: t("粘贴") },
    { id: 'selectAll', label: t("全选") }
  ];
  if (scene === 'selection') return [
    { id: 'copy', label: t("复制") },
    { id: 'selectAll', label: t("全选") }
  ];
  return [{ id: 'selectAll', label: t("全选") }];
}
