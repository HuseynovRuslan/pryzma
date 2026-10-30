# PRIZMA (kod adı: Meydan)

Yaradıcı layihələr və açıq müsabiqələr üçün platforma. **Canlı ünvan: https://pryzma.az**
(köhnə `prizma.qrlog.az`, `meydan.qrlog.az` və `www.pryzma.az` eyni yola 308 ilə yönləndirilir).

Platformanın **ictimai adı və başlıq mətnləri kodda deyil** — `PlatformSettings` sətrindədir və
*İdarəetmə → Parametrlər → Brend və ana səhifə* bölməsindən dəyişdirilir (`GET /api/settings` ictimai,
`PUT /api/admin/settings` admin). Brauzer eyni dəyərləri özü ilə gətirir, ona görə parametr sorğusu
uğursuz olsa da sayt düzgün adla işləyir.

Namespace, assembly, verilənlər bazası və konteyner adları (`Meydan.Api`, `meydan-web`, …) olduğu kimi
qalır: bu **məhsul adının dəyişməsidir**, kod miqrasiyası deyil.

```
Platform → Project → Competition → CompetitionCategory (optional) → Submission
```

Heç bir layihə, müsabiqə, kateqoriya və ya təqdimat forması kodda sabitlənməyib. "Bakı kəndləri", şevron müsabiqəsi,
onun 59 qəsəbəsi və forma sahələri yalnız seed məlumatıdır; hamısı idarəetmə panelindən (`/admin`) dəyişdirilir.

## Struktur

```
backend/
  src/Meydan.Domain           Entity-lər, enum-lar, rollar, slug
  src/Meydan.Infrastructure   EF Core (PostgreSQL / Npgsql), migration-lar, seed, JWT/refresh token, fayl storage, e-mail
  src/Meydan.Api              Feature-lər: Auth, Catalog, Submissions, Me, Admin, Media; DTO, validasiya, error handling
  tests/Meydan.Api.Tests      İnteqrasiya testləri — real PostgreSQL üzərində (in-memory əvəzedici yoxdur)
frontend/
  src/api          fetch/XHR klienti (access token yaddaşda, 401-də bir dəfə refresh, upload progress)
  src/auth         AuthContext, RequireAuth, iştirak niyyəti (login zamanı seçimin saxlanması)
  src/components   Dizayn sistemi komponentləri (CategoryPicker, submission/*)
  src/pages        Route səhifələri (lazy-loaded)
  src/admin        İdarəetmə paneli: ayrıca layout, səhifələr, admin API klienti, testlər
  src/lib          formRules (server qaydalarının UX üçün surəti), text (Azərbaycan axtarışı), format
  e2e/             flow.mjs, admin-flow.mjs, results-flow.mjs, phase5-flow.mjs (metadata → təhlükəsiz nüsxə, e-mail, bərpa), a11y.mjs (axe), qa.mjs (6 breakpoint), glyphs.mjs (şrift auditi), shots.mjs
scripts/
  dev-postgres.ps1 Lokal PostgreSQL (Docker-siz)
  backup.sh        PostgreSQL + storage backup (Linux / Git Bash)
  restore.sh       Yeni boş baza + qovluğa bərpa
```

## Verilənlər bazası: PostgreSQL

PostgreSQL yeganə dəstəklənən bazadır (PostgreSQL 16+; 17/18 ilə yoxlanılıb). Bütün tarixlər `timestamptz`
kimi UTC-də saxlanılır; offset ilə gələn dəyərlər (məs. `+04:00`) eyni ana UTC-yə çevrilir. Bazanın kodlaşdırması
UTF8 olmalıdır.

### Lokal quraşdırma — variant A: quraşdırılmış PostgreSQL (Docker-siz)

```powershell
./scripts/dev-postgres.ps1        # ilk dəfə: cluster yaradır, işə salır, meydan_dev bazasını açır
./scripts/dev-postgres.ps1 -Stop
```

Skript sistem PostgreSQL servisinə toxunmur: `%LOCALAPPDATA%\Meydan\pg-dev` altında şəxsi cluster açır
(port 55433, UTF8), təsadüfi parol yaradır və connection string-ləri **user-secrets**-ə yazır:

| Layihə | Açar |
|---|---|
| `backend/src/Meydan.Api` | `ConnectionStrings:Meydan` |
| `backend/tests/Meydan.Api.Tests` | `TestDatabase:AdminConnectionString` |

### Lokal quraşdırma — variant B: Docker Compose (istəyə bağlı)

```bash
cp .env.example .env          # POSTGRES_PASSWORD yazın; .env git-ə düşmür
docker compose up -d postgres
cd backend
dotnet user-secrets set "ConnectionStrings:Meydan" "Host=localhost;Port=55433;Database=meydan_dev;Username=meydan;Password=<parol>" --project src/Meydan.Api
dotnet user-secrets set "TestDatabase:AdminConnectionString" "Host=localhost;Port=55433;Database=postgres;Username=meydan;Password=<parol>" --project tests/Meydan.Api.Tests
```

Production Docker Compose-dan asılı deyil.

### Migration-lar

| Mühit | `Database:MigrateOnStartup` | Davranış |
|---|---|---|
| Development | `true` (`appsettings.Development.json`) | API start zamanı gözləyən migration-ları tətbiq edir, sonra seed |
| Production və digərləri | `false` (`appsettings.json`) | migration **tətbiq etmir**; gözləyən migration varsa API başlamır və nə etmək lazım olduğunu yazır |

Production-da migration ayrıca deploy addımıdır, tətbiqin yeni versiyası işə düşməzdən **əvvəl** icra olunur:

```bash
# Variant 1 — tətbiqin özü ilə (eyni connection string, eyni migration-lar):
ConnectionStrings__Meydan="…" dotnet Meydan.Api.dll migrate      # migrate + seed, sonra çıxır

# Variant 2 — DBA üçün SQL skript (idempotent, istənilən versiyadan son versiyaya):
cd backend
dotnet ef migrations script --idempotent -p src/Meydan.Infrastructure -s src/Meydan.Api -o migrate.sql
psql "$CONN" -v ON_ERROR_STOP=1 -f migrate.sql
```

Tövsiyə olunan ardıcıllıq: backup (`pg_dump -Fc`) → migrate → yeni versiyanı işə sal. Variant 2-də seed işləmir;
seed-in tələb olunduğu yeni bazada bir dəfə Variant 1 işlədin.

Development-də yeni migration:

```bash
cd backend
dotnet ef migrations add <Ad> -p src/Meydan.Infrastructure -s src/Meydan.Api -o Persistence/Migrations
```

