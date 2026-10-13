import { QueryClientProvider } from '@tanstack/react-query';
import { RouterProvider } from '@tanstack/react-router';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { createAppRouter, createQueryClient } from './router';
import './styles/tokens.css';
import './styles/base.css';

const container = document.getElementById('root');
if (!container) throw new Error('index.html has no #root');

createRoot(container).render(
  <StrictMode>
    <QueryClientProvider client={createQueryClient()}>
      <RouterProvider router={createAppRouter()} />
    </QueryClientProvider>
  </StrictMode>,
);
