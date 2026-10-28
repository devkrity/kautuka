import React from 'react';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import { Input } from '@/components/ui/input';

type RoleFilter = 'all' | 'elevated' | 'standard';
type StatusFilter = 'all' | 'active' | 'inactive';

interface UsersFilterCardProps {
  search: string;
  roleFilter: RoleFilter;
  statusFilter: StatusFilter;
  pageSize: number;
  shownCount: number;
  totalCount: number;
  onSearchChange: (value: string) => void;
  onRoleFilterChange: (value: RoleFilter) => void;
  onStatusFilterChange: (value: StatusFilter) => void;
  onPageSizeChange: (value: number) => void;
}

export function UsersFilterCard({
  search,
  roleFilter,
  statusFilter,
  pageSize,
  shownCount,
  totalCount,
  onSearchChange,
  onRoleFilterChange,
  onStatusFilterChange,
  onPageSizeChange,
}: Readonly<UsersFilterCardProps>) {
  return (
    <Card className="card-solid">
      <CardHeader>
        <CardTitle>Search and Filter</CardTitle>
        <CardDescription>Filter users and paginate through results.</CardDescription>
      </CardHeader>
      <CardContent className="space-y-3">
        <div className="grid grid-cols-1 gap-3 md:grid-cols-4">
          <Input
            type="search"
            name="user-directory-search"
            autoComplete="off"
            value={search}
            onChange={(e) => onSearchChange(e.target.value)}
            placeholder="Search by username, id, or email"
          />
          <select
            title="Filter role"
            className="input"
            value={roleFilter}
            onChange={(e) => onRoleFilterChange(e.target.value as RoleFilter)}
          >
            <option value="all">All Roles</option>
            <option value="elevated">Admin</option>
            <option value="standard">User</option>
          </select>
          <select
            title="Filter status"
            className="input"
            value={statusFilter}
            onChange={(e) => onStatusFilterChange(e.target.value as StatusFilter)}
          >
            <option value="all">All Statuses</option>
            <option value="active">Active</option>
            <option value="inactive">Inactive</option>
          </select>
          <select
            title="Users per page"
            className="input"
            value={String(pageSize)}
            onChange={(e) => onPageSizeChange(Math.max(1, Number(e.target.value) || 5))}
          >
            <option value="5">5 Per Page</option>
            <option value="10">10 Per Page</option>
            <option value="20">20 Per Page</option>
          </select>
        </div>
        <p className="text-xs text-muted-foreground">
          Showing {shownCount} of {totalCount} matching users.
        </p>
      </CardContent>
    </Card>
  );
}
