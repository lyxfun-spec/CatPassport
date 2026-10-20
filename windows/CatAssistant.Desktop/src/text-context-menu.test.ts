import { describe, expect, it } from 'vitest';
import { textMenuEntries } from './text-context-menu';

describe('textMenuEntries', () => {
  it('offers every editable action when the field has a selection', () => {
    expect(textMenuEntries('editable', true).map(entry => entry.id)).toEqual(['cut', 'copy', 'paste', 'selectAll']);
  });

  it('hides cut and copy when the field has no selection', () => {
    expect(textMenuEntries('editable', false).map(entry => entry.id)).toEqual(['paste', 'selectAll']);
  });

  it('offers copy and select all for selected message text', () => {
    expect(textMenuEntries('selection', true).map(entry => entry.id)).toEqual(['copy', 'selectAll']);
  });

  it('offers only select all for unselected message text', () => {
    expect(textMenuEntries('message', false).map(entry => entry.id)).toEqual(['selectAll']);
  });
});
