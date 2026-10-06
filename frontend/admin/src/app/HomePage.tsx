import { Button, EmptyState, PageHeader } from '@commercial/shared';
import { Boxes } from 'lucide-react';
import { Link } from 'react-router';

export function HomePage() {
  return (
    <>
      <PageHeader
        title="Genel Bakış"
        description="Commercial Platform yönetim paneli — çok kiracılı, çok şirketli, çok kanallı B2B ticaret."
      />
      <EmptyState
        icon={<Boxes className="h-5 w-5" aria-hidden />}
        title="Phase 0 — Foundation"
        description="Bu fazda yalnızca altyapı hazırlanır. Kiracı, şirket, müşteri, katalog ve sipariş ekranları ilgili fazlarda eklenecek."
      />
      <div>
        <Link to="/dev/status">
          <Button variant="outline">Sistem durumunu görüntüle</Button>
        </Link>
      </div>
    </>
  );
}