Migration-lar: `InitialPostgres` (baseline) → `Phase3Admin` (layihə/müsabiqə statusları, sahə aktivliyi, cavabda
sahə adının snapshot-u, daxili qeydlər, status tarixçəsi, audit). `Phase3Admin` köhnə statusları məlumat itirmədən
çevirir: layihə Active/Completed → Published; müsabiqə Open/Upcoming → Published, Judging/Completed → Closed.
→ `Phase4Results` (`CompetitionResults` cədvəli, `CompetitionFields.IsPublicAfterResults`; heç yerdə istifadə olunmayan
`Competitions.IsPublicGalleryEnabled` silinir). Mövcud formalarda yalnız təklifin adı sahəsi "nəticədən sonra ictimai"
olur, qalan cavablar və bütün fayllar gizli qalır.
→ `Phase5Hardening` (`DataProtectionKeys`, fayllar üçün ictimai nüsxə sahələri və `ScanStatus`, təklifin
`PreviewFileId`-i). Mövcud fayllar `Pending` alır və API startından sonra arxa planda, kiçik partiyalarla nüsxələri
yaradılır (bax: "İctimai fayllar"); orijinallar toxunulmur.
→ `Phase5EmailOutbox` (`EmailOutbox` cədvəli — davamlı e-mail növbəsi, bax: "E-mail").

Seed addımları (`SeedHistory` cədvəli) hər bazada bir dəfə işləyir; admin sonradan nəyi dəyişsə və ya silsə, seed onu
geri qaytarmır. Seed yalnız "Bakı kəndləri"ni yaradır — test/QA məlumatı (məs. Laçın) seed-də yoxdur.

## Sirlər

Repository-də heç bir sirr saxlanılmır. Development-də user-secrets, digər mühitlərdə environment variable
(`ConnectionStrings__Meydan`, `Jwt__SigningKey`, `Seed__AdminEmail`, `Seed__AdminPassword`) və ya hostun secret store-u.

```bash
cd backend
dotnet user-secrets set "Jwt:SigningKey"     "<ən azı 32 simvol, təsadüfi>" --project src/Meydan.Api
dotnet user-secrets set "Seed:AdminEmail"    "<admin e-mail>"               --project src/Meydan.Api
dotnet user-secrets set "Seed:AdminPassword" "<güclü parol>"                --project src/Meydan.Api
dotnet user-secrets list --project src/Meydan.Api
```

JWT açarı və ya connection string olmadan API başlamır (nə etmək lazım olduğunu yazaraq). Admin e-mail/parolu
verilmirsə admin hesabı yaradılmır.

## İşə salmaq

```bash
cd backend  && dotnet run --project src/Meydan.Api --launch-profile http   # http://localhost:5080 (swagger: /swagger)
cd frontend && npm install && npm run dev                                   # http://localhost:5173 (/api proxy)
```

## Təqdimat (Phase 2)

Axın: müsabiqə → qəsəbə → "Təklif ver" → (login/qeydiyyat, seçim saxlanılır) → qaralama → Məlumatlar → Fayllar →
Önizləmə → təqdim → `SUB-YYYY-NNNNN` → İşlərim.

- **Dinamik forma:** `CompetitionField` (ShortText, LongText, Number, Url, Date, Select, Checkbox, File). Cavablar
  `SubmissionAnswer`-də. `IsSubmissionTitle` olan sahə təklifin adıdır. Validasiya serverdədir, frontend onu UX üçün təkrarlayır.
- **Statuslar:** Draft (yalnız sahibi görür, redaktə olunur) → Submitted (istinad nömrəsi var, dəyişməz) →
  UnderReview / Approved / Rejected. Qalib moderasiya statusu deyil — müsabiqənin nəticəsidir (aşağıya bax).
- **İstinad nömrəsi:** yalnız serverdə, bir atomik PostgreSQL `INSERT … ON CONFLICT … RETURNING` ilə — paralel
  sorğularda təkrar olmur.
- **Limitlər:** `Competition.EndDate`, `StartDate`, `MaxSubmissionsPerUser` null-dursa tətbiq olunmur; dəyər qoyulanda
  server yoxlayır.

### Fayl yükləmə siyasəti

`Uploads` bölməsi platformanın yuxarı həddidir; File sahəsinin `OptionsJson`-u
(`{"maxFiles":5,"maxFileSizeMb":10,"accept":["png","jpeg","webp","pdf"]}`) onu yalnız daralda bilər.

| | |
|---|---|
| Formatlar | PNG, JPG/JPEG, WEBP, PDF. SVG qəbul olunmur (aktiv məzmun). |
| Yoxlama | uzantı + bəyan edilən MIME + faylın məzmunu (magic bytes) üst-üstə düşməlidir |
| Ölçü | hər fayl ≤ 10 MB, bir təklifə ≤ 5 fayl, **bir sorğuda bir fayl** |
| Sorğu həcmi | yükləmə sorğusu ≤ fayl limiti + 1 MB (10 MB + 1 MB = 11 534 336 bayt), digər bütün sorğular ≤ 2 MB (2 097 152 bayt) |
| Saxlama | təsadüfi ad (`yyyy/MM/<guid>.ext`), `Uploads:StorageRoot` altında, wwwroot-dan kənarda; path traversal mümkün deyil |
| Verilmə | yalnız sahibinə (və ya təqdimdən sonra Admin-ə); `nosniff`, `sandbox` CSP, `no-store` |

## İdarəetmə paneli (Phase 3)

`/admin` — yalnız `Admin` rolu. Frontend-dəki yoxlama rahatlıq üçündür; hər `/api/admin/*` endpoint-i serverdə
`[Authorize(Roles = Admin)]` ilə qorunur (anonim → 401, iştirakçı → 403).

| Bölmə | İmkanlar |
|---|---|
| İcmal | aktiv layihələr, qəbulu açıq müsabiqələr, təkliflər (cəmi / göndərilib / yoxlanılır / qaralama), son təkliflər |
| Layihələr | yarat, redaktə (ad, slug, təsvirlər, rəng, sıra), örtük şəkli, Qaralama / Dərc olunub / Arxiv, silmək yalnız boş layihəni |
| Müsabiqələr | bütün sahələr (tarix, mükafat, limit boş qala bilər), Qaralama / Dərc olunub / Bağlanıb / Arxiv |
| Kateqoriyalar | əlavə, redaktə, sıra, aktiv/deaktiv, toplu idxal (`Ad; ünvan; açıqlama`, dublikat yoxlaması, hamısı-ya-heç-nə) |
| Təqdimat forması | sahə əlavə/redaktə/sıra/deaktiv; cavablanmış sahənin növü və açarı kilidlənir, silinmir |
| Təkliflər | filtrlər (layihə, müsabiqə, kateqoriya, status, tarix), axtarış (istinad, ad, e-mail), detal, fayllar, status, daxili qeydlər |
| İştirakçılar | siyahı və axtarış |

- **Status ≠ qəbul.** Saxlanan status admin qərarıdır; qəbulun açıq olub-olmaması (`phase`: Upcoming / Open / Closed)
  tarixlərdən hesablanır. Qaralama heç vaxt saytda görünmür. Dərc etmək üçün ən azı bir aktiv sahə və (kateqoriya
  seçimi açıqdırsa) ən azı bir aktiv kateqoriya lazımdır — server səbəbləri siyahı ilə qaytarır.
- **Tarixi cavabların bütövlüyü.** Təqdim anında hər cavabın sahə adı (`FieldLabel`) donur; sonradan sahənin adı
  dəyişsə və ya deaktiv edilsə də köhnə təklif olduğu kimi göstərilir.
