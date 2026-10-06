import {
  flexRender,
  getCoreRowModel,
  getSortedRowModel,
  useReactTable,
  type ColumnDef,
  type SortingState,
} from '@tanstack/react-table';
import { ArrowDown, ArrowUp } from 'lucide-react';
import { useState } from 'react';
import { cn } from '../../lib/cn';

/**
 * Foundation table on TanStack Table (client-side sorting). Server pagination, URL state and export
 * arrive with the first real list screens (Phase 2+).
 */
export function DataTable<TData>({
  columns,
  data,
  emptyText = 'Kayıt yok',
  getRowId,
}: {
  columns: ColumnDef<TData, unknown>[];
  data: TData[];
  emptyText?: string;
  getRowId?: (row: TData, index: number) => string;
}) {
  const [sorting, setSorting] = useState<SortingState>([]);
  // eslint-disable-next-line react-hooks/incompatible-library -- TanStack Table v8 is not React-Compiler-memoizable
  const table = useReactTable({
    data,
    columns,
    state: { sorting },
    onSortingChange: setSorting,
    getCoreRowModel: getCoreRowModel(),
    getSortedRowModel: getSortedRowModel(),
    getRowId,
  });

  return (
    <div className="overflow-x-auto">
      <table className="w-full border-collapse text-sm">
        <thead>
          {table.getHeaderGroups().map((group) => (
            <tr key={group.id} className="border-b border-border">
              {group.headers.map((header) => {
                const sorted = header.column.getIsSorted();
                return (
                  <th
                    key={header.id}
                    scope="col"
                    className="px-4 py-2.5 text-left text-xs font-medium uppercase tracking-[0.06em] text-subtle"
                  >
                    {header.isPlaceholder ? null : header.column.getCanSort() ? (
                      <button
                        type="button"
                        className="inline-flex items-center gap-1 hover:text-fg"
                        onClick={header.column.getToggleSortingHandler()}
                      >
                        {flexRender(header.column.columnDef.header, header.getContext())}
                        {sorted === 'asc' ? <ArrowUp className="h-3 w-3" aria-hidden /> : null}
                        {sorted === 'desc' ? <ArrowDown className="h-3 w-3" aria-hidden /> : null}
                      </button>
                    ) : (
                      flexRender(header.column.columnDef.header, header.getContext())
                    )}
                  </th>
                );
              })}
            </tr>
          ))}
        </thead>
        <tbody>
          {table.getRowModel().rows.length === 0 ? (
            <tr>
              <td colSpan={columns.length} className="px-4 py-8 text-center text-muted">
                {emptyText}
              </td>
            </tr>
          ) : (
            table.getRowModel().rows.map((row) => (
              <tr key={row.id} className={cn('border-b border-border last:border-0 hover:bg-surface-2/50')}>
                {row.getVisibleCells().map((cell) => (
                  <td key={cell.id} className="px-4 py-2.5 text-fg">
                    {flexRender(cell.column.columnDef.cell, cell.getContext())}
                  </td>
                ))}
              </tr>
            ))
          )}
        </tbody>
      </table>
    </div>
  );
}
