import { describe, expect, it, vi } from 'vitest';
import {
  clearKey,
  getKey,
  getKeyState,
  setIdentity,
  setKey,
  signedIn,
  signOut,
  subscribeKey,
} from './key';

describe('the key store', () => {
  it('keeps the key in sessionStorage and nowhere else', () => {
    setKey('chk1.abc.secret');
    expect(sessionStorage.getItem('chargehand.key')).toBe('chk1.abc.secret');
    expect(getKey()).toBe('chk1.abc.secret');
    expect(localStorage.length).toBe(0);
    expect(document.cookie).toBe('');
  });

  it('forgets the key on sign-out and says why when the server refused it', () => {
    setKey('k');
    clearKey('The server rejected the key.');
    expect(sessionStorage.getItem('chargehand.key')).toBeNull();
    expect(getKeyState()).toEqual({
      key: null,
      identity: false,
      notice: 'The server rejected the key.',
    });
    setKey('k2');
    expect(getKeyState().notice).toBeNull();
  });

  it('tells subscribers about every change', () => {
    const listener = vi.fn();
    const unsubscribe = subscribeKey(listener);
    setKey('k');
    clearKey();
    unsubscribe();
    setKey('k2');
    expect(listener).toHaveBeenCalledTimes(2);
  });

  it('still works in memory when storage is blocked', () => {
    const blocked = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('blocked');
    });
    setKey('in-memory');
    expect(getKey()).toBe('in-memory');
    blocked.mockRestore();
  });

  it('signs in by identity with no key, and sign-out leaves the page as it is', () => {
    setIdentity();
    expect(getKey()).toBeNull();
    expect(signedIn(getKeyState())).toBe(true);
    expect(sessionStorage.getItem('chargehand.key')).toBeNull();
    signOut();
    expect(signedIn(getKeyState())).toBe(false);
  });
});
