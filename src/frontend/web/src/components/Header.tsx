import { BrandMark } from '@/components/BrandMark';
import { Button } from '@/components/ui/button';
import { Separator } from '@/components/ui/separator';
import { useApprovalInvalidationSync, usePendingApprovals } from '@/features/approvals';
import { useCurrentUser, UserProfileDialog, UserVersionInfo } from '@/features/auth';
import { ROLE_ADMIN } from '@/features/settings';
import { cn } from '@/lib/utils';
import {
  BarChart3,
  ChevronsLeft,
  ChevronsRight,
  CloudDownload,
  GitBranch,
  LayoutDashboard,
  LogOut,
  MemoryStick,
  MessageSquare,
  Moon,
  Plug,
  Settings,
  ShieldCheck,
  Sparkles,
  Sun,
  Timer,
} from 'lucide-react';
import React from 'react';
import { Link, useLocation } from 'react-router-dom';

interface HeaderProps {
  theme: 'light' | 'dark';
  onToggleTheme: () => void;
  authenticated?: boolean;
  onLogout?: () => void;
  currentVersion?: string;
  canCheckForUpdates?: boolean;
  isCheckingForUpdates?: boolean;
  onCheckForUpdates?: () => void;
  updateTitle?: string;
  updateDescription?: string;
  updateTone?: 'neutral' | 'available' | 'busy' | 'error';
  availableVersion?: string;
  releaseChannel?: string;
  canApplyUpdate?: boolean;
  isApplyingUpdate?: boolean;
  onApplyUpdate?: () => void;
  canRollbackUpdate?: boolean;
  isRollingBackUpdate?: boolean;
  onRollbackUpdate?: () => void;
}

const navItems: Array<{
  to: string;
  label: string;
  icon: typeof LayoutDashboard;
  adminOnly?: boolean;
  experimental?: boolean;
}> = [
  { to: '/dashboard', label: 'Dashboard', icon: LayoutDashboard },
  { to: '/workspace', label: 'Workspace', icon: MessageSquare },
  { to: '/workflows', label: 'Workflows', icon: GitBranch },
  { to: '/approvals', label: 'Approvals', icon: ShieldCheck },
  { to: '/autopilot', label: 'Auto-Pilot', icon: Timer },
  { to: '/connectors', label: 'Connectors', icon: Plug },
  { to: '/skills', label: 'Skills', icon: Sparkles },
  { to: '/memories', label: 'Memories', icon: MemoryStick },
  { to: '/usage', label: 'Usage', icon: BarChart3 },
  { to: '/settings', label: 'Settings', icon: Settings, adminOnly: true },
];

