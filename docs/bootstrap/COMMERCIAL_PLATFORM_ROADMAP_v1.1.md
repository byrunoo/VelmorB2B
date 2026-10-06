# COMMERCIAL PLATFORM ROADMAP v1.1

**Durum:** ACTIVE  
**Sürüm:** 1.1  
**Tarih:** 06.10.2026  
**Bağlı Mimari:** Commercial Platform Frozen Architecture v1.0  
**Amaç:** Frozen Architecture v1.0 kararlarını değiştirmeden uygulama sırasını, faz kapılarını, paralel iş akışlarını ve teslim kriterlerini tanımlamak.

---

# 1. ROADMAP'İN KONUMU

Bu doküman **Commercial Platform Frozen Architecture v1.0**'ın yerine geçmez.

Normatif öncelik:

1. `docs/architecture/frozen-architecture-v1.0.md`
2. Onaylanmış ADR'ler
3. Aktif faz mandate'i
4. Architecture Test Contract
5. Bu roadmap

Roadmap'in görevi:

- hangi fazın ne zaman başlayacağını,
- hangi işlerin paralel yürütüleceğini,
- hangi kararların hangi fazı bloke ettiğini,
- acceptance gate'leri,
- pilot ve release sırasını

tanımlamaktır.

Mimari kararlar roadmap üzerinden değiştirilemez.

---

# 2. TEMEL EXECUTION İLKELERİ

## 2.1 Tek aktif ana faz

Commercial Platform repository'sinde ana geliştirme fazı:

```text
docs/architecture/active-phase.json
```

ile belirlenir.

Örnek:

```json
{
  "phase": 0,
  "name": "Foundation",
  "mandate": "docs/phases/phase-0/PHASE0_IMPLEMENTATION_MANDATE.md"
}
```

## 2.2 Faz geçişi

Bir fazdan diğerine geçiş yalnızca **Phase Kickoff PR** ile yapılır.

Kickoff PR:

- önceki fazın acceptance gate'lerinin geçtiğini kanıtlar,
- yeni phase mandate'ini ekler/aktive eder,
- `active-phase.json` değerini değiştirir,
- başka işlevsel değişiklik içermez.

Phase Kickoff PR, Architecture Test Contract'taki normal değişiklik politikasının faz numarası açısından tanımlı istisnasıdır.

## 2.3 Scope guard

Aktif faz dışında business capability geliştirilmez.

Örneğin Phase 0 sırasında:

- Customer,
- Product,
- Quote,
- Order,
- ERP Connector,
- B2B business workflow

geliştirilemez.

## 2.4 Paralel workstream istisnası

Aşağıdaki iş ana fazlardan bağımsız paralel yürütülebilir:

```text
KAPI-1 — Kumpara Public API Fit-Gap & Readiness
```

Bu iş Commercial Platform business scope'unu genişletmez; Kumpara repository'sinde yürütülür.

---

# 3. ROADMAP ÖZETİ

```text
PHASE 0
Foundation + Architecture Enforcement
│
├──────────── PARALLEL TRACK: KAPI-1 ─────────────┐
│                                                 │
▼                                                 │
ADR-0001 Tenant Isolation Gate                    │
│                                                 │
▼                                                 │
PHASE 1 Identity / Tenant / Security              │
│                                                 │
▼                                                 │
PHASE 2 Customer + Catalog                        │
│                                                 │
▼                                                 │
PHASE 3 Pricing + Inventory                       │
│                                                 │
├── TARGET: Reform Data Source decided            │
│                                                 │
▼                                                 │
PHASE 4 Quote                                     │
│                                                 │
▼                                                 │
PHASE 5 Order Core + ERP-Free Operation           │
│                                                 │
▼                                                 ▼
DG-01 FINAL GATE                           KAPI-1 READY
│
├── Reform = Kumpara
│      ↓
│   PHASE 6A Kumpara Connector
│
└── Reform != Kumpara
       ↓
    PHASE 6A Kumpara Connector
       +
    PHASE 6B Reform Pilot Read Connector

                ↓
PHASE 7 B2B Read Experience
                ↓
PHASE 8 B2B Quote / Order
                ↓
PHASE 9 External Order Write
                ↓
PHASE 10 Credit & Commercial Control
                ↓
PHASE 11 Shipment
                ↓
PHASE 12 Change / Cancellation / Sales Return
                ↓
PHASE 13 Second Full External Connector
                ↓
PHASE 14 Local Connector Agent — if required
                ↓
PHASE 15 Hardening & Commercial Release
```

