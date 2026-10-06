import { AppShell, AuthShell, DevSessionBadge, DeveloperStatusPage, type NavGroup } from '@commercial/shared';
import { Activity, LayoutDashboard } from 'lucide-react';
import { createBrowserRouter } from 'react-router';
import { HomePage } from './HomePage';
import { NotFoundPage } from './NotFoundPage';

// Phase 0 navigation only. Business areas are added by their phase (A018 guards src paths).
const navigation: NavGroup[] = [
  { items: [{ label: 'Genel Bakış', to: '/', icon: LayoutDashboard, end: true }] },
  { label: 'Geliştirici', items: [{ label: 'Sistem Durumu', to: '/dev/status', icon: Activity }] },
];

export const router = createBrowserRouter([
  {
    path: '/',
    element: (
      <AuthShell principalType="WORKFORCE">
        <AppShell
          productName="Commercial"
          surfaceLabel="Yönetim Paneli"
          surfaceTone="brand"
          navigation={navigation}
          userSlot={<DevSessionBadge principalType="WORKFORCE" />}
        />
      </AuthShell>
    ),
    children: [
      { index: true, element: <HomePage /> },
      { path: 'dev/status', element: <DeveloperStatusPage surface="Yönetim" /> },
      { path: '*', element: <NotFoundPage /> },
    ],
  },
]);