export function Header({
  theme,
  onToggleTheme,
  authenticated = false,
  onLogout,
  currentVersion,
  canCheckForUpdates = false,
  isCheckingForUpdates = false,
  onCheckForUpdates,
  updateTitle,
  updateDescription,
  updateTone = 'neutral',
  availableVersion,
  releaseChannel,
  canApplyUpdate = false,
  isApplyingUpdate = false,
  onApplyUpdate,
  canRollbackUpdate = false,
  isRollingBackUpdate = false,
  onRollbackUpdate,
}: Readonly<HeaderProps>) {
  const [collapsed, setCollapsed] = React.useState(false);
  const location = useLocation();

  // Invalidation SSE drives pending badge refresh; no clock polling.
  useApprovalInvalidationSync(authenticated, 'mine');
  const { data: pendingList = [] } = usePendingApprovals('mine', {
    enabled: authenticated,
    refetchOnWindowFocus: true,
  });
  const pendingApprovals = pendingList.length;

  const { data: user } = useCurrentUser();
  const userInitials = user?.displayName
    ? user.displayName
        .split(' ')
        .map((n) => n[0])
        .join('')
        .toUpperCase()
    : user?.username?.[0]?.toUpperCase() || '?';

  return (
    <aside
      className={cn(
        'flex h-screen flex-col gap-4 border-r border-sidebar-border bg-sidebar p-4 transition-all',
        collapsed ? 'w-16' : 'w-16 md:w-72'
      )}
    >
      <div className={cn('flex items-center justify-between', collapsed && 'flex-col gap-2')}>
        <Link to="/" aria-label="Kautuka home">
          <BrandMark word={!collapsed} markClassName={collapsed ? 'h-7 w-7 text-base' : undefined} />
        </Link>
        <Button
          variant="ghost"
          size="icon"
          onClick={() => setCollapsed(!collapsed)}
          className="h-9 w-9 shrink-0"
          title={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}
        >
          {collapsed ? <ChevronsRight className="h-4 w-4" /> : <ChevronsLeft className="h-4 w-4" />}
        </Button>
      </div>

      <Separator />

      <nav className="flex flex-1 flex-col gap-1 overflow-y-auto">
        {navItems
          .filter((item) => !item.adminOnly || user?.role === ROLE_ADMIN)
          .map((item) => {
            const itemTo =
              item.to === '/workspace' && location.pathname.startsWith('/workspace/')
                ? `${location.pathname}${location.search}`
                : item.to;
            const active =
              location.pathname === item.to || location.pathname.startsWith(item.to + '/');
            return (
              <Link
                key={item.to}
                to={itemTo}
                className={cn(
                  'relative flex items-center gap-3 rounded-md px-4 py-2.5 text-sm font-medium transition-colors',
                  active
                    ? 'bg-primary/10 text-primary'
                    : 'text-foreground/70 hover:bg-primary/5 hover:text-foreground',
                  collapsed && 'justify-center px-0'
                )}
                title={collapsed ? `${item.label}${item.experimental ? ' (experimental)' : ''}` : undefined}
              >
                <item.icon className="h-4 w-4 shrink-0" />
                {!collapsed && (
                  <span className="truncate">
                    {item.label}
                    {item.experimental ? (
                      <span className="section-label ml-1 font-normal">
                        Experimental
                      </span>
                    ) : null}
                  </span>
                )}
                {item.to === '/approvals' && pendingApprovals > 0 && (
                  <span
                    className={cn(
                      'inline-flex h-5 min-w-5 items-center justify-center rounded-full px-1 text-xs font-semibold',
                      active
                        ? 'bg-primary-foreground text-primary-fill'
                        : 'bg-primary-fill text-primary-foreground',
                      collapsed ? 'absolute -right-1 -top-1' : 'ml-auto'
                    )}
                  >
                    {pendingApprovals > 99 ? '99+' : pendingApprovals}
                  </span>
                )}
              </Link>
            );
          })}
      </nav>

      <div className="mt-auto flex flex-col gap-4">
        <div className={cn('flex items-center gap-1', collapsed ? 'flex-col' : 'flex-row px-2')}>
          <Button
            variant="ghost"
            size="icon"
            onClick={onToggleTheme}
            className="h-9 w-9 shrink-0 text-foreground/70 hover:bg-primary/5 hover:text-foreground"
            title={theme === 'light' ? 'Switch to dark mode' : 'Switch to light mode'}
          >
            {theme === 'light' ? <Moon className="h-4 w-4" /> : <Sun className="h-4 w-4" />}
          </Button>

          {authenticated && onLogout && (
            <Button
              variant="ghost"
              size="icon"
              onClick={onLogout}
              className="h-9 w-9 shrink-0 text-foreground/70 hover:bg-primary/5 hover:text-foreground"
              title="Log out"
            >
              <LogOut className="h-4 w-4" />
            </Button>
          )}
        </div>

        <Separator />

        {authenticated && user && (
          <div className="w-full">
            {collapsed && updateTitle ? (
              <Button
                type="button"
                variant="ghost"
                size="icon"
                onClick={() => setCollapsed(false)}
                className="relative mb-2 h-9 w-9 rounded-full text-foreground/70 hover:bg-primary/5 hover:text-foreground"
                title={updateTitle}
              >
                <CloudDownload className="h-4 w-4" />
                <span
                  className={cn(
                    'absolute right-1 top-1 h-2 w-2 rounded-full bg-muted-foreground',
                    updateTone === 'available' && 'bg-attention',
                    updateTone === 'busy' && 'animate-pulse bg-primary',
                    updateTone === 'error' && 'bg-destructive'
                  )}
                />
              </Button>
            ) : null}
            <UserProfileDialog
              collapsed={collapsed}
              userName={user.displayName || user.username}
              userRole={user.role}
              userInitials={userInitials}
              triggerClassName={cn(
                'flex w-full items-center gap-3 transition-all',
                collapsed
                  ? 'justify-center rounded-full p-1 hover:bg-primary/5'
                  : 'rounded-2xl bg-muted/30 p-2 pr-4 hover:bg-muted/60'
              )}
            />
            {!collapsed ? (
              <UserVersionInfo
                className="px-2 pb-2"
                currentVersion={currentVersion}
                canCheckForUpdates={canCheckForUpdates}
                isCheckingForUpdates={isCheckingForUpdates}
                onCheckForUpdates={onCheckForUpdates}
                updateTitle={updateTitle}
                updateDescription={updateDescription}
                updateTone={updateTone}
                availableVersion={availableVersion}
                releaseChannel={releaseChannel}
                canApplyUpdate={canApplyUpdate}
                isApplyingUpdate={isApplyingUpdate}
                onApplyUpdate={onApplyUpdate}
                canRollbackUpdate={canRollbackUpdate}
                isRollingBackUpdate={isRollingBackUpdate}
                onRollbackUpdate={onRollbackUpdate}
              />
            ) : null}
          </div>
        )}
      </div>
    </aside>
  );
}
