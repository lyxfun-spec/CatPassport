export const CITATION_HREF_PREFIX = '#cite-';

const markerPattern = /\[ref_(\d+)\]/g;
const partialMarkerAtEnd = /\[(?:r(?:e(?:f(?:_\d{0,6})?)?)?)?$/;

interface MarkdownNode {
  type: string;
  value?: string;
  url?: string;
  children?: MarkdownNode[];
}

export function stripCitationMarkers(text: string): string {
  return text.replace(markerPattern, '');
}

export function hidePartialCitationMarker(text: string): string {
  return text.replace(partialMarkerAtEnd, '');
}

export function citationOrdinal(href?: string): number | undefined {
  if (!href?.startsWith(CITATION_HREF_PREFIX)) return undefined;
  const ordinal = Number(href.slice(CITATION_HREF_PREFIX.length));
  return Number.isInteger(ordinal) && ordinal > 0 ? ordinal : undefined;
}

export function remarkCitations() {
  return (tree: MarkdownNode) => transform(tree, false);
}

function transform(node: MarkdownNode, insideLink: boolean): void {
  if (!node.children) return;
  const children: MarkdownNode[] = [];
  for (const child of node.children) {
    const previous = children.at(-1);
    if (child.type === 'text' && previous?.type === 'text') previous.value = (previous.value ?? '') + (child.value ?? '');
    else children.push(child.type === 'text' ? { ...child } : child);
  }
  node.children = children.flatMap(child => {
    if (child.type === 'text') return splitText(child.value ?? '', insideLink);
    transform(child, insideLink || child.type === 'link' || child.type === 'linkReference');
    return [child];
  });
}

function splitText(value: string, insideLink: boolean): MarkdownNode[] {
  const nodes: MarkdownNode[] = [];
  let cursor = 0;
  for (const match of value.matchAll(markerPattern)) {
    if (match.index > cursor) nodes.push({ type: 'text', value: value.slice(cursor, match.index) });
    if (!insideLink) nodes.push({ type: 'link', url: CITATION_HREF_PREFIX + match[1], children: [{ type: 'text', value: match[1] }] });
    cursor = match.index + match[0].length;
  }
  if (cursor < value.length) nodes.push({ type: 'text', value: value.slice(cursor) });
  return nodes;
}
