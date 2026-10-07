import { Menu, Moon, PanelLeftClose, PanelLeftOpen, Sun, X, type LucideIcon } from 'lucide-react';
import { useEffect, useState, type ReactNode } from 'react';
import { NavLink, Outlet, useLocation } from 'react-router';
import { env } from '../config/env';
import { cn } from '../lib/cn';
import { readStorage, storageKey, writeStorage } from '../lib/storage';
import { useTheme } from '../providers/themeContext';
import { ErrorBoundary } from '../providers/ErrorBoundary';

export type NavItem = { label: string; to: string; icon: LucideIcon; end?: boolean };
export type NavGroup = { label?: string; items: NavItem[] };

export type AppShellProps = {
  productName: string;
  surfaceLabel: string;
  /** Visually distinguishes surfaces that share the shell (admin = brand, portal = accent). */
  surfaceTone: 'brand' | 'accent';
  navigation: NavGroup[];
  userSlot?: ReactNode;
};

const COLLAPSE_KEY = storageKey('shell', 'sidebar-collapsed');

function readCollapsed(): boolean {
  return readStorage(COLLAPSE_KEY, 'commercial.shell.collapsed') === '1';
}

/**
 * Shared application shell: 264px sidebar (68px icon rail when collapsed), 64px sticky header,
 * mobile drawer below lg. Routes render into <Outlet/> inside an error boundary so one failing page
 * never blanks the shell.
 */
export function AppShell({ productName, surfaceLabel, surfaceTone, navigation, userSlot }: AppShellProps) {
  const [collapsed, setCollapsed] = useState(readCollapsed);
  const [mobileOpen, setMobileOpen] = useState(false);
  const location = useLocation();

  useEffect(() => {
    writeStorage(COLLAPSE_KEY, collapsed ? '1' : '0');
  }, [collapsed]);

  const [lastPath, setLastPath] = useState(location.pathname);
  if (lastPath !== location.pathname) {
    setLastPath(location.pathname);
    setMobileOpen(false);
  }

  return (
    <div className="flex min-h-dvh bg-bg text-fg">
      <aside
        className={cn(
          'sticky top-0 hidden h-dvh shrink-0 flex-col border-r border-border bg-surface transition-[width] duration-200 lg:flex',
          collapsed ? 'w-[68px]' : 'w-[264px]',
        )}
      >
        <SidebarContent
          productName={productName}
          surfaceLabel={surfaceLabel}
          surfaceTone={surfaceTone}
          navigation={navigation}
          collapsed={collapsed}
        />
      </aside>

      {mobileOpen ? (
        <div className="fixed inset-0 z-40 lg:hidden">
          <button
            type="button"
            aria-label="Menüyü kapat"
            className="absolute inset-0 bg-overlay"
            onClick={() => setMobileOpen(false)}
          />
          <aside className="absolute inset-y-0 left-0 flex w-[84vw] max-w-[300px] flex-col bg-surface shadow-pop">
            <div className="absolute top-3 right-3">
              <button
                type="button"
                aria-label="Menüyü kapat"
                className="flex h-9 w-9 items-center justify-center rounded-lg text-muted hover:bg-surface-2"
                onClick={() => setMobileOpen(false)}
              >
                <X className="h-5 w-5" aria-hidden />
              </button>
            </div>
            <SidebarContent
              productName={productName}
              surfaceLabel={surfaceLabel}
              surfaceTone={surfaceTone}
              navigation={navigation}
              collapsed={false}
            />
          </aside>
        </div>
      ) : null}

      <div className="flex min-w-0 flex-1 flex-col">
        {env.appEnv !== 'production' ? (
          <div className="bg-warning-soft px-4 py-1 text-center text-xs font-medium text-warning">
            {env.appEnv.toUpperCase()} ortamı — Phase 0 Foundation (iş ekranları sonraki fazlarda)
          </div>
        ) : null}
        <header className="sticky top-0 z-30 flex h-16 items-center gap-3 border-b border-border bg-surface/85 px-4 backdrop-blur-md sm:px-5">
          <button
            type="button"
            aria-label="Menüyü aç"
            className="flex h-9 w-9 items-center justify-center rounded-lg border border-border text-muted hover:bg-surface-2 lg:hidden"
            onClick={() => setMobileOpen(true)}
          >
            <Menu className="h-[18px] w-[18px]" aria-hidden />
          </button>
          <button
            type="button"
            aria-label={collapsed ? 'Kenar çubuğunu genişlet' : 'Kenar çubuğunu daralt'}
            className="hidden h-9 w-9 items-center justify-center rounded-lg border border-border text-muted hover:bg-surface-2 lg:flex"
            onClick={() => setCollapsed((c) => !c)}
          >
            {collapsed ? (
              <PanelLeftOpen className="h-[18px] w-[18px]" aria-hidden />
            ) : (
              <PanelLeftClose className="h-[18px] w-[18px]" aria-hidden />
            )}
          </button>
          <div className="flex-1" />
          <ThemeToggle />
          {userSlot}
        </header>
        <main className="mx-auto w-full max-w-[1400px] flex-1 space-y-6 p-4 sm:p-5 lg:p-8">
          <ErrorBoundary key={location.pathname}>
            <Outlet />
          </ErrorBoundary>
        </main>
      </div>
    </div>
  );
}