---

# 4. PARALLEL TRACK — KAPI-1

## Amaç

Commercial Platform Phase 6'ya geldiğinde Kumpara Public API tarafında bekleme oluşmasını engellemek.

KAPI-1 **Phase 6'da başlamaz**.

Phase 0 ile birlikte başlar.

## Repository

```text
Kumpara / BusinessOS
src/BusinessOS.PublicApi
```

## Owner

```text
Kumpara Backend / Public API Integration Workstream
```

## Kapsam

### KAPI-1.1 — Customer-specific pricing

Şu bağlamda fiyat çözümleme doğrulanır:

```text
customer
product
quantity
currency
date
```

Eksikse Public API capability eklenir.

### KAPI-1.2 — Credit / payment terms

Gerekli alanlar doğrulanır:

```text
creditLimit
availableCredit
paymentTerm
overduePolicy
blocked
```

### KAPI-1.3 — Invoice documents

Commercial Platform'ın ihtiyaç duyduğu:

- invoice metadata,
- PDF,
- XML veya belge referansı

erişimi doğrulanır.

### KAPI-1.4 — Real-time validation

Gerekli ise:

```text
price
inventory
customer status
credit
overdue
```

için validation contract hazırlanır.

### KAPI-1.5 — Idempotency retention

Kumpara'nın `Idempotency-Key` kayıtlarının retention süresi belgelenir.

Kural:

```text
retention >= maksimum otomatik retry
          + dead-letter manual replay penceresi
```

### KAPI-1.6 — External reference lookup

Order create için immutable Commercial Platform referansı Kumpara'da saklanabilir ve API üzerinden aranabilir olmalıdır.

Örnek:

```text
CP-ORD-2026-00001234
```

Capability:

```text
ORDER_LOOKUP_BY_EXTERNAL_REFERENCE
```

### KAPI-1.7 — Safe create retry contract

Aşağıdaki senaryolar contract test ile doğrulanır:

```text
same key replay
network timeout
response lost after commit
concurrent retry
application restart
retention expiry
external reference lookup
manual dead-letter replay
```

## KAPI-1 Completion Gate

KAPI-1 ancak şu koşullarda READY sayılır:

- gerekli read capability'leri doğrulanmış,
- eksik Public API endpoint'leri tamamlanmış,
- idempotency retention belgelenmiş,
- external reference lookup mevcut,
- safe retry contract testleri geçmiş.

KAPI-1 READY olmadan Phase 6A acceptance tamamlanamaz.

---

# 5. DECISION GATE — ADR-0001 TENANT ISOLATION

## Zamanlama

Phase 0 tamamlandıktan sonra, Phase 1 kickoff öncesinde zorunludur.

## Karar

Aşağıdaki yaklaşımlardan biri veya onaylanmış kombinasyonu seçilir:

```text
EF Core Global Query Filters
PostgreSQL Row-Level Security
```

## Neden erken?

RLS seçilirse:

- connection/session context,
- connection pooling,
- DbContext initialization,
- tenant session variables

altyapısı başlangıçtan etkilenir.

## Gate

ADR-0001 `ACCEPTED` olmadan Phase 1 kickoff yapılamaz.

---

# 6. PHASE 0 — FOUNDATION & ARCHITECTURE ENFORCEMENT

## Amaç

Business capability geliştirmeden güvenilir proje zemini oluşturmak.

## Teslimatlar

### Backend

```text
.NET 10 LTS
ASP.NET Core
PostgreSQL
Redis
Hangfire
Transactional Outbox
OpenTelemetry
Serilog
OpenAPI
```

### Frontend

```text
React
TypeScript
Vite

admin
b2b
shared
```

### Repository structure

```text
src/
  Commercial.Domain/
  Commercial.Application/
  Commercial.Infrastructure/
  Commercial.Api/
  Commercial.Worker/
  Commercial.Connectors.Abstractions/

frontend/
  admin/
  b2b/
  shared/

tests/
  Commercial.UnitTests/
  Commercial.IntegrationTests/
  Commercial.ArchitectureTests/
  Commercial.ContractTests/
```

### Governance files

```text
CLAUDE.md
docs/architecture/frozen-architecture-v1.0.md
docs/architecture/active-phase.json
ARCHITECTURE_TEST_CONTRACT.md
PHASE0_IMPLEMENTATION_MANDATE.md
```