- **Moderasiya.** Göndərilib → Yoxlanılır / Təsdiqlənib / Rədd edilib; hər dəyişiklik kim + nə vaxt + şərh ilə
  `SubmissionStatusChanges`-də. Winner avtomatik təyin olunmur. Daxili qeydlər iştirakçı API-lərində heç vaxt qaytarılmır.
- **Audit.** `AuditLogs`: AdminUserId, Action, EntityType, EntityId, Timestamp (UTC), qısa xülasə. Parol, token,
  qeyd mətni, cavab və ya fayl məzmunu yazılmır.
- **Örtük şəkilləri** eyni `IFileStorage` üzərindən (`covers/` sahəsi) saxlanılır; `/api/media/covers/…` yalnız bu
  sahəni və yalnız şəkilləri verir. Storage abstraksiyası S3/MinIO kimi başqa provayderə keçid üçün ayrılıb.

### Həmişəlik silmək

Arxivləşdirmək saxlayır, silmək götürür. İkisi də admin-dədir və biri digərini əvəz etmir: gündəlik iş üçün
**Arxivlə**, sınaq/lazımsız məzmunu təmizləmək üçün **Həmişəlik sil**.

| Yer | Düymə | Endpoint |
|---|---|---|
| Layihə səhifəsi → *Silmək* | Həmişəlik sil | `GET /api/admin/projects/{id}/delete-impact`, `DELETE /api/admin/projects/{id}/permanent` |
| Müsabiqə səhifəsi → *Silmək* | Həmişəlik sil | `GET /api/admin/competitions/{id}/delete-impact`, `DELETE /api/admin/competitions/{id}/permanent` |
| Təklif səhifəsi → *Silmək* | Həmişəlik sil | `GET /api/admin/submissions/{id}/delete-impact`, `DELETE /api/admin/submissions/{id}/permanent` |
| İştirakçılar siyahısı → sətir | Həmişəlik sil | `GET /api/admin/participants/{id}/delete-impact`, `DELETE /api/admin/participants/{id}/permanent` |

Köhnə `DELETE /api/admin/projects/{id}` və `DELETE /api/admin/competitions/{id}` olduğu kimi qalır — onlar yalnız
boş obyekti silir və təhlükəsiz yoldur. `/permanent` isə qəsdən seçilən yoldur.

- **Dialoq serverin saydığı rəqəmləri göstərir** (`delete-impact`), React-də hesablanmır. Asılı məlumat varsa düymə
  **SİL** sözü yazılana qədər sönükdür. Uğursuzluqda sətir ekrandan silinmir — səbəb göstərilir.
- **Ağac açıq şəkildə gəzilir** (`PermanentDeletion`): qaliblər → nəticə → təkliflər (cavab, fayl, qeyd, tarixçə) →
  kateqoriyalar → forma sahələri → müsabiqə → layihə. Bir çox əlaqə qəsdən `Restrict`-dir, ona görə kaskad-a
  güvənilmir. Seçilmiş ağacdan kənarda heç nəyə toxunulmur.
- **İştirakçı hesabı məzmunla birlikdə silinmir.** Sınaq müsabiqəsinə real adam iş göndəribsə, işi gedir,
  hesabı qalır. Hesabın özünü silmək ayrıca əməliyyatdır və **yalnız boş hesab üçün** işləyir: təklifi
  (qaralama daxil) olan hesab, admin hesabı və öz hesabınız rədd edilir — 409. Silinəndə giriş üsulları,
  sessiyalar və gözləyən məktublar da gedir; audit qeydi (`ParticipantPermanentlyDeleted`) qalır.
- **Fayllar bazadan sonra silinir**: sətirlər bir tranzaksiyada gedir, sonra hər saxlanan açar üçün “bunu göstərən
  başqa sətir qaldımı?” yoxlanılır. Qalıbsa fayl saxlanılır; silinə bilməyən fayl log-a düşür və hesabatda
  `storedFilesFailed` kimi qayıdır, amma bazanı geri qaytarmır. Qovluq heç vaxt toplu silinmir.
- **Audit qalır.** `ProjectPermanentlyDeleted` / `CompetitionPermanentlyDeleted` / `SubmissionPermanentlyDeleted` —
  admin, slug və ya istinad nömrəsi, asılılıq sayları. `AuditLogs`-un obyektə FK-sı yoxdur, ona görə qeyd silinən
  obyektdən sonra da durur.
- **Seed geri qaytarmır.** `SeedHistory` açarı yerində qaldığı üçün silinmiş seed məzmunu növbəti deploy-da
  yenidən yaranmır (`PermanentDeletionTests.Deleted_seeded_content_does_not_come_back_on_the_next_start`).
  `SeedHistory` silinmir.

## Qalib, nəticələr və qalereya (Phase 4)

Bir müsabiqə — **bir** ümumi qalib. Kateqoriyalar (məs. 59 qəsəbə) yalnız işin hansı qrupa aid olduğunu göstərir; kateqoriya
qalibi yoxdur. Münsiflər, bal, səsvermə, ikinci/üçüncü yer yoxdur — qalibi administrator özü seçir.

```
Qalib seçilməyib ──(Qalib seç)──▶ Hazırlanır (gizli) ──(Nəticəni dərc et)──▶ Dərc olunub (ictimai)
                                   ▲  (Qalibi dəyiş)                            │
                                   └──────────────(Dərcdən çıxar)───────────────┘
```

- **Model:** `CompetitionResult` (CompetitionId **unikal**, WinningSubmissionId, SelectedBy/At, PublishedBy/At, Announcement,
  `xmin` versiyası). Qalibin FK-sı `(WinningSubmissionId, CompetitionId)` → `Submissions(Id, CompetitionId)` — başqa müsabiqənin
  işi bazaya belə yazıla bilməz. Paralel "Qalib seç" sorğularından yalnız biri keçir (unikal indeks), qalanları 409 alır.
- **Kim qalib ola bilər:** həmin müsabiqənin Göndərilib / Yoxlanılır / Təsdiqlənib statuslu işi. Qaralama, rədd edilmiş və
  geri çəkilmiş iş ola bilməz. Qalib seçilmiş işi rədd etmək bloklanır (əvvəlcə qalibi dəyişin).
- **Qalibi dəyişmək** ayrıca əməliyyatdır (`PUT …/result/winner` hazırkı qalibin id-si ilə); dərcdən sonra yalnız nəticəni
  dərcdən çıxardıqdan sonra mümkündür.
- **Dərc:** qalib seçilməli və müsabiqə yeni iş qəbul etməməlidir (Bağlanıb və ya son tarix keçib). Nəticə yalnız dərc olunub
  **və** müsabiqə saytda görünür **və** qəbul bağlıdırsa ictimaidir — müsabiqəni yenidən açmaq nəticəni və qalereyanı gizlədir.
