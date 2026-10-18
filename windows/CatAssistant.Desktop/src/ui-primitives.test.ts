import { describe, expect, it } from 'vitest';
import { anchoredTransformOrigin } from './ui-primitives';

describe('anchored surface geometry', () => {
  it('uses the trigger center as the transform origin inside the surface', () => {
    expect(anchoredTransformOrigin(
      { left: 700, top: 80, width: 120, height: 36 },
      { left: 300, top: 200, width: 400, height: 240 }
    )).toBe('460px -102px');
  });

  it('keeps different menu items as distinct origins without translating the surface', () => {
    const surface = { left: 300, top: 200, width: 400, height: 240 };
    expect(anchoredTransformOrigin(
      { left: 700, top: 80, width: 120, height: 36 },
      surface
    )).toBe('460px -102px');
    expect(anchoredTransformOrigin(
      { left: 700, top: 116, width: 120, height: 36 },
      surface
    )).toBe('460px -66px');
  });
});
