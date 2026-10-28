/**
 * Dashboard feature public API
 */
export * from './api/dashboard';
export * from './api/queries';
export { RecentSessionsList } from './components/RecentSessionsList';
export { RecentTargetsGrid } from './components/RecentTargetsGrid';
export { StatsCardGrid } from './components/StatsCardGrid';
export { ApplianceHealthCard } from './components/ApplianceHealthCard';
export * from './hooks/useDashboardData';
export * from './schemas/dashboardSchema';
export * from './schemas/updateStatusSchema';
export * from './schemas/updateHistorySchema';
export * from './schemas/applianceHealthSchema';

