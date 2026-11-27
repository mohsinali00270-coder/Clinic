# Clinic Management — .NET MAUI + ASP.NET Core 10

Built phase by phase from `Clinic Management MAUI App — Requirements Specification.docx` (§12 delivery plan).

| Phase | Scope | Status |
|---|---|---|
| 1. Foundation | Solution structure, multi-tenancy, JWT auth, users, roles, permissions, branches, device registration | **Done** |
| 2. Offline + sync core | SQLite/SQLCipher, outbox, ChangeLog, push/pull, Sync Center, number ranges, offline PIN, remote wipe | **Done** |
| 3. Dynamic fields engine | Field/form/lookup admin, versioning, expression engine, DynamicFormView | **Done** |
| 4. Patients + appointments | Patient registry (dynamic form), search, duplicates, attachments, doctors, schedules, booking, queue, SignalR | **Done** |
| 5. Encounters + prescriptions | Specialty forms, vitals, ICD-10, signing/amendments, drug master, Rx templates, PDF; cases, referrals, procedures, day care, on-device dictation/OCR/read-aloud | **Done** |
| 6. Labs + billing | Labs; billing; 6b: packages, running bills, panels/insurance, credit notes, deposits applied, tax, expenses, doctor share and payouts, messaging, feedback and complaints | **Done** |
| 7. Inventory + reports | Stock ledger, batches, FEFO dispensing, alerts; dashboard, reports by reportable fields, Excel/PDF export | **Done** |
| 8. Conflicts, hardening, release | Full conflict UI, patient merge, audit viewer, performance, CI/CD, store builds | **Done** |
| 9. AI | §13 assistants | Next |
| 10 | Patient panel | — |

## Solution layout

```
src/
  Clinic.Shared          DTOs, routes, permission catalogue (used by API and app)
  Clinic.Domain          Entities: Tenant, Branch, User, Role, Device, RefreshToken, AuditLog
  Clinic.Application     Services + FluentValidation validators (Auth, Tenants, Branches, Roles, Users, Devices)
  Clinic.Infrastructure  EF Core 10 (SQL Server), tenant query filters, audit, JWT, PBKDF2, migrations, seeding
  Clinic.Api             Controllers, permission policies, ProblemDetails, rate limiting, health checks
  Clinic.Client.Core     Device-side engine (plain .NET, testable): SQLCipher local DB, LocalStore + outbox,
                         SyncEngine (push → pull → number top-up), SyncCenter, HTTP transport
  Clinic.App             .NET MAUI app (Android, iOS, Windows, Mac Catalyst) — MVVM with CommunityToolkit
tests/
  Clinic.Api.Tests       Integration tests: tenant isolation, auth, sync server, two-device sync engine scenarios
```

## Run it

Requirements: .NET 10 SDK, MAUI workloads, SQL Server LocalDB (or change `ConnectionStrings:Clinic`).

```powershell
# API (applies migrations and seeds dev data on start) → http://localhost:5272
dotnet run --project src/Clinic.Api --launch-profile http

# App on Windows
dotnet build src/Clinic.App -f net10.0-windows10.0.19041.0 -t:Run

# Tests
dotnet test tests/Clinic.Api.Tests
```

On the Android emulator the app defaults to `http://10.0.2.2:5272`; change the server address under
**Server settings** on the sign-in screen for a real device.

### Development sign-ins (seeded only in Development)

| Who | Clinic code | User | Password |
|---|---|---|---|
| Tenant Admin (demo clinic) | `demo` | `admin` | `Admin@123` |
| Platform Super Admin (API only) | *(empty)* | `superadmin` | `SuperAdmin@123` |

The JWT signing key in `appsettings.Development.json` is for development only. In production set
`Jwt__SigningKey` (32+ bytes) and `ConnectionStrings__Clinic` from a secret store.

## Design system (app UI)

All colours and styles live in two files; screens reference them by key and never use raw colours.

- **`Resources/Styles/Colors.xaml`** holds the tokens:
  - brand: deep navy `#0B2545` and medical teal `#0D9488`/`#0F766E`;
  - surfaces: soft white and light gray, with navy-black in dark mode;
  - status: success, warning, danger and info, each with a soft background tone;
  - the hero gradient.

  Light and dark variants sit side by side (`Danger` / `DangerDark`). Body-text pairs meet WCAG AA contrast.
- **`Resources/Styles/Styles.xaml`** holds an implicit style for every control (filled inputs, 10 px rounded
  buttons, hover and focus states) and the named styles the screens use:
  - surfaces: `Card`, `ElevatedCard`, `StatCard`, `HeroCard`, `IconTile`;
  - text: `PageTitle`, `SectionTitle`, `FieldLabel`, `Muted`, `Caption`;
  - buttons: `SecondaryButton` (tonal), `DangerButton`, `LinkButton`;
  - chips: `Badge` and `Chip*`.
- **Icons:** `Resources/Images/ic_*.svg` are 24 px line icons, teal stroke. They are used in the menu, the
  dashboard and the quick actions.
- **Code-built visuals** (chips, chart bars, dynamic forms) get theme colours from `ThemeColors.Get("Success")`.
- **Appearance:** System, Light or Dark, chosen on the dashboard (`AppSettings.Theme`).
- **Layout:** on desktop the menu is a fixed sidebar; on phones and tablets it is a flyout. The dashboard
  shows 2, 3 or 6 tiles per row and one or two panel columns depending on width.

## Phase 1 — what is implemented