### Architecture enforcement

- dependency rules,
- exact provider token scanning,
- type/namespace scanning,
- phase-aware scope guard,
- forbidden Kumpara internal dependency,
- forbidden legacy Sales Ops dependency.

### Contract tests — Phase 0

Yalnız:

```text
Commercial API OpenAPI snapshot
standard error-envelope schema
```

doğrulanır.

Kumpara veya ERP fake contract'ı yazılmaz.

### Transactional Outbox foundation

Minimum model:

```text
Id
OccurredAtUtc
Type
Payload
Status
AttemptCount
NextAttemptAtUtc

LockedBy
LockedUntilUtc
```

Dispatcher:

```text
FOR UPDATE SKIP LOCKED
```

veya eşdeğer lease stratejisi kullanır.

Zorunlu:

- multi-worker ownership,
- lease expiry recovery,
- idempotent handler,
- duplicate execution testleri.

## Phase 0 Gate

Aşağıdakilerin tamamı PASS:

```text
dotnet build
dotnet test
frontend build
architecture tests
contract tests
outbox multi-worker test
observability smoke test
```

Ek olarak:

- bir architecture rule kasıtlı olarak kırılır,
- CI'nin gerçekten fail olduğu kanıtlanır,
- ihlal geri alınır,
- tüm pipeline tekrar PASS olur.

---

# 7. PHASE 1 — IDENTITY / TENANT / SECURITY

## Önkoşul

```text
ADR-0001 = ACCEPTED
```

## Kapsam

```text
Tenant
Company

WORKFORCE principal
CUSTOMER principal

Role
Permission
Scope
```

Server-side security context:

```text
TenantId
CompanyId
CustomerAccountId
PrincipalType
Permissions
```

## Gate

```text
Cross Tenant PASS
Cross Company PASS
Cross Customer PASS
IDOR PASS
Horizontal Privilege Escalation PASS
Role Escalation PASS
```

B2B principal başka customer account verisine erişememelidir.

---

# 8. PHASE 2 — CUSTOMER + CATALOG

## Customer

```text
CustomerAccount
CustomerContact
CustomerAddress
CustomerUserBinding
```

## Catalog

```text
Product
ProductVariant
Unit
Category
Attribute
Image
Document
```

## Data Authority başlangıcı

```text
LOCAL
EXTERNAL
HYBRID
```

policy altyapısı eklenir.

## Gate

Hem LOCAL hem EXTERNAL kaynaklı Customer/Product senaryosu aynı architecture içinde çalışır.

LOCAL enrichment external sync tarafından overwrite edilemez.

---

# 9. PHASE 3 — PRICING + INVENTORY

## Pricing

```text
PriceList
Price
CustomerPrice
PricingRule
QuantityBreak
Currency
ExchangeRate
```

## Inventory

```text
Warehouse
InventoryBalance
Availability
```

Portal gösterimi:

```text
EXACT
STATUS_ONLY
HIDDEN
```

## Reform Data Source Target

**Phase 3 tamamlanmadan önce O-001'in cevaplanması hedeflenir.**

Bu henüz teknik blocker değildir ancak planlama blocker'ıdır.

Sorulacak soru:

> Reform'un gerçek product, price, inventory, account ve invoice source of truth sistemi nedir?

## Gate

Aynı ürün farklı müşterilere farklı fiyat gösterebilir.

LOCAL ve EXTERNAL pricing authority senaryoları çalışır.

---

# 10. PHASE 4 — QUOTE

## Kapsam

```text
Quote
QuoteLine
QuoteVersion
QuoteApproval
QuoteAcceptance
```

Accepted QuoteVersion immutable'dır.

## Gate

Internal kullanıcı teklif oluşturabilir.

B2B tüketimine hazır aynı Quote entity modeli bulunur.

Accepted versiyon silent mutate edilemez.

---

# 11. PHASE 5 — ORDER CORE + ERP-FREE OPERATION

## Order

```text
SalesOrder
SalesOrderLine
OrderApproval
OrderValidation
OrderChangeRequest
OrderCancellationRequest
```

State axes:

```text
BusinessStatus
ApprovalStatus
IntegrationStatus
FulfillmentStatus
CancellationStatus
PaymentStatus
```

Snapshot:

```text
price
discount
tax
currency
exchange rate
product description
unit
commercial terms
```

