import { zodResolver } from '@hookform/resolvers/zod';
import type { ColumnDef } from '@tanstack/react-table';
import { Activity, Send } from 'lucide-react';
import { useState, type ReactNode } from 'react';
import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { ApiError } from '../api/client';
import type { HealthCheckEntry } from '../api/schemas';
import { useApiVersion, useCreateOutboxPing, useHealth, useOutboxPingStatus } from '../api/system';
import { DataTable } from '../components/data/DataTable';
import { StatusPill, type Tone } from '../components/ui/Badge';
import { Button } from '../components/ui/Button';
import { Card, CardBody, CardHeader, StatCard } from '../components/ui/Card';
import { Field, Input } from '../components/ui/Form';
import { ErrorState, Spinner } from '../components/ui/States';
import { env } from '../config/env';
import { PageHeader } from '../layout/PageHeader';

const healthTone = (status: string): Tone =>
  status === 'Healthy' ? 'success' : status === 'Degraded' ? 'warning' : 'danger';

const healthColumns: ColumnDef<HealthCheckEntry, unknown>[] = [
  {
    accessorKey: 'name',
    header: 'Bağımlılık',
    cell: (c) => <span className="font-medium">{c.getValue<string>()}</span>,
  },
  {
    accessorKey: 'status',
    header: 'Durum',
    cell: (c) => <StatusPill tone={healthTone(c.getValue<string>())} label={c.getValue<string>()} />,
  },
  {
    accessorKey: 'durationMs',
    header: 'Süre (ms)',
    cell: (c) => <span className="tabular">{c.getValue<number>().toFixed(1)}</span>,
  },
  {
    accessorKey: 'description',
    header: 'Not',
    enableSorting: false,
    cell: (c) => <span className="text-muted">{c.getValue<string | null>() ?? '—'}</span>,
  },
];

const PingFormSchema = z.object({
  note: z.string().trim().max(200, 'En fazla 200 karakter.').optional(),
});
type PingForm = z.infer<typeof PingFormSchema>;

/**
 * Phase 0 developer screen (shared by admin and B2B): API version, liveness/readiness of
 * PostgreSQL/Redis/Hangfire and an end-to-end outbox round trip (API → outbox → Hangfire → handler).
 */
export function DeveloperStatusPage({ surface }: { surface: string }) {
  const version = useApiVersion();
  const live = useHealth('live');
  const ready = useHealth('ready');

  return (
    <>
      <PageHeader
        title="Sistem Durumu"
        description={`Geliştirici ekranı · ${surface} · yalnızca Commercial API ile konuşur`}
        actions={
          <Button
            variant="outline"
            size="sm"
            onClick={() => void Promise.all([version.refetch(), live.refetch(), ready.refetch()])}
          >
            <Activity className="h-3.5 w-3.5" aria-hidden />
            Yenile
          </Button>
        }
      />

      <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
        <StatCard label="API sürümü" value={version.data?.version ?? '—'} hint={version.data?.service} />
        <StatCard
          label="API ortamı"
          value={version.data?.environment ?? '—'}
          hint={`.NET ${version.data?.runtime ?? '—'}`}
        />
        <StatCard
          label="Canlılık"
          value={
            live.data ? <StatusPill tone={healthTone(live.data.status)} label={live.data.status} /> : '—'
          }
          hint="/health/live"
        />
        <StatCard
          label="Hazırlık"
          value={
            ready.data ? <StatusPill tone={healthTone(ready.data.status)} label={ready.data.status} /> : '—'
          }
          hint="/health/ready"
        />
      </div>

      {version.error ? <ApiErrorState error={version.error} title="API'ye ulaşılamadı" /> : null}

      <Card>
        <CardHeader
          title="Hazırlık kontrolleri"
          description="PostgreSQL, Redis ve Hangfire storage (15 sn'de bir yenilenir)"
        />
        {ready.isPending ? (
          <CardBody>
            <Spinner />
          </CardBody>
        ) : ready.error ? (
          <CardBody>
            <ApiErrorState error={ready.error} title="Readiness okunamadı" />
          </CardBody>
        ) : (
          <DataTable columns={healthColumns} data={ready.data?.checks ?? []} getRowId={(row) => row.name} />
        )}
      </Card>

      {env.isDevelopment ? <OutboxRoundTripCard /> : null}
    </>
  );
}

