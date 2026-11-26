# Clinic Management — .NET MAUI + ASP.NET Core 10

Built phase by phase from `Clinic Management MAUI App — Requirements Specification.docx` (§12 delivery plan).

| Phase | Scope | Status |
|---|---|---|
| 1. Foundation | Solution structure, multi-tenancy, JWT auth, users, roles, permissions, branches, device registration | **Done** |
| 2. Offline + sync core | SQLite/SQLCipher, outbox, ChangeLog, push/pull, Sync Center, number ranges, offline PIN, remote wipe | **Done** |
| 3. Dynamic fields engine | Field/form/lookup admin, versioning, expression engine, DynamicFormView | **Done** |
| 4. Patients + appointments | Patient registry (dynamic form), search, duplicates, attachments, doctors, schedules, booking, queue, SignalR | **Done** |
| 5. Encounters + prescriptions | Specialty forms, vitals, ICD-10, signing/amendments, drug master, Rx templates, PDF; cases, referrals, procedures | Next |
| 4–10 | Patients & appointments → … → Patient panel | — |

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

### Phase 5b (next)

Cases and care plans (§4.10), referrals (§4.11), procedures with consent and day-care (§4.12), and on-device
dictation, OCR and read-aloud (§13.3).
### Deliberately left for later phases

- Patient SMS/WhatsApp alerts and reminders (FR-ALR), waitlist, recurring and group booking, resources
  (rooms/machines), deposits, no-show fees, kiosk/QR check-in, TV display and voice call-out.
- Doctor fees, commission and payouts (FR-DOC-03…05) come with billing in Phase 6.
- Patient merge (FR-SYN-32) and cross-branch online lookup are Phase 8. Device data scope by age of record
  (FR-OFF-04) is not applied yet; devices hold their branches' patients.
- File, image, signature and body-diagram fields render a placeholder until attachments arrive (Phase 4/5).
- Device FTS indexing of searchable fields arrives with patient search (Phase 4).
- Drag-and-drop ordering is up/down buttons for now; form context by doctor waits for doctor management (Phase 4).
- OS background sync (WorkManager / BGTaskScheduler, FR-SYN-02) and the SignalR "changes available" nudge
  (FR-SYN-03) — Phase 4 with the queue. Today sync runs while the app is open.
- Server-side conflict records and the side-by-side Mine/Theirs/Merged view (FR-SYN-30) — Phase 8. Conflicts
  already reach the Sync Center with Keep mine / Discard mine.
- Initial snapshot download (FR-SYN-23) and ChangeLog retention purge (FR-SYN-24). Paged pull from 0 is used for
  now; the `ResetRequired` path is in place.
- Biometric unlock, iOS backup exclusion, auto-lock (FR-SEC-05) — Phase 8.
- Admin screens (branches/users/devices) are online-only, as tenant-admin config edits are in §2.
- Super Admin tenant management is API-only (per §2); 2FA for admins, certificate pinning, Serilog/OTel — Phase 8.