## ERP-free commercial finance

Minimum:

```text
CommercialAccount
CommercialAccountEntry
ManualPayment
ManualAdjustment
```

Bu muhasebe değildir.

Yasal fatura oluşturulmaz.

## Gate

Hiç ERP bağlı olmadan:

```text
Customer
Catalog
Pricing
Inventory
Quote
Order
Commercial Ledger
```

uçtan uca çalışır.

---

# 12. DG-01 — REFORM DATA SOURCE FINAL GATE

## Zamanlama

Phase 6 başlamadan **zorunlu final karar**.

## Yol A — Reform Kumpara kullanıyor

```text
Pilot source = Kumpara
```

Phase 6A yeterlidir.

## Yol B — Reform başka ERP kullanıyor

Phase 6B aktive edilir.

Kumpara connector geliştirmesi yine devam eder.

## Gate

O-001 OPEN durumundayken Phase 6 kickoff yapılamaz.

---

# 13. PHASE 6A — KUMPARA CONNECTOR + KAPI-1 READINESS

## Önkoşullar

```text
KAPI-1 READY
DG-01 decided
```

## Connector read capability

```text
Customers
Products
Pricing
Inventory

Balance
Ledger
Aging

Invoices
Invoice Documents

Payments
```

## Integration foundation

```text
IntegrationConnection
ExternalObjectMapping
SyncCheckpoint
IntegrationOperation
IntegrationOutbox
IntegrationInbox
DeadLetter
Reconciliation
MappingRule
```

## Gate

Commercial Platform Kumpara database'ine erişmeden yalnız Public API üzerinden gerçek data okuyabilir.

---

# 14. PHASE 6B — REFORM PILOT READ CONNECTOR

Yalnız Reform data source Kumpara değilse aktive edilir.

## Amaç

İlk pilotu full ERP write entegrasyonu bekletmeden gerçek veriye bağlamak.

## Minimum capability

```text
Customers
Products
Prices
Inventory

Balance
Ledger

Invoices
Invoice Documents
```

Order write zorunlu değildir.

## Gate

Phase 7 için Reform gerçek verisi Commercial Platform'a read-only olarak akabilir.

---

# 15. PHASE 7 — B2B READ EXPERIENCE

Bu faz bilinçli olarak ERP write'dan önce gelir.

## Dashboard

```text
Current Balance
Overdue Balance
Open Quotes
Open Orders
Latest Invoices
```

## Catalog

```text
Products
Search
Categories
Technical Attributes
Documents
Customer Price
Stock Visibility
```

## Account

```text
Balance
Ledger
Aging
Date Filters
Debit/Credit
```

## Invoice

```text
List
Detail
Document
PDF/XML where available
```

## Gate

Gerçek Reform müşterisi sipariş oluşturmadan dahi portaldan günlük operasyonel değer elde eder.

---

# 16. PHASE 8 — B2B QUOTE & ORDER

## Quote

```text
Quote List
Quote Detail
Accept
Reject
Comment
```

## Order

```text
Cart
Create Order
History
Detail
Status
```

İlk aşamada order:

```text
LOCAL
```

olarak kabul edilebilir.

External write zorunlu değildir.

## Gate

Internal Sales UI ve B2B Portal aynı Quote/Order entity'lerini kullanır.

Duplicate B2B order domain'i yoktur.

---

# 17. PHASE 9 — EXTERNAL ORDER WRITE

## İlk hedef

Kumpara.

## Akış

```text
Validate
↓
External Reference Lookup
↓
Create
↓
Native/Connector Idempotency
↓
ExternalObjectMapping
```

## Validation

Capability'ye göre:

```text
price
inventory
customer status
credit
overdue
payment terms
```

## Test gate

```text
same key replay
timeout
response loss after commit
concurrent retry
worker restart
retention expiry
external-reference lookup
manual dead-letter replay
```

## Gate

Aynı Commercial Order için duplicate external order üretilemez.

---

# 18. PHASE 10 — CREDIT & COMMERCIAL CONTROL

## Kapsam

```text
CreditLimit
AvailableCredit
OverduePolicy
PaymentTerms
OrderBlock
ApprovalThreshold
```

## Gate

Internal UI ve B2B aynı credit policy engine'i kullanır.

Örnek:

```text
Credit exceeded
→ PENDING_INTERNAL_APPROVAL
```

---