function SidebarContent({
  productName,
  surfaceLabel,
  surfaceTone,
  navigation,
  collapsed,
}: Omit<AppShellProps, 'userSlot'> & { collapsed: boolean }) {
  return (
    <>
      <div
        className={cn(
          'flex h-16 shrink-0 items-center gap-2.5 border-b border-border',
          collapsed ? 'justify-center px-2' : 'px-4',
        )}
      >
        <div
          aria-hidden
          className={cn(
            'flex h-8 w-8 shrink-0 items-center justify-center rounded-md font-display text-sm font-bold',
            surfaceTone === 'brand' ? 'bg-brand text-brand-fg' : 'bg-accent text-accent-fg',
          )}
        >
          {productName.slice(0, 1)}
        </div>
        {!collapsed ? (
          <div className="min-w-0 leading-tight">
            <div className="truncate font-display text-[15px] font-semibold text-fg">{productName}</div>
            <div
              className={cn(
                'truncate text-xs font-medium',
                surfaceTone === 'brand' ? 'text-muted' : 'text-accent',
              )}
            >
              {surfaceLabel}
            </div>
          </div>
        ) : null}
      </div>
      <nav className="flex-1 space-y-4 overflow-y-auto px-2.5 py-4" aria-label="Ana menü">
        {navigation.map((group, index) => (
          <div key={group.label ?? index} className="space-y-0.5">
            {group.label && !collapsed ? (
              <div className="px-2.5 pb-1 text-[11px] font-semibold uppercase tracking-[0.08em] text-subtle">
                {group.label}
              </div>
            ) : null}
            {group.items.map((item) => (
              <NavLink
                key={item.to}
                to={item.to}
                end={item.end}
                title={collapsed ? item.label : undefined}
                className={({ isActive }) =>
                  cn(
                    'relative flex items-center gap-2.5 rounded-md px-2.5 py-[7px] text-[13px] transition-colors',
                    collapsed && 'justify-center',
                    isActive
                      ? 'bg-surface-2 font-semibold text-fg shadow-xs ring-1 ring-border'
                      : 'text-muted hover:bg-surface-2/60 hover:text-fg',
                  )
                }
              >
                {({ isActive }) => (
                  <>
                    {collapsed && isActive ? (
                      <span aria-hidden className="absolute inset-y-1.5 left-0 w-[3px] rounded-r bg-brand" />
                    ) : null}
                    <item.icon
                      className={cn('h-[17px] w-[17px] shrink-0', isActive && 'text-brand')}
                      aria-hidden
                    />
                    {!collapsed ? <span className="truncate">{item.label}</span> : null}
                  </>
                )}
              </NavLink>
            ))}
          </div>
        ))}
      </nav>
    </>
  );
}

function ThemeToggle() {
  const { resolved, toggle } = useTheme();
  return (
    <button
      type="button"
      onClick={toggle}
      aria-label={resolved === 'dark' ? 'Açık temaya geç' : 'Koyu temaya geç'}
      className="flex h-9 w-9 items-center justify-center rounded-lg border border-border text-muted hover:bg-surface-2 hover:text-fg"
    >
      {resolved === 'dark' ? (
        <Sun className="h-[18px] w-[18px]" aria-hidden />
      ) : (
        <Moon className="h-[18px] w-[18px]" aria-hidden />
      )}
    </button>
  );
}