- **Multi-tenancy (FR-API-03, FR-SEC-08)**: every tenant table carries `TenantId`; an EF global query filter limits
  every query to the caller's tenant (and hides soft-deleted rows). `SaveChanges` stamps `TenantId` on insert and
  **refuses** any insert/update/delete for another tenant. Tests check every tenant table via reflection.
- **Auth (FR-API-02, FR-SEC-04)**: 15-minute JWT + 30-day rotating refresh tokens (hashed at rest) with
  reuse detection that revokes the whole token family; PBKDF2 password hashing; lockout after 5 failures.
- **Permissions (FR-AUTH-01/02)**: string permissions with scopes `All` / `OwnBranch` / `OwnRecords`; roles are
  editable bundles, seeded with the 7 default roles from §2. `[HasPermission(...)]` on every endpoint; scopes
  applied in services; no privilege escalation (you can only grant what you hold).
- **Audit (FR-API-07, FR-SEC-07)**: every write is logged with old/new values (secrets masked), user and device.
- **Devices (FR-OFF-30)**: register on login, short per-tenant device code (`D01`…) for offline numbering,
  rename, revoke (kills its sessions; login/refresh from it returns `device-revoked`).
- **API hygiene (FR-API-04/06)**: RFC 7807 ProblemDetails with field keys, rate-limited auth, Brotli/gzip,
  request size limit, `/health`, OpenAPI at `/openapi/v1.json` in Development.
- **MAUI app**: sign-in, home (connectivity, roles, branches, device code), branches, users (roles + branches),
  devices; menu items hidden by permission; tokens in SecureStorage; profile/permissions cached for offline use.

## Phase 2 — offline-first and sync

**How a change travels.** A screen calls `LocalStore.InsertAsync/UpdateAsync/DeleteAsync`. In one SQLite transaction
the row is written and an outbox entry is appended with only the fields that changed. The `SyncCoordinator` triggers
a sync on start, on reconnect, every 5 minutes, 10 s after a save, or on **Sync now** (FR-SYN-01). `SyncEngine` then:

1. **Pushes** the outbox in order, in batches of 200, gzip-compressed (`POST /api/v1/sync/push`). The server applies
   each change in its own transaction, keyed by `ChangeId`, so a resent batch returns `Duplicate` and is never applied
   twice. Rows are checked again on the server (permission, branch scope, validation, writable fields only).
2. **Pulls** `GET /api/v1/sync/pull?since=` page by page. The checkpoint is saved in the same SQLite transaction as
   the page, so an interrupted pull resumes where it stopped. Fields with unsynced local edits keep their local values.
3. **Tops up** MRN / token / invoice number ranges when fewer than 20% remain (FR-OFF-22).

**Server side.** Every save of a sync entity takes the next per-tenant `ChangeSequence`. That number becomes the row's
`RowVersion` and is written to a `ChangeLog` row listing the fields that changed. The counter row stays locked until
commit, so sequences become visible in order and a checkpoint can never skip a late commit. Conflicts are detected
per field: only fields that another device changed after the pushing device's base version count (§7.4). Policies
are `LastWriterWins` (notes), `ServerWins` (config) and `Manual` (ready for clinical data in Phase 5). Delete vs edit:
the edit wins.

**Device security.** The local DB uses SQLCipher with a random per-clinic key kept in SecureStorage. It refuses to
open if SQLCipher isn't the active provider, rather than silently writing plain text. It is excluded from Android
backup. The offline PIN is a salted PBKDF2 hash; 5 wrong tries remove it. A revoked device deletes its DB, key, PIN
and session on next contact.

**App screens.** Status line and Sync now on Home; **Handover notes** (the first offline entity, per branch);
**Sync Center** (pending / retrying / rejected / conflict, with Retry, Keep mine, Discard mine); PIN unlock.
Signing out with unsynced changes is blocked unless the user holds `Sync.DiscardPending`.

**Upgrading an existing database.** On start, built-in roles of existing clinics receive newly introduced permissions
(`DefaultRoles.CatalogVersion`), and rows created before sync existed are given a `ChangeLog` entry.

## Phase 3 — dynamic fields and forms (§5)

**Model (Clinic.Shared/Forms, used by app and API).** `FormDefinition` → sections → fields, with 22 field types,
English/Urdu labels, validation, conditions, formulas, abnormal ranges, lookups (cascading), repeating groups,
role permissions and flags. `ExpressionCompiler` is a small safe language: no loops, reflection or I/O; null-safe
arithmetic; date maths; `if`, `round`, `daysBetween`, `ageYears`, `contains`, … `FormEvaluator` runs calculations
in dependency order, then visibility, required, enabled, permissions, abnormal flags and validation. It is identical
on device (offline) and server (re-check). `FormDefinitionValidator` checks keys, expressions, unknown names,
calculation loops, regexes and ranges. It also checks version compatibility: a key keeps its type, and a field
that holds data must be retired rather than removed.

**Server.** Forms are drafts (saved freely, problems listed) → **Publish** creates an immutable `FormVersion`
that syncs to devices, branch-scoped when the form is. Records keep the version they were captured with.
Lookup lists have built-in Pakistani defaults (gender, blood group, cities → areas, specialties, …) and CSV import.
There are 8 global templates (handover note, patient registration, vitals with BMI, General OPD, Dental, Gynae with
EDD/gestational age, Paediatrics, Physiotherapy).
`FieldValue` rows are the sync unit, one per (record, field), with deterministic ids. Two devices editing different
fields of a record therefore merge, and the same field is last-writer-wins. On push the server checks:
- the parent exists and the caller can edit it;
- the field exists in that form version;
- the caller's role may edit it;
- type, range, pattern, options, lookup membership and uniqueness.

