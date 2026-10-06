import { EmptyState, PageHeader } from '@commercial/shared';
import { Store } from 'lucide-react';

export function PortalHomePage() {
  return (
    <>
      <PageHeader title="Hoş geldiniz" description="B2B portal kabuğu — Phase 0 Foundation." />
      <EmptyState
        icon={<Store className="h-5 w-5" aria-hidden />}
        title="Portal içerikleri sonraki fazlarda"
        description="Bakiye, ekstre, fatura, katalog, teklif ve sipariş deneyimi yol haritasındaki fazlarla eklenecek."
      />
    </>
  );
}