# 19. PHASE 11 — SHIPMENT & FULFILLMENT

## Kapsam

```text
Shipment
ShipmentLine
Partial Shipment
Shipped
Delivered
```

External read projection olarak başlayabilir.

## Gate

Aynı order eş zamanlı olarak örneğin:

```text
businessStatus = CONFIRMED
integrationStatus = ACCEPTED
fulfillmentStatus = PARTIAL
```

durumlarını taşıyabilir.

---

# 20. PHASE 12 — CHANGE / CANCELLATION / SALES RETURN

## Kapsam

```text
OrderChangeRequest
CancellationRequest
CommercialReturnRequest
```

External kabul edilmiş order silent mutate edilmez.

## Not

Bu fazdaki Return:

```text
Sales Return
```

anlamındadır.

Rental/Kiralama iadesi değildir.

---

# 21. PHASE 13 — SECOND FULL EXTERNAL CONNECTOR

## Seçim ilkesi

Teknik kolaylık değil gerçek ticari talep.

Muhtemel:

```text
Logo
Mikro
Netsis
Paraşüt
```

## Amaç

Canonical connector mimarisinin ikinci bağımsız provider ile gerçek doğrulaması.

## Gate

Commercial Domain'e provider-specific branching eklemeden ikinci connector çalışır.

---

# 22. PHASE 14 — LOCAL CONNECTOR AGENT

Yalnız seçilen provider gerektirirse.

## Kapsam

```text
Outbound-only connectivity

Agent registration
Device identity
Certificate
Certificate rotation

Local queue
Offline tolerance

Job execution
Health
Remote diagnostics

ERP version detection

Auto-update
Rollback
```

## Kural

ERP database'ine doğrudan INSERT/UPDATE varsayılan write stratejisi değildir.

---

# 23. PHASE 15 — HARDENING & COMMERCIAL RELEASE

## Security

```text
Isolation suite
Penetration review
Credential rotation
Session security
```

## Reliability

```text
External outage
Timeout
429
Network split
Duplicate operation
Worker crash
Lease expiry
Dead-letter replay
Reconciliation
```

## Operations

```text
Connection health UI
Retry UI
Dead Letter UI
Reconciliation UI
Alerts
```

## Recovery

```text
Backup
Restore
Disaster recovery verification
```

## Final Gate

```text
Security PASS
Isolation PASS
Architecture PASS
Contract PASS
Recovery PASS
Backup/Restore PASS
Observability PASS
Performance PASS
```

---

# 24. EARLY COMMERCIAL RELEASE MILESTONE

Tüm Phase 0–15 tamamlanmadan ürün satılabilir/pilot olabilir.

Minimum commercial release adayı:

```text
Tenant / Company
Security

Customer
Catalog
Customer Pricing
Inventory

Quote
Order

Internal Sales UI
B2B Portal

Commercial Account
veya
External Account

Invoice Reference
veya
External Invoice

Pilot Read Connector

Audit
Integration Health
```

External ERP order write V1.1 olabilir.

---

# 25. PILOT STRATEGY — REFORM

Pilot generic demo tenant ile yapılmaz.

Reform'un gerçek:

```text
customers
products
prices
inventory
balances
ledger
invoices
quotes
orders
```

verileri kullanılır.

Ancak hiçbir Reform davranışı source code'a tenant-specific hard-code edilmez.

Tenant configuration veya capability/policy mekanizmaları kullanılır.

---

# 26. ERP'SİZ RELEASE SENARYOSU

Commercial Platform'ın release'i ERP entegrasyonuna bağlı değildir.

ERP olmadan:

```text
Customer
Catalog
Pricing
Inventory
Quote
Order
Commercial Ledger
InvoiceReference
```

çalışabilir.

Ancak:

```text
Legal Accounting
e-Invoice generation
e-Archive generation
GIB integration
```

kapsam dışıdır.

---

# 27. KİRALAMA SINIRI

Commercial Platform V1 aşağıdakileri içermez:

```text
Rental Contracts
Rental Accrual
Site Rental Inventory
Rental Return
Damage
Loss
Rental Billing
```

Bu domain KiralamaTakip'te kalır.

Pilot sırasında bu taleplerin çıkması roadmap kapsamını otomatik değiştirmez.

---

# 28. LEGACY SALES OPS GEÇİŞ KURALI

Eski Sales Ops yeni ticari geliştirmelerin source of truth'u değildir.

Ayrı:

