import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { createBrowserRouter, RouterProvider } from 'react-router';
import { ApiError } from './api/errors';
import { AppProvider } from './app/AppContext';
import { routes } from './app/router';
import { Session } from './auth/session';
import './index.css';

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      // Client errors (4xx) are answers, not glitches: never retry them.
      retry: (failures, error) => !(error instanceof ApiError && error.status >= 400 && error.status < 500) && failures < 2,
      refetchOnWindowFocus: false,
    },
  },
});

const session = new Session();
const router = createBrowserRouter(routes);

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <AppProvider session={session}>
        <RouterProvider router={router} />
      </AppProvider>
    </QueryClientProvider>
  </StrictMode>,
);
