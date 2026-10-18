import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import ReactMarkdown from 'react-markdown';
import { describe, expect, it } from 'vitest';
import { citationOrdinal, hidePartialCitationMarker, remarkCitations, stripCitationMarkers } from './citations';

type Node = { type: string; value?: string; url?: string; children?: Node[] };
const run = (tree: Node) => { remarkCitations()(tree); return tree; };

describe('citation markers', () => {
  it('strips markers for plain text', () => {
    expect(stripCitationMarkers('降温近10℃[ref_1][ref_5]。')).toBe('降温近10℃。');
  });

  it('hides an unfinished marker at the end of a streaming reply', () => {
    expect(hidePartialCitationMarker('天气晴[ref_1')).toBe('天气晴');
    expect(hidePartialCitationMarker('天气晴[re')).toBe('天气晴');
    expect(hidePartialCitationMarker('天气晴[ref_1]')).toBe('天气晴[ref_1]');
  });

  it('parses citation hrefs only', () => {
    expect(citationOrdinal('#cite-3')).toBe(3);
    expect(citationOrdinal('#cite-0')).toBeUndefined();
    expect(citationOrdinal('https://example.com')).toBeUndefined();
  });

  it('turns markers in text into citation links, merging split text nodes', () => {
    const tree = run({ type: 'root', children: [{ type: 'paragraph', children: [{ type: 'text', value: '降温[ref_1]' }, { type: 'text', value: '[ref_5]。' }] }] });
    expect(tree.children![0].children).toEqual([
      { type: 'text', value: '降温' },
      { type: 'link', url: '#cite-1', children: [{ type: 'text', value: '1' }] },
      { type: 'link', url: '#cite-5', children: [{ type: 'text', value: '5' }] },
      { type: 'text', value: '。' }
    ]);
  });

  it('drops markers inside existing links and leaves code untouched', () => {
    const tree = run({ type: 'root', children: [{ type: 'paragraph', children: [
      { type: 'link', url: 'https://example.com', children: [{ type: 'text', value: '官网[ref_2]' }] },
      { type: 'inlineCode', value: '[ref_3]' }
    ] }] });
    expect(tree.children![0].children).toEqual([
      { type: 'link', url: 'https://example.com', children: [{ type: 'text', value: '官网' }] },
      { type: 'inlineCode', value: '[ref_3]' }
    ]);
  });

  it('renders markers through react-markdown as citation links', () => {
    const html = renderToStaticMarkup(createElement(ReactMarkdown, { remarkPlugins: [remarkCitations] }, '降温近10℃[ref_1][ref_5]，`[ref_9]`。'));
    expect(html).toBe('<p>降温近10℃<a href="#cite-1">1</a><a href="#cite-5">5</a>，<code>[ref_9]</code>。</p>');
  });
});