- **Nə ictimai olur:** istinad nömrəsi, kateqoriya, forma qurucusunda "nəticədən sonra qalereyada göstər" işarələnmiş sahələrin
  cavabları və fayl sahələrinin **server tərəfindən hazırlanmış metadata-sız nüsxələri** (şəkillər WEBP, PDF üçün yalnız birinci
  səhifənin şəkli; bax "Production → İctimai fayllar"). Rədd edilmişlər qalereyada yoxdur.
- **Heç vaxt ictimai deyil:** iştirakçının adı, e-maili, telefonu, faylların orijinal adı, storage açarları, daxili qeydlər,
  moderasiya statusu və şərhləri, status tarixçəsi, audit.
- **Audit:** `WinnerSelected`, `WinnerChanged` (köhnə → yeni), `ResultsPublished`, `ResultsUnpublished`, `ResultUpdated` —
  admin, müsabiqə, submission id + istinad, UTC vaxt; məzmun yazılmır.

### Yerlər (1-ci, 2-ci, 3-cü) — hələ yoxdur

“Novxanının simvolu” və “Əmircanın simvolu” müsabiqələrində mükafatlar yerlər üzrə paylanır (1-ci, 2-ci, 3-cü yer).
Hazırkı model bunu saxlamır: kateqoriyalı müsabiqədə hər kateqoriyaya bir qalib, kateqoriyasız müsabiqədə isə bir ümumi
qalib var — sıralama yoxdur. Ona görə hər iki müsabiqə kateqoriyasız qurulub (nominasiya forma sahəsidir, kateqoriya
deyil), mükafatlar isə mətn kimi göstərilir. Yerlər üzrə nəticə — və lazım olsa, hər nominasiya üzrə ayrıca qalib —
ayrıca işdir: münsiflər qaydası dəqiqləşəndən sonra model dəyişməlidir. Bu UX dəyişikliyində riskli dəyişiklik edilmədi.

### Layihə: admin anlayışı, ictimai addım deyil

Layihə (`Project`) modeldə qalır və öz səhifələri (`/projects`, `/projects/:slug`) işləkdir, amma ictimai naviqasiyada
yoxdur: ziyarətçi ana səhifə → afişa → müsabiqə → iştirak yolu ilə gedir. Qəsəbə də forma sualı deyil — hər qəsəbənin öz
müsabiqəsi var, ona görə iştirakçıdan bir dəfə də soruşulmur.

## E-mail

Provayder-neytral `IEmailSender` (göndərmə) + **PostgreSQL-də davamlı outbox** (`EmailOutbox` cədvəli). Sorğu heç vaxt
mail serverini gözləmir və onun xətası ilə uğursuz olmur; məktub restartdan, çökmədən sonra da itmir.

| `Email:Mode` | Harada | Nə edir |
|---|---|---|
| `Smtp` | production | MailKit ilə SMTP (STARTTLS / SSL / None) |
| `Disabled` | açıq seçim | göndərmir; loga yalnız növ + maskalanmış alıcı (`a***@site.az`); sətir `Cancelled` olur |
| `Log` | yalnız Development (default) | mətn (reset linki daxil) development loguna yazılır |
| `Pickup` | yalnız qeyri-production (QA/e2e) | hər məktub `Email:PickupDirectory`-də `.eml` faylı |

Məktublar (Azərbaycan dilində, sadə HTML + plain-text): **şifrənin yenilənməsi** və **"Təklifiniz qəbul edildi"**
(istinad nömrəsi ilə). Qəbul məktubu təklif bazaya yazıldıqdan *sonra* növbəyə qoyulur; növbəyə qoymaq belə alınmasa
təklif geri alınmır — yalnız log yazılır.

**Outbox necə işləyir**

- Sətir yalnız **istinad** saxlayır: növ (`password-reset` / `submission-received`), istifadəçi id-si, lazım olsa təklif
  id-si, status, vaxtlar, cəhd sayı, lease, son xətanın tipi. **E-mail ünvanı, məktub mətni, link, reset token, parol,
  SMTP məlumatı, JWT/refresh token saxlanılmır.** Məktub göndərmə anında yığılır: alıcı ünvanı və ad istifadəçidən
  oxunur, reset token **yalnız göndərmədən dərhal əvvəl** yaradılır — bazada heç vaxt olmur.
- Reset məktubu token ömrü (`Auth:PasswordResetTokenLifetime`, default 1 saat) ərzində göndərilə bilməsə `Expired`
  olur və göndərilmir (gec gələn link onsuz da işləməzdi). Digər məktublar `Email:DefaultValidityDays` (7) gün.
- Worker (`EmailOutboxWorker`, hər API instansiyasında) sətirləri atomik götürür:
  `UPDATE … WHERE Id IN (SELECT … FOR UPDATE SKIP LOCKED) RETURNING Id` + `Email:LeaseSeconds` (120) müddətli lease.
  İki instansiya eyni sətri eyni vaxtda göndərmir. Çatdırılma **ən azı bir dəfə**dir: proses SMTP qəbul etdikdən sonra,
  `Sent` yazılmazdan əvvəl ölərsə, lease bitəndə məktub təkrar göndərilə bilər (nadir dublikat, itki yox).
- Uğursuzluqda: `Email:MaxAttempts` (5) cəhd, fasilə `RetryDelaySeconds × 3^(cəhd−1)` (5 s, 15 s, 45 s, 135 s; ən çox
  1 saat). Cəhdlər bitəndə sətir `Failed` olur, bir daha cəhd edilmir və `Error` səviyyəsində `given up after N attempts`
  loglanır (növ, sətir id-si, maskalanmış alıcı, xəta tipi/SMTP kodu — mətn, token, parol heç vaxt).
- Son statuslar: `Sent`, `Failed`, `Expired`, `Cancelled` (hesab/təklif artıq yoxdur və ya `Disabled` rejimi).
  Bitmiş sətirlər `Email:RetentionDays` (30) gündən sonra silinir. Hesab silinəndə gözləyən məktubları da silinir.
- Monitorinq: `select "Status", count(*) from "EmailOutbox" group by 1;` — köhnə `Pending` və ya yeni `Failed`
  sətirlər SMTP problemini göstərir. `Email:OutboxWorkerEnabled=false` yalnız test/texniki xidmət üçündür.

## Şifrə bərpası

- `forgot-password` hər zaman eyni cavabı qaytarır (hesab olsa da, olmasa da) və məktubu yalnız növbəyə qoyur —
  cavab da, vaxtı da hansı e-mailin qeydiyyatda olduğunu göstərmir.
- Link `App:PublicUrl` + `/reset-password#email=…&token=…`: host sorğudan yox, konfiqurasiyadan gəlir (Host header
  hücumu yoxdur), token URL fragment-dədir (server/proxy access loglarına, Referer-ə düşmür), dəyərlər URL-encode olunur.
- Token: `Auth:PasswordResetTokenLifetime` (default 1 saat), birdəfəlik — uğurlu bərpa security stamp-ı dəyişir, köhnə
  tokenlər işləmir. Açarlar (ASP.NET Data Protection) PostgreSQL-dədir: restart və çox instansiya ilə işləyir, backup-a düşür.
  Açarlar tətbiq səviyyəsində şifrələnmir (bazanın özü qapalı şəbəkədədir) — bax `deploy/README.md`.