function OutboxRoundTripCard() {
  const [messageId, setMessageId] = useState<string | null>(null);
  const create = useCreateOutboxPing();
  const status = useOutboxPingStatus(messageId);
  const form = useForm<PingForm>({ resolver: zodResolver(PingFormSchema), defaultValues: { note: '' } });

  const onSubmit = form.handleSubmit(async (values) => {
    try {
      const accepted = await create.mutateAsync({ note: values.note || undefined });
      setMessageId(accepted.messageId);
    } catch (error) {
      // Server-side validation errors map back onto the form fields.
      if (error instanceof ApiError) {
        for (const [field, messages] of Object.entries(error.fieldErrors)) {
          if (field === 'note') form.setError('note', { message: messages[0] });
        }
      }
    }
  });

  return (
    <Card>
      <CardHeader
        title="Outbox gidiş-dönüş testi"
        description="Uygulama transaction'ı → outbox → Hangfire dispatcher → idempotent handler"
      />
      <CardBody className="space-y-4">
        <form className="flex flex-col gap-3 sm:flex-row sm:items-end" onSubmit={onSubmit} noValidate>
          <div className="flex-1">
            <Field
              label="Not"
              htmlFor="ping-note"
              hint="İsteğe bağlı, en fazla 200 karakter"
              error={form.formState.errors.note?.message}
            >
              <Input
                id="ping-note"
                placeholder="ör. smoke test"
                invalid={!!form.formState.errors.note}
                {...form.register('note')}
              />
            </Field>
          </div>
          <Button type="submit" disabled={create.isPending}>
            <Send className="h-4 w-4" aria-hidden />
            Ping gönder
          </Button>
        </form>

        {create.error && !(create.error instanceof ApiError && create.error.status === 400) ? (
          <ApiErrorState error={create.error} title="Ping oluşturulamadı" />
        ) : null}

        {messageId ? (
          <dl className="grid gap-3 rounded-md border border-border bg-surface-2/40 p-3 text-sm sm:grid-cols-2 lg:grid-cols-4">
            <Item label="Mesaj" value={<span className="tabular text-xs">{messageId}</span>} />
            <Item
              label="Outbox durumu"
              value={
                status.data ? (
                  <StatusPill
                    tone={status.data.outboxStatus === 'Processed' ? 'success' : 'info'}
                    label={status.data.outboxStatus}
                  />
                ) : (
                  <Spinner label="Bekleniyor…" />
                )
              }
            />
            <Item label="Deneme" value={<span className="tabular">{status.data?.attempts ?? '—'}</span>} />
            <Item
              label="Handler etkisi"
              value={status.data?.handlerEffectRecorded ? 'Kaydedildi (1 kez)' : '—'}
            />
            <Item
              label="Correlation ID"
              value={<span className="tabular text-xs">{status.data?.correlationId ?? '—'}</span>}
            />
          </dl>
        ) : null}
      </CardBody>
    </Card>
  );
}

function Item({ label, value }: { label: string; value: ReactNode }) {
  return (
    <div className="min-w-0">
      <dt className="text-xs text-subtle">{label}</dt>
      <dd className="mt-0.5 truncate text-fg">{value}</dd>
    </div>
  );
}

function ApiErrorState({ error, title }: { error: Error; title: string }) {
  return (
    <ErrorState
      title={title}
      description={
        error instanceof ApiError ? `${error.code}: ${error.message}` : 'Ağ hatası veya API çalışmıyor.'
      }
      correlationId={error instanceof ApiError ? error.correlationId : undefined}
    />
  );
}