```text
Customer
Quote
Order
Payment
```

ticari çekirdeği büyütülmez.

Gelecekte gerekirse:

```text
Lead
Opportunity
Field Visit
Route
Target
Commission
```

capability'leri Commercial Platform üzerine bounded context olarak taşınır.

Bu migration roadmap'i Commercial V1'i bloke etmez.

---

# 29. RABBITMQ KARARI

V1 default:

```text
PostgreSQL
Transactional Outbox
Hangfire
```

RabbitMQ ancak ölçülen/kanıtlanmış ihtiyaç halinde roadmap'e eklenir.

Tetikleyici örnekleri:

```text
multiple independent consumers
high fan-out
cross-runtime delivery
throughput beyond Hangfire/outbox capacity
service decomposition
```

---

# 30. ROADMAP DECISION REGISTER

## O-001 — Reform Data Source

**Status:** OPEN  
**Target:** Phase 3 sonuna kadar  
**Hard Blocker:** Phase 6 kickoff

## O-002 — Second Full Connector

**Status:** OPEN  
**Decision point:** Phase 13 öncesi veya gerçek müşteri ihtiyacı oluştuğunda

## O-003 — Product Commercial Name

**Status:** OPEN  
**Technical blocker:** NO

## ADR-0001 — Tenant Isolation

**Status:** MUST BE ACCEPTED BEFORE PHASE 1

---

# 31. ROADMAP INVARIANTS

1. Frozen Architecture roadmap tarafından değiştirilemez.
2. Ana faz `active-phase.json` ile belirlenir.
3. Faz değişimi yalnız kickoff PR ile yapılır.
4. Aktif faz dışında business scope geliştirilemez.
5. KAPI-1 Phase 0 ile paralel başlar.
6. KAPI-1 READY olmadan Phase 6A acceptance tamamlanamaz.
7. Reform data source Phase 6 öncesi kesinleşmiş olmalıdır.
8. Reform data source'un Phase 3 sonuna kadar öğrenilmesi hedeflenir.
9. Reform Kumpara kullanmıyorsa Phase 6B erkene alınır.
10. B2B read experience external order write'dan önce gelir.
11. Commercial Platform ERP olmadan çalışabilir.
12. ERP'siz finance full accounting değildir.
13. Yasal e-Belge oluşturma V1 kapsamı değildir.
14. Legacy Sales Ops ayrı commercial core olarak büyütülmez.
15. Kiralama V1 kapsamına alınmaz.
16. RabbitMQ V1 default dependency değildir.
17. Provider-specific business logic Commercial Domain'e giremez.
18. Phase 0 architecture enforcement sonraki fazlarda gevşetilemez; yalnız faz-aware scope listeleri kickoff PR ile ilerletilir.
19. Her faz kendi acceptance gate'i geçmeden production scope'u genişletemez.
20. İlk gerçek müşteri ihtiyacı varsayımsal platform genellemesinden daha yüksek önceliktedir.

---

# 32. NEXT ACTIONS

Roadmap v1.1 yayınlandıktan sonra sıralı aksiyon:

```text
1. Phase 0 package Claude Code'a verilir.

2. Aynı anda KAPI-1 workstream açılır.

3. Phase 0 acceptance tamamlanır.

4. ADR-0001 kararı ACCEPTED yapılır.

5. Phase 1 Kickoff PR açılır.

6. Phase 2–3 sırasında Reform gerçek data source kesinleştirilmeye çalışılır.

7. Phase 5 sonunda DG-01 final kontrolü yapılır.

8. Phase 6A veya Phase 6A + 6B yoluna geçilir.
```

---

# 33. FINAL ROADMAP STATEMENT

Commercial Platform'ın teslimat stratejisi:

> önce mimari güvenlik ve isolation zemini,

> sonra ERP'den bağımsız commercial core,

> ardından erken read-only B2B değeri,

> sonrasında external write ve gelişmiş ticari kontroller

şeklindedir.

Kumpara entegrasyonu Phase 6'da sıfırdan başlamaz; **KAPI-1 Phase 0 ile paralel yürür.**

Reform'un gerçek veri kaynağı geç öğrenilmez; **Phase 3 sonuna kadar öğrenilmesi hedeflenir ve Phase 6 öncesinde zorunlu olarak kapanır.**

Bu hükümlerle:

# COMMERCIAL PLATFORM ROADMAP v1.1 — ACTIVE