It then **recomputes calculated fields**. Values of fields a role may not view are never sent to that device.

**App.** `DynamicFormView` builds the UI at runtime: one column on phones, up to four wide. It refreshes
calculations, conditions, abnormal colours and errors live, offline, in English or Urdu (RTL). Handover notes now
carry the clinic's "details" form. Drafts autosave every 10 s and on leaving the page. Tenant Admins get **Forms**:
designer (sections/fields, reorder), field editor (all properties, instant offline checks), live preview
(phone/tablet width, language, role, gender/age), publish, version history, clone, JSON export/import,
and **Lookup lists** with CSV import.

## Phase 4 — patients, doctors, appointments, queue (§4.1, §4.2, §4.9)

**Patients (offline).** Core fields (name, gender, DOB or age, phone, branch) plus the clinic's **Patient** dynamic
form. Each patient gets an MRN `{Branch}-{Device}-{Seq}` from the device's leased number range, unique without the
network. Search runs on an FTS5 index on the device by name parts in any order, phone (+92/0 forms), MRN or CNIC.
Registration warns about likely duplicates: same phone, a similar name (token order and edit distance), and a
compatible DOB. The timeline lists registration, appointments and files. Attachments (photos, PDFs, up to 25 MB):
- encrypted with AES-GCM in app storage;
- metadata syncs, then the bytes upload in resumable 1 MB chunks (`/api/v1/files/{id}/chunks/{n}`);
- the server checks size and SHA-256;
- other devices download on demand;
- files are stored behind `IFileStore` (local disk now; Azure Blob or S3 can plug in).

**Doctors and setup (admin, online → synced).** Doctor profile (specialty, licence and expiry warnings, type, fee,
linked staff login), weekly sessions (slot length, patients per slot, buffer, validity) with overlap checks, and
leave. Appointment types have duration, fee, preparation notes, minimum notice, maximum advance and a reschedule
limit; defaults are consultation, follow-up, procedure and walk-in. Branch holidays are also set here.

**Booking and queue (offline).** `SlotCalculator` (shared with the server) turns schedules minus leave, holidays
and existing bookings into slots. On the device you can:
- book, reschedule (limit 2), cancel with a reason (lookup list) or mark a no-show;
- add a walk-in with a token and priority (emergency, priority, normal);
- find the next available slot across doctors of a specialty;
- run the queue: Waiting → Vitals → With doctor → Completed, plus hold, transfer, left-without-being-seen and
  **Call next**;
- see an estimated wait from today's average consultation time.

Every status change writes a time-stamped history row. When two offline devices book the same full slot, the
server keeps both and flags the later one **double-booked**; duplicate tokens get the next free number.

**Live updates.** After a sync save, the API hub `/hubs/clinic` (SignalR) sends a debounced "changes available"
event to the affected branch's devices, and they pull at once. The acceptance test shows a walk-in reaching the
doctor's queue on another device within 10 s.

## Phase 5 — encounters and prescriptions (§4.3, §4.4)

**Consultation (offline).** Open it from the queue ("Open consultation" moves the patient to *With doctor*) or
"New consultation" on the patient page. A doctor whose licence has expired cannot start one. The screen has:
- **Vitals** from the clinic's Vitals form, with BMI calculated and abnormal values flagged. Nurses can record
  vitals but cannot start or sign encounters.
- The **specialty form**, resolved by the doctor's specialty (e.g. *General OPD consultation*). It autosaves
  every 10 s and when you leave the page.
- **Diagnoses**: ICD-10 search over a list cached on the device (about 110 common codes seeded; admins can import
  more by CSV), or free text.
