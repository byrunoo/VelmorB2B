import { AppShell, AuthShell, DevSessionBadge, DeveloperStatusPage, type NavGroup } from '@commercial/shared';
import { Activity, Home } from 'lucide-react';
import { createBrowserRouter } from 'react-router';
import { PortalHomePage } from './PortalHomePage';
import { NotFoundPage } from './NotFoundPage';

// One B2B app serves many portals/sales channels; the channel is resolved server-side from the
// host/session in Phase 1+ — never from a hard-coded provider or tenant in the bundle.
const navigation: NavGroup[] = [
  { items: [{ label: 'Ana Sayfa', to: '/', icon: Home, end: true }] },
  { label: 'Geliştirici', items: [{ label: 'Sistem Durumu', to: '/dev/status', icon: Activity }] },
];

export const router = createBrowserRouter([
  {
    path: '/',
    element: (
      <AuthShell principalType="CUSTOMER">
        <AppShell
          productName="Commercial"
          surfaceLabel="B2B Portal"
          surfaceTone="accent"
          navigation={navigation}
          userSlot={<DevSessionBadge principalType="CUSTOMER" />}
        />
      </AuthShell>
    ),
    children: [
      { index: true, element: <PortalHomePage /> },
      { path: 'dev/status', element: <DeveloperStatusPage surface="B2B Portal" /> },
      { path: '*', element: <NotFoundPage /> },
    ],
  },
]);
