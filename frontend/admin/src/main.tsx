import { ErrorBoundary, QueryProvider, ThemeProvider } from '@commercial/shared';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { RouterProvider } from 'react-router';
import { router } from './app/router';
import './index.css';

const root = document.getElementById('root');
if (!root) throw new Error('#root element missing');

createRoot(root).render(
  <StrictMode>
    <ErrorBoundary>
      <ThemeProvider storageKey="commercial.admin.theme">
        <QueryProvider>
          <RouterProvider router={router} />
        </QueryProvider>
      </ThemeProvider>
    </ErrorBoundary>
  </StrictMode>,
);
