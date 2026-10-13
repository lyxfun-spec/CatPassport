import { describe, expect, it } from 'vitest';
import { initialState, reduceBackendEvent } from './state';

describe('backend stream state', () => {
  it('assembles incremental chat text and clears it on completion', () => {
    const delta = (text: string) => ({ event: 'conversation.stream' as const, data: { conversationId: 7, phase: 'delta' as const, delta: text } });
    let state = reduceBackendEvent(initialState, delta('你'));
    state = reduceBackendEvent(state, delta('好'));
    expect(state.streamingText).toBe('你好');
    state = reduceBackendEvent(state, { event: 'conversation.stream', data: { conversationId: 7, phase: 'completed' } });
    expect(state.streamingText).toBe('');
  });
});