- Bərpadan sonra **bütün sessiyalar dərhal bitir**: refresh tokenlər ləğv olunur, artıq verilmiş access tokenlər
  (JWT, 15 dəq.) də növbəti sorğuda 401 alır. JWT-də istifadəçinin security stamp-ının qısa SHA-256 izi (`sst`) var;
  hər autentifikasiyalı sorğuda bazadakı cari stamp ilə müqayisə olunur (bir primary-key sorğusu). Bərpa stamp-ı
  dəyişdirir → köhnə tokenlər keçmir. Adi profil redaktəsi stamp-ı dəyişmir. SPA 401-də bir dəfə refresh edir, o da
  alınmayınca istifadəçi çıxış edilmiş olur.
- Rate limit: `forgot`/`reset` birlikdə 5 sorğu / 15 dəq. / IP.

## Yoxlamalar

```bash
cd backend  && dotnet build && dotnet test          # real PostgreSQL tələb edir (yuxarıya bax)
cd frontend && npm run typecheck && npm run lint && npm test && npm run build
# işləyən dev stack üzərində:
cd frontend && npm run e2e && npm run e2e:qa && npm run e2e:glyphs && npm run e2e:responsive
```

Admin axını (`e2e/admin-flow.mjs`) məlumat yaradır (Laçın layihəsi, test iştirakçısı), ona görə onu **dev bazasında
yox**, ayrıca QA instansında işlədin:

```bash
# API: ayrı baza və ayrı upload qovluğu ilə (connection string-də Database=meydan_qa)
ConnectionStrings__Meydan="…Database=meydan_qa…" Uploads__StorageRoot=/tmp/meydan-qa   ASPNETCORE_URLS=http://localhost:5090 dotnet run --project backend/src/Meydan.Api --no-launch-profile
# Vite: proxy-ni QA API-yə yönəlt
cd frontend && API_TARGET=http://localhost:5090 PORT=5174 npx vite
# Axın (admin məlumatları yalnız env ilə; heç yerdə çap olunmur):
QA_ADMIN_EMAIL=… QA_ADMIN_PASSWORD=… BASE_URL=http://localhost:5174 npm run e2e:admin -- ../design-review/phase3 390
# Nəticə axını — hər dəfə YENİ, yalnız seed olunmuş QA bazası tələb edir (qalib bir dəfə seçilir):
QA_ADMIN_EMAIL=… QA_ADMIN_PASSWORD=… BASE_URL=http://localhost:5174 npm run e2e:results -- ../design-review/phase4 390
```

Phase 5 axını (`e2e/phase5-flow.mjs`) **production build** üzərində işləyir (`npm run build && npx vite preview` —
SPA təhlükəsizlik başlıqları və CSP ilə) və məktubları oxuyur: API-ni `Email__Mode=Pickup`,
`Email__PickupDirectory=<qovluq>`, `App__PublicUrl=http://localhost:4174` ilə qaldırın, sonra
`EML_DIR=<qovluq> BASE_URL=http://localhost:4174 QA_ADMIN_EMAIL=… QA_ADMIN_PASSWORD=… node e2e/phase5-flow.mjs <out> 390`.
Əlçatanlıq: eyni mühitdə `node e2e/a11y.mjs <out> 390` (axe-core, WCAG 2.1 A/AA qaydaları).

Hər inteqrasiya testi öz PostgreSQL bazasını alır (bir dəfə migrate edilmiş template-dən klonlanır) və sonda silinir.
Bir test tamamilə boş bazada bütün migration-ları sıfırdan işlədir.

## Deploy: `main` = sayt

`main`-ə hər push GitHub Actions-da yoxlanır (frontend: lint, build, testlər; backend: testlər PostgreSQL ilə) və
yoxlamalar keçəndə **avtomatik https://pryzma.az-a çıxır** ([.github/workflows/deploy.yml](.github/workflows/deploy.yml)).
Yarımçıq iş ayrıca budaqda aparılır, `main`-ə yalnız hazır olan gəlir.

- Serverə gedən açar yalnız `production` mühitindədir və onu yalnız `main` işlədə bilər; serverdə də bu açar bircə şey
  edə bilir — [deploy/scripts/ci-deploy.sh](deploy/scripts/ci-deploy.sh) (surəti `~deploy/bin`-də, release-dən kənarda).
- **07:30–09:30 və 17:00–19:00 (Bakı)** deploy rədd olunur: server QRLog ilə ortaqdır və bu saatlarda ~114 nəfər
  işə gəlib-gedişini qeyd edir. Bu saatlarda push edilsə, workflow-u sonra Actions səhifəsindən yenidən işə salın.
- Miqrasiyası olan dəyişiklik deploy zamanı avtomatik tətbiq olunur (deploy.sh əvvəlcə ehtiyat nüsxə çıxarır);
  geri qaytarıla bilməyən miqrasiyalardan (sütun/cədvəl silmək) əvvəl sahibi ilə danışın.
