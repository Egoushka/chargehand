import { QueryClientProvider } from '@tanstack/react-query';
import { RouterProvider } from '@tanstack/react-router';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { createAppRouter, createQueryClient } from './router';
import { applyPreferences } from './shell/preferences';
import './styles/tokens.css';
import './styles/bundle.css';
import './styles/base.css';
import './styles/ui.css';
import './shell/shell.css';

const container = document.getElementById('root');
if (!container) throw new Error('index.html has no #root');

applyPreferences();

createRoot(container).render(
  <StrictMode>
    <QueryClientProvider client={createQueryClient()}>
      <RouterProvider router={createAppRouter()} />
    </QueryClientProvider>
  </StrictMode>,
);
