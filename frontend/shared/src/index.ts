// Public surface of @commercial/shared. Apps import ONLY from here (or './styles.css').

export { apiRequest, ApiError, newCorrelationId } from './api/client';
export * from './api/schemas';
export * from './api/system';
export { env, readEnv, type AppEnv } from './config/env';
export { cn } from './lib/cn';
export { STORAGE_NAMESPACE, storageKey, type StorageScope } from './lib/storage';

export { QueryProvider } from './providers/QueryProvider';
export { createQueryClient } from './providers/queryClient';
export { ThemeProvider } from './providers/ThemeProvider';
export { useTheme, type ThemePreference } from './providers/themeContext';
export { ErrorBoundary } from './providers/ErrorBoundary';

export { Button, type ButtonProps } from './components/ui/Button';
export { Card, CardHeader, CardBody, StatCard } from './components/ui/Card';
export { Badge, StatusPill, type Tone } from './components/ui/Badge';
export { Input, Field } from './components/ui/Form';
export { Spinner, EmptyState, ErrorState } from './components/ui/States';
export { DataTable } from './components/data/DataTable';

export { AppShell, type AppShellProps, type NavGroup, type NavItem } from './layout/AppShell';
export { PageHeader } from './layout/PageHeader';
export { AuthShell, DevSessionBadge } from './layout/AuthShell';
export { DeveloperStatusPage } from './pages/DeveloperStatusPage';