- Lokal test bazası: `scripts/dev-postgres.ps1`. Windows portu ehtiyata götürübsə ("could not bind … Permission
  denied"), `-Port 54433` kimi başqa portla işə salın.

## Production

**Canlı ünvan: https://pryzma.az** — paylaşılan QRLog VPS-də Docker Compose stack-i (`deploy/compose.yml`):
öz PostgreSQL 18 konteyneri (port açılmır, least-privilege `meydan` rolu), API (qeyri-root istifadəçi, yalnız daxili
şəbəkə), nginx (SPA + `/api` proxy), TLS və HSTS QRLog stack-inin Caddy-sində. Server əməliyyatları — deploy, migration,
restart, health, backup/restore, rollback, konfiqurasiya açarları — **[deploy/README.md](deploy/README.md)**-dədir.
Aşağıdakılar tətbiqin özünün tələbləridir.

### QRLog ilə giriş (Phase 6)

Giriş səhifəsində əsas seçim **"QRLog ilə daxil ol"**dur; e-mail + şifrə girişi, qeydiyyat, refresh və şifrə bərpası
olduğu kimi qalır. QRLog OpenID provayderi deyil; Kitabxana 2.0 üçün artıq işləyən imzalı server-server "vouch"
mexanizmi ümumiləşdirilib:

1. Brauzer `POST /api/auth/qrlog/start` → qısa ömürlü bilet (`QrlogLoginTickets`): təsadüfi **kod** ünvanda QRLog-a
   gedir, **claim secret** yalnız HttpOnly cookie-dədir (`meydan_qrlog`, Path `/api/auth/qrlog`, SameSite=Strict).
2. Şəxs `https://app.qrlog.az/signin/meydan?code=…&return=…` səhifəsində (QRLog tətbiqi, öz PIN girişi ilə) bir
   toxunuşla təsdiqləyir. `return` yalnız `https://prizma.qrlog.az/...` ola bilər.
3. **QRLog-un serveri** `POST /api/auth/qrlog/confirm` edir: gövdə `{code, subject (Employee Id), fullName, firstName,
   lastName, email, emailVerified:false}`, başlıqlar `X-QRLog-Timestamp` + `X-QRLog-Signature` = HMAC-SHA256(paylaşılan
   sirr, `timestamp + "
" + gövdə`). Yoxlama: sabit vaxtlı imza müqayisəsi, ±5 dəq vaxt pəncərəsi, kod birdəfəlik,
   bilet 5 dəq. Telefon, şirkət, rol göndərilmir və qəbul edilmir.
4. Brauzer cookie ilə `POST /api/auth/qrlog/claim` (poll): `signed-in` → adi sessiya (access token + refresh cookie);
   `email-taken` → e-mail artıq MEYDAN hesabındadırsa **avtomatik bağlanmır** (QRLog e-maili təsdiqlənmiş deyil) —
   şəxs həmin hesaba şifrə ilə daxil olub Profil → "QRLog hesabını bağla" seçir; `gone` → vaxtı bitib/ləğv olunub.
   Kimlik yalnız bir dəfə istifadə olunur, sonra biletdən silinir.

Kimlik əlaqəsi Identity-nin `AspNetUserLogins` cədvəlindədir (`LoginProvider=QRLog`, `ProviderKey=Employee Id`) —
MEYDAN istifadəçi açarı heç vaxt QRLog id-si deyil. İlk girişdə yalnız **Participant** rolu ilə hesab yaranır; QRLog
rolu (admin/menecer) MEYDAN-da heç nə vermir; **idarəetmə (Admin) hesabına QRLog bağlamaq da, QRLog ilə daxil olmaq da
mümkün deyil** — admin yalnız e-mail + şifrə ilə daxil olur. E-mailsiz QRLog işçisi də hesab ala bilər (`OptionalEmailUserValidator`).
Bağlantını ayırmaq (`DELETE /api/me/logins/qrlog`) yalnız şifrəsi olan hesab üçün mümkündür; bağlama/ayırma audit
olunur (`Account` / `QrlogLinked`, `QrlogUnlinked`). `GET /api/config` SPA-ya hansı qapıların açıq olduğunu deyir
(`qrlogLogin`, `passwordReset`). Konfiqurasiya: `QrlogSso__Enabled`, `QrlogSso__VouchSecret`, `QrlogSso__AuthorizeUrl`
(production-da aktivdirsə hamısı məcburidir, yoxlanılır). QRLog tərəfi: `ExternalSignInController` +
`/signin/:app` səhifəsi (AttendanceQR repo-su), `ExternalSignIn__Apps__meydan__*` mühit dəyişənləri.

### Tələblər

| | |
|---|---|
| .NET | 8.0 runtime (ASP.NET Core); build üçün SDK 8+ |
| PostgreSQL | 16+ (17/18 ilə yoxlanılıb), UTF8 baza; `pg_dump`/`pg_restore` backup üçün |
| Frontend build | Node 20+ (22 ilə yoxlanılıb): `npm ci && npm run build` → `frontend/dist/` statik fayllar |
| Şəkil/PDF emalı | SkiaSharp + PDFium native kitabxanaları NuGet paketləri ilə gəlir (linux-x64 daxil); əlavə sistem paketi lazım deyil |

### Konfiqurasiya (environment variables)

Production/Staging-də API bunlarsız **başlamır** və çatışmayan açarları adı ilə sadalayır (dəyərləri heç vaxt).

| Açar | Məcburi | Qeyd |
|---|---|---|
| `ConnectionStrings__Meydan` | ✔ | `Host=…;Port=5432;Database=meydan;Username=…;Password=…` |
| `Jwt__SigningKey` | ✔ | ≥ 32 təsadüfi simvol |
| `App__PublicUrl` | ✔ | saytın `https://` ünvanı; e-mail linkləri buradan qurulur |
| `Uploads__StorageRoot` | ✔ | mütləq yol, **tətbiq qovluğundan kənar** davamlı disk (redeploy faylları silməsin) |
| `AuthCookie__Secure` | default `true` | production-da `true` olmalıdır |
| `Email__Mode` | ✔ | `Smtp` və ya `Disabled` (açıq seçim) |
| `Email__Smtp__Host`, `Email__Smtp__Port`, `Email__Smtp__Security` (`StartTls`/`SslOnConnect`/`None`) | Smtp üçün | |
| `Email__Smtp__Username`, `Email__Smtp__Password` | relay-dən asılı | |
| `Email__FromAddress`, `Email__FromName` | Smtp üçün | göndərən (`MEYDAN` default) |
| `Cors__Origins__0…` | yox | SPA eyni origin-dən verilirsə lazım deyil; yalnız dəqiq `https://` origin-lər |
| `ReverseProxy__KnownProxies__0…` / `ReverseProxy__KnownNetworks__0…` | proxy başqa hostdadırsa | `X-Forwarded-*` yalnız bunlardan (və loopback-dən) qəbul olunur |
| `AllowedHosts` | tövsiyə | saytın host adı(ları), `;` ilə |
| `Seed__AdminEmail`, `Seed__AdminPassword` | yalnız ilk quraşdırma | aşağıya bax |
| `Auth__PasswordResetTokenLifetime` | yox | `01:00:00` |
| `RateLimiting__*`, `Uploads__Max*`, `Kestrel__MaxRequestBodyBytes` | yox | default-lar `appsettings.json`-da |

### Əməliyyatlar

**Migration** (hər deploydan əvvəl): `dotnet Meydan.Api.dll migrate` — bax "Migration-lar".

**İlk admin**: ilk `migrate`-dən əvvəl `Seed__AdminEmail` və `Seed__AdminPassword` təyin edin (güclü parol: ≥ 8 simvol,
rəqəm və kiçik hərf). Hesab yalnız həmin e-mail mövcud deyilsə yaradılır; mövcud adminin parolu heç vaxt dəyişdirilmir,
parol loga yazılmır. Yaradıldıqdan sonra `Seed__AdminPassword`-u mühitdən silin və admin şifrəsini saytda "Şifrəni
unutmuşam" ilə dəyişin.

**Health**: `/health/live` — proses cavab verir (asılılıqsız; DB kəsilməsi prosesi restart etdirməməlidir);
`/health/ready` (və `/health`) — PostgreSQL əlçatandır və gözləyən migration yoxdur + storage yazıla/oxuna bilir
(müvəqqəti probe faylı dərhal silinir). Cavabda yalnız `{"status","checks":{"database","storage"}}` — yol, host,
versiya, xəta mətni yoxdur. 503 = hazır deyil.

**Loglar**: stdout-a JSON (bir sətir = bir hadisə), UTC vaxt, `CorrelationId` scope-da. Hər sorğu üçün: metod, yol
(query string-siz), status, müddət. Heç vaxt: parol, JWT, refresh token, reset token, connection string, SMTP parolu,
təklif cavabları, fayl məzmunu, tam e-mail (maskalanır). Cavablarda `X-Correlation-ID`; 500 cavabı yalnız
"Gözlənilməz xəta…" + bu istinadı göstərir — stack trace, SQL, fayl yolu heç vaxt.

**Storage**: `Uploads__StorageRoot` altında: `yyyy/MM/…` — iştirakçıların **orijinal** faylları (gizli, dəyişdirilmir);
`derived/…` — ictimai təhlükəsiz nüsxələr; `covers/…` — layihə örtükləri. Yazılar atomikdir (`*.partial` → rename);
qəza nəticəsində qalan 1 saatdan köhnə `*.partial` fayllar startda silinir. Fayllar heç vaxt statik qovluq kimi
internetə açılmamalıdır — hamısı API-nin icazə yoxlamasından keçir.

### Deploy və artıq açıq olan tablar

Deploy zamanı kiminsə brauzerində köhnə bundle işləyir. O tab yeni bir səhifəyə keçəndə **köhnə** build-in chunk
faylını istəyir — adı məzmun hash-ı olduğu üçün yeni build-də başqa addır. Üç qat qoruyur:

1. **Köhnə fayllar dərhal silinmir.** `/assets` qovluğu artıq image-in içində deyil, `meydan_webassets` adlı
   volume-dur. Hər release öz fayllarını ora **əlavə edir**, əvəz etmir (`frontend/nginx-assets.sh`):
   - eyni adlı fayl varsa toxunulmur (ad = məzmun hash-ı, üzərinə yazmağın mənası yoxdur);
   - cari build-in bütün faylları hər başlanğıcda `touch` olunur — deploy uzun müddət dəyişməsə də cari release
     heç vaxt "köhnə" sayılmır;
   - `ASSET_RETENTION_DAYS`-dən (default **30 gün**) köhnə fayllar silinir — onlar artıq işləməyən build-ə aiddir.
2. **Cache başlıqları.** `index.html` → `Cache-Control: no-cache` (hər dəfə yoxlanılır, deploy dərhal çatır);
   `/assets/*` → `public, max-age=31536000, immutable`. `index.html` image-in içində qalır, volume-da deyil, ona
   görə hər release öz `index.html`-ini verir.
3. **Chunk yüklənmirsə istifadəçi boş səhifə görmür.** `StaleBundleBoundary` (`frontend/src/components/StaleBundle.tsx`)
   həm React-in tutduğu `lazy()` xətasını, həm də Vite-in `vite:preloadError` hadisəsini tutur və
   **“Yeni versiya mövcuddur — Səhifəni yenilə”** düyməsini göstərir. Digər xətalar olduğu kimi yuxarı ötürülür —
   real bug deploy kimi göstərilmir.

Deploy-ın özü atomikdir: arxiv `releases/<stamp>.unpacking` qovluğuna açılır və yalnız tam açıldıqdan sonra
`mv` ilə öz adına keçir; `current` simlinki `/health/ready` cavab verəndən sonra `ln -sfn` ilə dəyişir (tək
addım). Konteynerlərin yenidən yaradılması bir neçə saniyə çəkir — bu müddətdə sorğular proxy tərəfindən
gözləyir; sıfır-downtime deyil, amma yarımçıq release heç vaxt verilmir.

### Backup və restore

Production cədvəli və konteyner-əsaslı skriptlər (`deploy/scripts/backup.sh`, `restore-test.sh`) — bax
[deploy/README.md](deploy/README.md). Aşağıdakı host skriptləri eyni formatı verir (`pg_dump` host-da quraşdırılıbsa).

Tam bərpa üçün iki şey lazımdır: **PostgreSQL bazası** (bütün məlumat, hesablar, nəticələr, audit, Data Protection
açarları) və **storage kökü** (orijinallar, törəmələr, örtüklər). Sirlər (environment / secret store) ayrıca saxlanılır.

```bash
# backup: meydan-db-<stamp>.dump + meydan-files-<stamp>.tar.gz + meydan-<stamp>.sha256
PGHOST=… PGUSER=… PGDATABASE=meydan scripts/backup.sh /var/lib/meydan/uploads /var/backups/meydan
# restore — yalnız YENİ boş bazaya və YENİ boş qovluğa (mövcud məlumatın üstünə yazmaqdan imtina edir)
PGHOST=… PGUSER=… scripts/restore.sh /var/backups/meydan 20260918T120000Z meydan_restored /var/lib/meydan/uploads-restored
# sonra: ConnectionStrings__Meydan və Uploads__StorageRoot-u yeni yerlərə yönəldin, `migrate`, start, /health/ready
```

Parol yalnız `PGPASSWORD` və ya `~/.pgpass` (chmod 600) ilə; backup faylları `umask 077` ilə yaranır. Arxivə
`*.partial` və health probe faylları düşmür. Production-da: hər gecə 03:20 (Bakı), 30 gün saxlanma, bazar günləri
restore sınağı ayrıca bazaya (`/etc/cron.d/meydan`).

### Təhlükəsizlik

- **Başlıqlar (API)**: `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`,
  `Permissions-Policy`, `Cross-Origin-Opener/Resource-Policy`, CSP `default-src 'none'; frame-ancestors 'none'`;
  fayl endpoint-ləri `sandbox` CSP. HSTS (1 il) production-da HTTPS cavablarında. `Server` başlığı yoxdur.
- **Başlıqlar (SPA)**: `frontend/security-headers.ts` — `vite preview` onları tətbiq edir, e2e testlər real build-i bu
  CSP altında yoxlayır; production web server eyni dəyərləri göndərməlidir (+ HTTPS-də `upgrade-insecure-requests`).
  Source map-lər build-ə daxil deyil.
- **CORS**: default heç bir cross-origin yoxdur; `Cors:Origins` yalnız dəqiq origin-lər, wildcard heç vaxt credentials ilə.
- **Rate limit** (IP və ya istifadəçi üzrə; 429 Azərbaycan dilində, `Retry-After`): login/qeydiyyat 20/dəq., şifrə
  bərpası 5/15 dəq., təklif yazma 120/dəq., yükləmə 40/dəq. Admin endpoint-ləri limitsiz.
- **Refresh limiti sessiya üzrədir, IP üzrə yox** (bir ofis NAT-ı / mobil CGNAT arxasında çox adam bir IP-dən gəlir):
  refresh cookie-si `<ailə>.<sirr>` formasındadır — ailə (sessiyanın id-si, rotasiyada dəyişmir) limiter açarı olur,
  yalnız birtərəfli SHA-256 barmaq izi kimi. Sessiya başına 20/dəq.; üstündə IP başına kobud qoruma 1200/dəq. (yalnız
  cookie-li sorğular); cookie-siz 600/dəq./IP və xarab cookie 30/dəq./IP — hər ikisi bazaya getmədən cavablanır; bir
  IP-dən heç nəyə uyğun gəlməyən cookie-lər dəqiqədə 60-dan çox olsa, o IP-nin növbəti axtarışları bazadan əvvəl 429 alır.
  429 heç vaxt çıxış demək deyil: cookie və sessiya toxunulmaz qalır; SPA onu müvəqqəti vəziyyət kimi göstərir
  ("Giriş vəziyyəti bu an yoxlanıla bilmədi"), `Retry-After`-a uyğun məhdud sayda yenidən yoxlayır. Yalnız 401 çıxışdır.
- **Body limit**: bütün sorğular 2 MB (`Kestrel:MaxRequestBodyBytes` = 2 097 152); yalnız iki yükləmə endpoint-i
  (təklif faylı, admin örtük şəkli) öz limitini fayl limiti + 1 MB multipart ehtiyatına qaldırır (10 MB → 11 534 336
  bayt) və sorğuda **tam bir fayl** qəbul edir. Limit Kestrel səviyyəsindədir: böyük body oxunmur/buferlənmir. Yükləmə
  formu model binding zamanı yox, action-da oxunur — ölçü və pozuq multipart xətaları Azərbaycan dilində sahə xətasıdır.
- **Antivirus**: qoşulmayıb. `IFileScanner` genişlənmə nöqtəsidir (məs. ClamAV); qoşulmadıqca fayllar `NotScanned`
  kimi qeyd olunur və adminə "Antivirus yoxlaması qoşulmayıb" göstərilir — "yoxlanılıb" iddiası heç vaxt edilmir.

### İctimai fayllar (nəticədən sonra)

```
Orijinal yükləmə ──► gizli storage (dəyişdirilmir, yalnız sahibi/admin)
        │
        └─► server tərəfindən yeni WEBP (arxa planda) ──► derived/ ──► qalereya (yalnız dərcdən sonra, hər sorğuda yoxlanılır)
```

- Şəkillər (PNG/JPG/WEBP): başlıqdan ölçü yoxlanılır (≤ 40 MP, ≤ 12000 px — decompression bomb yüklənmədə rədd olunur),
  JPEG kiçildilmiş decode olunur, EXIF orientasiyası piksellərə tətbiq edilir, sRGB-yə çevrilib ≤ 1600 px WEBP kimi
  yenidən kodlaşdırılır. EXIF, GPS, XMP, mətn blokları, thumbnail-lar və orijinalın ICC profili nüsxəyə keçmir
  (kodlayıcı öz standart 456 baytlıq RGB profilini yazır — şəxsi məlumat deyil).
- PDF: **PDF-in özü heç vaxt ictimai deyil.** Qalereyada yalnız birinci səhifənin şəkli (PDFium, annotasiyasız, formsuz,
  JavaScript mühərriksiz build, ölçü məhdud) — sonra eyni WEBP emalı. Orijinal PDF admin/sahibi üçün gizli endpoint-dən.
- Nüsxə yaradıla bilməsə (zədəli fayl, dekoder xətası, orijinal tapılmır): status `Failed` + səbəb kodu, qalereyada yer
  tutucu, admin nəticə bölməsində və təklif səhifəsində xəbərdarlıq — orijinal heç vaxt ehtiyat variant kimi göstərilmir.
  Yalnız infrastruktur xətaları (disk I/O, baza) faylı `Pending` saxlayır ki, növbəti keçiddə təkrar cəhd olunsun.
- Emal ardıcıldır (bir fayl bir vaxtda) və məhduddur: yeni yükləmələr yaddaşda məhdud növbə ilə (`Uploads:DerivativeQueueCapacity`,
  1000) **birinci** emal olunur; köhnə `Pending` fayllar startdan sonra və hər `Uploads:DerivativeSweepMinutes` (10)
  dəqiqədə bazadan kiçik partiyalarla (`DerivativeBackfillBatchSize`, 20) oxunur, hər fayldan sonra
  `DerivativeBackfillPauseMs` (200 ms) fasilə. Böyük backlog startu gecikdirmir, yeni yükləmələri gözlətmir.
- Public URL `…/gallery/{ref}/files/{id}`: nəticə dərc olunub + müsabiqə bağlıdır + iş rədd edilməyib + sahə ictimaidir +
  nüsxə hazırdır — hər sorğuda. `Cache-Control: no-cache` + ETag: dərcdən çıxarılan kimi köhnə URL 404 olur.
- Admin hər təklif üçün qalereyanın **önizləmə şəklini** seçə bilər (yalnız həmin təklifin ictimai, hazır nüsxəsi olan
  faylı); seçim yoxdursa birinci şəkil, sonra PDF səhifəsi, sonra layihə naxışı.
- Admin örtük şəkilləri də yüklənəndə eyni emaldan keçir (yalnız metadata-sız WEBP saxlanılır).

## API

| Metod | Yol | |
|---|---|---|
| GET | `/api/home`, `/api/projects[/{slug}]`, `/api/competitions[/{slug}]` | ictimai |
| POST | `/api/auth/register`, `login`, `refresh`, `logout`, `forgot-password`, `reset-password` | |
| POST | `/api/submissions` | qaralama aç / davam et |
| GET/PUT | `/api/submissions/{id}` | sahibi |
| POST/DELETE/GET | `/api/submissions/{id}/files[/{fileId}]` | sahibi |
| POST | `/api/submissions/{id}/check`, `/api/submissions/{id}/submit` | sahibi |
| GET/PUT | `/api/me/profile`, GET `/api/me/submissions` | Participant |
| GET | `/api/media/covers/{…}` | ictimai (yalnız örtük şəkilləri) |
| GET | `/api/results`, `/api/competitions/{slug}/results` | ictimai — yalnız dərc olunmuş nəticə, əks halda 404 |
| GET | `/api/competitions/{slug}/gallery[?category=]`, `…/gallery/{reference}`, `…/gallery/{reference}/files/{fileId}` | ictimai — yalnız dərcdən sonra |
| GET | `/api/admin/competitions/{id}/result` | Admin |
| POST/PUT | `/api/admin/competitions/{id}/result/winner` (seç / dəyiş), PUT `…/announcement` | Admin |
| POST | `/api/admin/competitions/{id}/result/publish`, `…/unpublish` | Admin |
| GET | `/api/admin/dashboard`, `participants`, `audit`, `upload-policy` | Admin |
| GET/POST/PUT/DELETE | `/api/admin/projects[/{id}]`, PUT `{id}/status`, POST/DELETE `{id}/cover` | Admin |
| GET/POST/PUT/DELETE | `/api/admin/competitions[/{id}]`, PUT `{id}/status` | Admin |
| GET/POST/PUT/DELETE | `/api/admin/competitions/{id}/categories[/{catId}]`, PUT `…/order`, POST `…/import` | Admin |
| GET/POST/PUT/DELETE | `/api/admin/competitions/{id}/fields[/{fieldId}]`, PUT `…/order` | Admin |
| GET | `/api/admin/submissions` (filtr + səhifələmə), `/api/admin/submissions/{id}` | Admin |
| PUT | `/api/admin/submissions/{id}/preview` (qalereya önizləməsi), GET `…/files/{fileId}/public` (təhlükəsiz nüsxə) | Admin |
| GET | `/health/live`, `/health/ready`, `/health` | ictimai (yalnız status) |
| PUT/POST | `/api/admin/submissions/{id}/status`, `/api/admin/submissions/{id}/notes` | Admin |