- A **prescription** from the drug master (about 50 common drugs; CSV import). Each drug fills in its default
  dose, frequency and duration. You can also:
  - apply **favourites** (doctor templates, synced to the doctor's other devices);
  - see **warnings** for allergies (allergy classes and free-text notes vs the patient's allergies),
    duplicate generics, and drugs still running from the last 60 days.
- A **follow-up date**.

**Signing and amendments (FR-ENC-05).** Signing locks the encounter. Afterwards, only the signing doctor can change
it, and every change records an amendment with old value, new value and reason. The server re-checks this: a change
to a signed encounter is accepted only if an amendment from the signing doctor matches the pushed value *exactly*,
so a device that bypasses the app is rejected. Clinical records (notes, vitals, diagnoses, prescription lines) use
the **Manual** conflict policy. When two devices edit the same field, the second one sees a conflict in the Sync
Center instead of silently overwriting.

**Printing (FR-RX-04, offline).** The prescription PDF is rendered on the device with SkiaSharp. HarfBuzz shapes
the Urdu dosing text, so it shows correctly joined right-to-left. The PDF is A4 with clinic/doctor header, patient,
vitals, diagnosis, Rx lines (English + Urdu frequency), advice from fields marked *print on prescription*, follow-up
and signature; long lists break across pages. Unsigned prescriptions carry a DRAFT watermark. The file opens in the
system viewer to print, or goes to the share sheet.

Acceptance (`EncounterTests.Doctor_completes_and_prints_a_prescription_fully_offline`): with the transport offline,
the doctor goes from queue → vitals → notes → ICD → prescription (allergy caught) → favourite → sign → PDF. Back
online, everything syncs with nothing rejected. A nurse device receives the encounter and a receptionist device
doesn't. Verified as well in the Windows app against SQL Server LocalDB.

## Phase 5b — cases, referrals, procedures, day care, on-device assist (§4.10–4.12, §13.3)

**Cases (offline, FR-CASE-01…08).** A case groups the visits, procedures and admissions of one clinical problem.
Case types are tenant masters (starter set: general episode, pregnancy, physiotherapy course, diabetes follow-up,
root canal). Each type has:
- its own **Case** form: a form whose context is the type code wins. The `case_standard` template adds pregnancy
  fields (LMP, EDD = LMP + 280, gravida/para) only when `caseType == 'pregnancy'`;
- expected sessions, a follow-up interval and a default care plan. Opening a case turns the plan into dated steps
  and adds the doctor to the team as primary.

On the case screen you can:
- edit the details form, add, finish, skip or reschedule plan steps, manage the team (primary / consulting /
  nurse / therapist) and write notes;
- file a visit under a case from the consultation (one case per visit). Linking a visit marks the next planned
  visit step done, and completing a procedure marks the next procedure step done;
- see progress: sessions done vs planned, overdue steps (flagged and listed as follow-ups), vitals trends and
  procedure fees;
- close with an outcome (`case_outcomes` list) and a closing summary drafted from the case's records, or reopen.

The server rejects changes to the plan, team or notes of a closed case, and links to another patient's case.

**Referrals (FR-REF-01…04).**
- *Internal* referrals go to a doctor or to any doctor of a specialty. They appear in that doctor's **Follow-ups &
  referrals** inbox, and only the receiving doctor can accept, decline or complete them (re-checked on the server).
- *External* referrals carry a clinical summary built from the consultation (complaint, plan, vitals, diagnoses,
  medicines, allergies) and print as an A4 letter on the device.
- *Incoming* referrals record who sent the patient (`referrers` / `referral_source` lookups) for reports and, in
  Phase 6, commissions.
- The follow-up list shows patients due or overdue, from consultation follow-up dates and planned care-plan steps.
  A follow-up drops off once the patient has visited again or has a booking. Each row offers call, SMS or WhatsApp
  with a bilingual reminder; Pakistani numbers are normalised to +92.

**Procedures (FR-PRC-01/02).** The procedure master holds category, fee, duration, consumables and whether consent
is needed, with English and Urdu consent wording. Planning a procedure copies the fee and consumables. Each
procedure has a **Procedure** note form (the `procedure_note` template; a form whose context is the procedure code
wins). For procedures that need consent:
- the patient or guardian signs on a signature pad (CommunityToolkit `DrawingView`);
- the PNG is stored as an encrypted attachment of the procedure (category `consent`) and uploads like any file;
- the procedure cannot start or be completed until consent is signed. The server re-derives whether consent is
  needed from the master and checks the signature attachment exists, so a device cannot skip it.

The consent form prints with the signature. Urdu is shaped with HarfBuzz.

**Day care (FR-PRC-03/04).** Beds, rooms and chairs are branch masters with a charge per day or per hour.
- The bed board shows who is where. From there you admit, transfer (the bed history is kept as `BedTransfer`
  rows), write nursing notes, and keep the medication chart: doctors order, nurses chart given / held / refused.
- Room charges accrue per started day or hour of each bed stay (minimum one unit) and show as a running total.
  The calculation is in `Clinic.Shared` so billing (Phase 6) uses the same rule.
- Discharging needs a summary, stops active orders, and prints a discharge summary.
- When two offline devices admit different patients to the same bed, the server keeps both admissions and flags
  the later one (`BedConflict`), like a double-booked slot.

**On-device assist (FR-AI-20…22, no network).**
- *Dictation* — a 🎤 button on every text field of a dynamic form uses the platform speech engine (English or
  Urdu by form language). The text is appended and stays editable.
- *OCR* — "Scan ID card" on registration and "Scan report" on vitals photograph a document. The platform engine
  reads the text: Windows.Media.Ocr, Apple Vision, or Google ML Kit with the bundled Latin model.
  `OcrExtraction` (pure, unit-tested) then proposes values: CNIC, phone, DOB and gender (CNIC check digit),
  "120/80" blood pressure, and numbers next to a field's label or a synonym. Values outside the field's range are
  dropped. Proposals are ticked or unticked and only ticked values are applied.
- *Read aloud* — the prescription read to the patient in English or Urdu (platform voices), built by
  `PatientInstructions` from structured data only.

**Permissions** (catalogue v3; built-in roles are upgraded at start): `Cases.View/Manage`, `Referrals.View/Manage`,
`Procedures.View/Perform`, `Admissions.View/Manage`, `Nursing.Record` and `CareSetup.Manage`.
- Doctors get these at branch scope, because cases are team care.
- Nurses get procedures, day care and nursing.
- Reception gets referrals and read access to cases and day care.

Admins manage case types, procedure fees and beds under **Care setup** (API: `/api/v1/case-types`,
`/procedure-types`, `/beds`).

Tests (`CareTests`, `CareRulesTests`) cover:
- a pregnancy case run offline from booking to closing;
- an internal referral reaching the receiving doctor, and the external letter;
- the follow-up list settling once the patient is booked;
- consent enforced on the device and on the server;
- day care with room charges over 29 hours, the medication chart and discharge;
- the two-device bed clash;
- the room-charge, OCR, read-aloud and phone-link rules.

Note: Skia's PDF backend crashes on lazily decoded images, so letters decode the signature to a raster bitmap
first.

## Phase 6 — labs and billing (§4.5, §4.6)

**Labs (offline, FR-LAB-01…03).**
- The *test menu* (12 starter tests: CBC, sugars, HbA1c, lipids, LFT, RFT, urine R/E, hepatitis screens, TSH) holds
  department, sample type, fee and turnaround.
- *Result parameters are a dynamic form*: entity `LabResult`, with the test code as context. Reference ranges and
  flags are the form's abnormal rules, so they can depend on `gender` and `ageYears`; for example, Hb is low below
  12.0 for women and 13.0 for men. Templates: `lab_cbc`, `lab_lipid` (LDL calculated) and `lab_generic` (value,
  unit, reference low/high, an H/L/N flag, or a text result) for tests without their own form.
- *Flow*: the doctor orders from the consultation (or the patient page, for walk-in blood work). The lab's work
  list moves each test from collect (sample number) to results (form validation; abnormal flag) to verify. Status
  moves forward only, and the order completes when every test is verified or cancelled.
- *Verification locks the result*. The server refuses any later change, including a single parameter value; values
  are clinical, so a conflict on them goes to the Sync Center for a person to resolve.
- The *report PDF* prints on the device, with ▲ flags and reference text, marked provisional until verified.
  Outside reports attach to the test (FR-LAB-03), and an analyser printout can be scanned into the form (OCR,
  ticked values only).
- `Labs.View` is new (catalogue v4). Lab technicians enter and verify results. Doctors order. Nurses and reception
  can read results.

**Billing (offline, FR-BIL-01…06, FR-BIL-13).**
- *Fee master*: services with tenant-wide prices and optional branch overrides, plus tax %. Lab tests, procedures,
  beds and doctors carry their own fees.
- *Unbilled charges*: visits (appointment type fee, else the doctor's fee), procedures, lab tests and finished
  day-care stays (room charges) that no live invoice charges yet. Tick them to make an invoice, then add services or
  manual lines.
- *Invoices* are numbered `{Branch}-{Device}-{seq}` from the device's leased range, so they are unique without the
  network. Line and invoice discounts need a reason and `Billing.Discount`. Finalising locks lines and amounts.
  Voiding needs a reason and refunds first.
- *Payments* are an insert-only ledger: payment, refund or deposit (advance), by method from the `payment_methods`
  lookup (cash, card, bank transfer, Easypaisa, JazzCash, wallet). Corrections are new rows, so offline desks never
  overwrite each other's money. The patient balance is invoiced − paid − deposits + refunds.
- *Receipts* print offline.
- *Cash closing*: each user closes their own drawer per day, with expected cash, counted cash, totals by method and
  over / short.

The device and the server run the same arithmetic (`Billing.Totals`, `CashClosingCalculator` in `Clinic.Shared`).
On the server:
- an invoice cannot become final unless its stored totals equal the totals recomputed from the lines the server
  holds;
- a visit, procedure, test or stay can be on only one live invoice; billing it on a second offline desk is refused
  with the first invoice's number;
- refunds cannot exceed what was paid, and need `Billing.Refund`;
- a closing records the server's own expected cash and flags a mismatch.

API: `GET /api/v1/billing/day-summary?branchId&date&userId`, `GET /api/v1/billing/balance/{patientId}`,
`/api/v1/lab-tests`, `/api/v1/services`. Admins edit lab fees and services under **Care setup**.

**App.**
- New screens: **Lab** (work list and test screen), **Billing** (from the patient page), **Invoice**, **Cash
  closing**.
- The consultation gains "Order tests", with each test's status and abnormal mark; tap it for the report.
- Reception now also gets `Labs.View`, `Procedures.View` and `Billing.CashClose` (own); the accountant gets the views
  needed to bill.

**Acceptance** (`LabBillingTests.Day_closing_totals_match_across_devices_after_sync`): two reception desks bill and
collect offline (cash, card, split payment, deposit) and sync, then each closes its own cash.
- Both closings match the server's expected cash.
- Branch totals by method on either device equal the server's day summary.
- After an accountant's refund, they match again.
- Patient balances (credit and owing) agree between device and server.

Other tests cover:
- the lab flow end to end (gender-specific flags, the verified lock, a nurse refused, the report, an outside
  attachment);
- the server's billing guards: discount permission, double billing, editing a final invoice or a payment, the
  refund permission, and forged totals.

## Phase 6b — billing extensions, communication, feedback (§4.13–4.15, FR-DOC-04/05)

**Packages (FR-BIL-10).**
- Package plans list entitlements: N uses of a service kind, optionally one code, for example 10 × procedure
  `physio_session`, or antenatal = 6 consultations + 2 CBC + 1 FBS. Each plan has a price and a validity.
- Selling a package creates the patient's package plus an invoice line for its price (source `package`).
- Later visits, procedures and tests that the package covers are offered as unbilled charges marked with the
  package. They bill at zero and record a `PackageUsage`. Removing the line or voiding the invoice gives the session
  back.
- The server refuses a use beyond the count, after expiry, or for another patient's charge.

**Running bill (FR-BIL-11).** For a case or a day-care stay: what is invoiced and paid, plus what has been done and
not yet invoiced (room charges so far included). It is finalised by invoicing the pending charges.

**Panels and insurance (FR-BIL-12).**
- *Payer master*: corporate, insurance or government; default co-pay %, payment terms and a per-payer price list.
- *Panel membership*: member number, optional own co-pay and validity.
- A draft invoice can be billed to a panel. Lines take the payer's price, and the total splits into payer and
  patient shares; the patient's share is the co-pay rounded up to the rupee. The patient pays only their share.
  The server checks the panel (this patient, valid on the invoice date) and the split arithmetic.
- *Accounts (online)*: payer settlements, a payer statement (claimed, settled, outstanding, and ageing in
  0–30 / 31–60 / 61–90 / 90+ days, settlements paying the oldest invoices first) and a CSV claim batch export.

**Deposits applied (FR-BIL-13).** Advance credit can settle an invoice. This is a payment of kind `allocation`,
method `wallet`. It moves no cash, so the day closing leaves it out. The server caps it at both the patient's
credit and the invoice's due amount.

**Credit notes and write-offs (FR-BIL-14).** Billing staff request one on a final invoice, with a reason. Someone
else with `Billing.Approve` approves or rejects it; nobody can approve their own request. The decision is then
locked. Approved amounts reduce the invoice's due and the patient balance on the device and the server.

**Tax, expenses, profit (FR-BIL-15/17).**
- Tax per service (set on the fee master) is summarised by rate for a period.
- Expenses are recorded offline per branch, by category from the `expense_categories` lookup.
- The profit view is collected − refunded + payer settlements − expenses − doctor payouts paid, with expenses by
  category.

**Doctor share and payouts (FR-DOC-04/05, FR-BIL-18).**
- Share rules give a doctor a % or a fixed amount per service kind.
- The share is earned only on money collected in the period: collections on an invoice are spread over its lines
  in proportion to their amounts. A refund in the period reverses it in proportion.
- A payout statement goes draft → approved (recomputed, then frozen) → paid (with a reference). Approved periods
  cannot overlap.

**Communication (FR-COM-01…03).**
- Templates for six keys (appointment reminder, follow-up, lab ready, payment due, birthday, feedback request) in
  English and Urdu, with `{placeholders}`, for SMS and WhatsApp.
- Preferences per patient: language, channel and opt-out, one row per patient with an id derived from the
  patient.
- A message is rendered on the device in the patient's language and queued offline (`OutboundMessage`). After sync,
  the server's `MessageDispatchWorker` sends it through `IMessageSender`, skipping opted-out patients and retrying
  failures three times. The status (sent / failed / skipped) syncs back to the patient's timeline.
- The development sender only logs (numbers starting `+920000` fail). An SMS gateway, the WhatsApp Business API or
  SMTP adapter can plug in behind the same interface.
- The follow-up list can now queue the reminder instead of opening the phone's SMS app.

**Feedback and complaints (FR-FBK-01/02).**
- In-clinic feedback: stars for the visit, the doctor and the service, a comment, and an optional Feedback dynamic
  form. Ratings are averaged per doctor.
- A poor rating can become a complaint.
- The complaint register goes open → assigned (in progress) → resolved (resolution required) → closed. Anyone
  recording feedback can log a complaint. Working and resolving it needs `Feedback.ManageComplaints`, which the
  server enforces.

**Permissions** (catalogue v5): `Billing.Approve`, `Billing.Accounts`, `Expenses.Manage`, `Messages.Send`,
`Feedback.Record` and `Feedback.ManageComplaints`.
- Reception: messages and feedback.
- Doctors: messages.
- Nurses: feedback.
- Accountant: approvals, accounts and expenses.
- Tenant Admin: everything.

**App.**
- Billing page: packages (sell, sessions used) and panels (add).
- Invoice: bill to a panel, use advance credit, request a credit note or write-off; the panel split is shown.
- Patient page: send a message, edit preferences, record feedback.
- New screens: **Approvals**, **Expenses**, **Feedback & complaints**.
- Payers, packages, templates, share rules, payouts and finance reports are online accounts functions, available
  through the API for now (`/api/v1/payers`, `/packages`, `/message-templates`, `/doctor-share-rules`,
  `/doctor-payouts`, `/payer-settlements`, `/reports/tax`, `/reports/profit`).

**Migration** `BillingExtensionsCommsFeedback`: 15 new tables, plus panel columns on `Invoices`. Existing invoices
get `PatientAmount = Total`.

**Tests** (`AccountsTests`):
- packages used session by session, the running bill, and a forged extra session refused;
- the panel co-pay split with payer prices, the statement, ageing, the CSV export, a settlement, and a forged split
  refused;
- applied credit leaving the cash drawer alone, with self-approval and desk approval refused;
- doctor share 375 = 50% × (75% collected of 1,000) after a refund, payout approval and payment, tax by rate, and
  profit;
- messages sent, failed and skipped (opt-out after queueing) end to end, with the status on the timeline;
- feedback ratings per doctor, and complaint handling permissions;
- the pure rules (split, ageing, packages, share, templates).

## Phase 7 — inventory and reports (§4.7, §4.8)

**Stock is a ledger (FR-INV-01/02).**
- *Items* are a tenant master (medicine, consumable, reagent). Medicines link to the drug master, so prescriptions
  find what to dispense. The starter list holds one item per seeded drug plus common consumables.
- *Batches* are received per branch with a batch number, expiry, cost and supplier.
- *Movements* record every change: purchase, dispense, consume, adjust, expired, return, transfer out/in. They are
  signed and insert-only, and stock on hand is the sum of a batch's movements. Corrections are new movements, so
  offline devices never overwrite each other.
- When two counters dispense the same last units offline, both dispensings are kept. Stock goes negative and shows
  as an alert to count and adjust, rather than losing a dispensing that already happened.

**Pharmacy.**
- For a patient's prescription lines, the app finds the matching item and suggests the quantity: dose × times a day
  × days; liquids, creams and inhalers count as one pack.
- Dispensing picks batches first-expiry-first-out and skips expired ones. Each dispensing creates a
  `Dispensation` (the billable record, `ChargeSource.Dispense`) and its movements. A prescription line is dispensed
  once.
- Unopened returns go back to the batch they came from. Over-the-counter sales use the same flow.
- If the device knows of too little stock it asks first; it can still dispense ("shelf stock not entered yet").
- Procedure consumables can be taken from stock by matching names.
- Also: stock counts (adjustment to the counted figure), write-off of expired or damaged batches, and transfers to
  another branch (out here, into a matching batch there).
- Alerts (FR-INV-03): low (at or below the reorder level), near expiry (within 60 days), expired, and negative.
  Items a branch has never stocked do not raise alerts.

The server checks:
- batches belong to the item and branch;
- the sign suits the kind of movement;
- permissions by kind: stock in, stock out, `Prescriptions.Dispense`, and consumables for procedures;
- a dispensing movement belongs to a dispensation of the same item;
- a dispensed prescription line belongs to the same patient.

**Reports (FR-REP-01…03).**
- `ReportCalculator` (in `Clinic.Shared`) computes the dashboard for a period and branch:
  - new patients and visits;
  - visits by doctor and the top 10 diagnoses;
  - revenue by method and by day (refunds shown; applied credit excluded);
  - pending payments (patient share − settled + refunds − approved credits);
  - stock alerts by kind.
- The device runs it on local data, marked "as of last sync"; the server runs it on the database
  (`GET /api/v1/reports/summary`). Both use clinic-local days.
- *Dynamic fields marked reportable* (FR-DYN-07) are group-by columns: records counted by value, with option and
  lookup labels, and multi-select values counted per option (`/reports/breakdown`). The patient template now marks
  blood group, city, area and referral source reportable.
- *Export*: Excel through a small dependency-free Office Open XML writer (`XlsxWriter`: one sheet per table, numbers
  kept as numbers), and PDF through the clinic letter renderer.
- `GET /api/v1/reports/stock` gives on-hand stock by item and the server's alerts.

**Roles** (catalogue v6):
- Pharmacist: reads prescriptions (`Encounters.View`), bills and collects at the counter, closes own cash, reports.
- Nurse: stock out (consumables).
- Reception: reports.
- Accountant: stock and encounter views.

**Role upgrades fixed.** Upgrades used to add only permissions that were new to the catalogue, so an older
permission newly given to a built-in role never reached existing clinics. Examples are reception's cash closing
(v4) and the pharmacist's new access (v6). `DefaultRoles.GrantedIn` now records each late grant;
`RoleUpgradeTests` checks that grants reach an existing clinic and that permissions an admin removed stay removed.

**App.** New screens:
- **Pharmacy stock**: alerts, receive a batch, and per-batch count, write-off or transfer.
- **Dispense** (from the patient page): open prescription lines, over the counter, then bill.
- **Dashboard & reports**: period, figures, group-by field, Excel / PDF.

**Acceptance** (`InventoryReportsTests.Offline_counters_merge_stock_and_reports_match_server_totals`):
- two pharmacy counters dispense the same batch offline and bill it;
- after sync, stock is −2 on both devices, with a negative alert;
- the device's offline report equals the server's on every figure: patients, visits, visits by doctor, top
  diagnoses, revenue by method and day, pending invoices and amount, and stock alerts;
- the patient-by-city breakdown also matches, and a non-reportable field is refused;
- the Excel workbook (8 sheets) and the PDF export are produced.

The other test covers the prescription → FEFO → bill → return flow, the locked ledger, and a nurse refused for
dispensing.

**Migration** `InventoryAndReports`: 4 new tables.

## Phase 8 — conflicts, hardening, release (§7.4, §10, §11)

### Conflicts (FR-SYN-30/31)

- A manual-policy conflict is now a server record (`SyncConflicts`): the fields, mine, theirs, both row versions,
  device and user. The push result carries its `ConflictId`.
- **Sync Center → Compare fields** shows each conflicting field with *Mine* and *Theirs (server)*. Pick per field,
  or use *All mine* / *All theirs*:
  - all theirs: the change is discarded and the server version kept;
  - all mine: the change is re-sent on the current row version;
  - a mix: the local row is rebuilt from the server values plus the chosen ones and sent as **merged**.
- The outcome is reported back (`POST api/v1/sync/conflicts/resolutions`). The server closes the conflict and
  writes an audit row `ConflictResolved:{mine|theirs|merged}` with both versions. `GET api/v1/sync/conflicts`
  (Audit.View) lists open and resolved ones.

### Snapshot and retention (FR-SYN-23/24)

- A new device (checkpoint 0), or one told to reset, downloads one gzip NDJSON snapshot
  (`GET api/v1/sync/snapshot`) instead of paging the whole change log.
- A daily maintenance job compacts the ChangeLog older than `Sync:ChangeLogRetentionDays` (default 90). It
  removes superseded rows and delete markers and moves `PurgedThrough`, so a device older than that resets
  (after pushing its outbox).

### Patient merge (FR-SYN-32)

- **Duplicate patients** (Patients.Merge) lists likely pairs: same phone, similar name, compatible DOB. Choose
  the record to keep.
- The merge re-points every synced record that has a `PatientId` (visits, bills, labs, files…) to the survivor,
  copies dynamic values the survivor lacks, and soft-deletes the duplicate with `MergedIntoId`. It is audited
  as `PatientMerged`.
- A device that was offline during the merge can still push work on the old patient. The server redirects it
  to the survivor, and edits to the merged record itself are refused.

### Audit (FR-SEC-07)

- Opening a patient record writes an access event on the device. It syncs like any change and lands in the
  audit trail as `Read:open`, so reads are logged offline too.
- **Audit log** (Audit.View) searches by record type, id, action and date, showing before/after values.

### Device security (FR-SEC-01/05/06)

- **Auto-lock:** after `AutoLockMinutes` (default 5) without a touch or navigation, or that long in the
  background, the PIN screen covers the app. It is modal, so the form underneath keeps what was typed, and
  sync keeps running.
- **Screen privacy:** `FLAG_SECURE` on Android (no screenshots, blank recent-apps thumbnail); a blur over the
  iOS app-switcher snapshot.
- **Certificate pinning:** set SHA-256 SPKI pins (base64, `;`-separated) in the `certificate_pins` preference,
  or bake them in at `AppSettings.BuildPins`. A chain without a pinned key is then refused even if the OS trusts
  it. The SignalR nudge then uses SSE/long polling, which go through the pinned handler.

### Observability (§11)

- **Logs:** Serilog structured logs to the console, with framework and SQL text at Warning (override under
  `Serilog` in appsettings).
- **Traces and metrics:** OpenTelemetry for ASP.NET Core, HttpClient and `Clinic.Sync` (changes pushed,
  conflicts, rejections, rows pulled). Exported over OTLP when `OpenTelemetry:OtlpEndpoint` or
  `OTEL_EXPORTER_OTLP_ENDPOINT` is set.
- **App errors:** crashes, unobserved task errors and sync errors are queued in a small file and posted to
  `api/v1/telemetry/errors` after the next sign-in or successful sync. Messages and stack traces only, no
  patient data.

### Performance (§11 NFR targets, measured by `NfrTests`)

| Target | Result |
|---|---|
| Patient search over 100,000 local records < 300 ms | FTS5 prefix match, well under target |
| Push of 200 changes < 3 s; pull page of 500 rows < 2 s | Pass |
| Form with 150 dynamic fields renders < 1.5 s | Evaluation of all rules is milliseconds |
| Sync interrupted 20 times loses nothing | Pass: every change applied exactly once |
| A week offline, 50,000 queued changes (`Category=Load`) | Pass |

- **One transaction per push batch:** each change runs under its own savepoint, so a rejected or conflicting
  change still rolls back alone. Duplicates are found with one query, and SyncLog rows are written once at the
  end. Push cost went from about 50 ms to about 10 ms per change.
- **Logging fix:** Serilog had been logging every SQL command at Information. It is now at Warning.
- The NFR tests run in their own non-parallel collection, so the numbers measure the code.
- **Report periods:** the accounts reports (payer statement, claims CSV, doctor share, tax, profit) now treat
  dates as clinic-local days, as the Phase 7 reports already did. A day starts at local midnight converted to
  UTC, so evening payments are no longer counted on the next day.

### Deployment and CI

```bash
cp .env.example .env            # SQL password, JWT signing key, first-run seed
docker compose up -d --build    # SQL Server 2022 + API on :8080 (put TLS in front)
```

`src/Clinic.Api/Dockerfile` builds a non-root ASP.NET image. Attachments go on the `/data/files` volume, and
the liveness probe is `GET /health`.

`.github/workflows/ci.yml` runs:

- server build and tests (load test on manual dispatch);
- the API image;
- a signed Android AAB (when `ANDROID_KEYSTORE_BASE64` / `ANDROID_KEY_ALIAS` / `ANDROID_KEY_PASSWORD` are set);
- the Windows build;
- an unsigned iOS simulator build on macOS.

Store signing for iOS needs the Apple certificates and profiles added as secrets.

**Migration** `ConflictsMergeAudit`: `SyncConflicts`, `PatientMerges`, `AccessEvents`, `ClientErrors`, and
`Patients.MergedIntoId`.

### Deliberately left for later phases

- Patient SMS/WhatsApp alerts and reminders (FR-ALR), waitlist, recurring and group booking, resources
  (rooms/machines), deposits, no-show fees, kiosk/QR check-in, TV display and voice call-out.
- Doctor fees, commission and payouts (FR-DOC-03…05) come with billing in Phase 6.
- Cross-branch online patient lookup. Device data scope by age of record (FR-OFF-04) is not applied; devices
  hold their branches' patients.
- File, image, signature and body-diagram fields render a placeholder until attachments arrive (Phase 4/5).
- Device FTS indexing of searchable fields arrives with patient search (Phase 4).
- Drag-and-drop ordering is up/down buttons for now; form context by doctor waits for doctor management (Phase 4).
- OS background sync (WorkManager / BGTaskScheduler, FR-SYN-02) and the SignalR "changes available" nudge
  (FR-SYN-03) — Phase 4 with the queue. Today sync runs while the app is open.
- Biometric unlock and iOS backup exclusion (the PIN covers FR-SEC-05 for now).
- Admin screens (branches/users/devices) are online-only, as tenant-admin config edits are in §2.
- Super Admin tenant management is API-only (per §2); 2FA for admins is not built yet.
- Full Urdu translation of the app chrome (form labels are already bilingual).
