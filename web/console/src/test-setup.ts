import { cleanup } from '@testing-library/react';
import { afterEach } from 'vitest';
import { clearKey } from './api/key';

afterEach(() => {
  cleanup();
  clearKey();
});
