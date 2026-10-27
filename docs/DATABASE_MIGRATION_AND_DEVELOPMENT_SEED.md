# Database migration và development seed — 24/10/2026

**Cập nhật mới nhất 25/10/2026:** migration **013_lecturer_business_codes.sql** thêm `LecturerCodeSequences` và bảo đảm unique index mã giảng viên. API và seed mới cấp mã `GV-{mã bộ môn bỏ BM-}-{D4}` trong transaction có khóa; giữ nguyên toàn bộ mã cũ. Xem [báo cáo mã giảng viên và header danh mục](LECTURER_CODES_AND_CATALOG_HEADERS.md) cho kết quả 189 backend tests, 37 frontend tests và kiểm chứng UI thật. Các ghi chú 001–012 bên dưới là lịch sử triển khai trước đợt 013.

**Cập nhật 25/10/2026:** đã thêm migration **012_catalog_business_codes.sql**, `CourseCodeCounters` và `CatalogSeedIdentities`, cấp mã BM/MH tự động an toàn đồng thời. Không đổi mã/ID dữ liệu cũ; seed mới đúng quy tắc và seed cũ được nhận diện lại để không tạo bản sao. Migration/seed replay giữ nguyên số bản ghi mọi bảng. Xem [báo cáo mã Bộ môn/Môn học](CATALOG_AUTOMATIC_CODES.md) cho schema, API, kiểm thử và mã thật đã tạo qua UI. Các phần 001–011 bên dưới là lịch sử triển khai trước đợt 012.

Đã triển khai trên `F:\KLCN\HTBAM-Git`, Compose project `htbam-angular`, database `HTBAM`, volume SQL hiện có `infra_htbam-sql`. Giữ nguyên các thay đổi chưa commit đã có trước phiên làm việc. Không commit hoặc push.

## Trạng thái ban đầu và nguyên nhân

Compose cũ chỉ khởi động SQL/MinIO/AI/backend/web, không chạy migration, không chờ SQL healthy. Backend gọi `AcademicSeed` rồi thử lại cả lỗi schema tối đa 20 lần. Khi code cần schema 007 nhưng database chưa được nâng cấp, truy vấn `AcademicTerms` gây `Invalid object name`, trì hoãn khởi động và khiến web chờ backend.

Ở lần **truy vấn thực tế trong phiên này**, sau khi khởi động Docker Desktop, `AcademicTerms` và tám bảng học vụ của 007 **đã tồn tại**, có 2 học kỳ nháp và đủ 32 tiết A/B. Không có bảng lịch sử migration, không có giai đoạn, dòng lịch hoặc quyền lớp. Log backend lúc đó ghi academic seed thành công. Vì vậy không kết luận rằng 007 vẫn thiếu ở thời điểm truy vấn, hoặc suy ra các script trước đó đã chạy chỉ từ tên tệp.

Các số liệu trước thay đổi: 7 user, 5 teacher, 2 khoa, 4 bộ môn, 2 lớp sinh hoạt, 5 sinh viên, 3 môn, 1 phòng, 4 lớp học phần, 4 enrollment, 4 session, 8 session student, 6 nhiệm kỳ, 4 dạy thay, 4 video, 3 face enrollment, 18 ảnh face và 654 audit log. Bản chụp nằm trong thư mục scratch bị Git bỏ qua `.runtime-fix-backup-database/before-counts.txt`.

## Cơ chế migration và nguồn chuẩn

`database/sqlserver` là nguồn chuẩn; các SQL cùng tên ở `database/` là bản tương thích. Chỉ chạy một nguồn. Kiểm tra/đồng bộ từ thư mục gốc:

```powershell
node database/sync-sqlserver.cjs --check
node database/sync-sqlserver.cjs
```

Runner `SqlMigrationRunner` đọc đủ artifact trước khi chạy; chỉ dùng database đích trong connection string; xử lý batch `GO`. Dùng application lock ở SQL Server để tuần tự hóa tạo database và toàn bộ tiến trình migration; trả khóa rõ ràng khi kết thúc. Mỗi script chạy trong transaction, bao gồm tất cả batch và việc ghi lịch sử. Có `XACT_ABORT`, rollback khi lỗi và thông báo tên script thất bại. Không ghi lịch sử thành công trước khi commit. DDL/seed của script hiện có nằm trong transaction ngoài; transaction lồng trong SQL phải cân bằng.

`SchemaMigrations` lưu `ScriptName`, SHA-256 nội dung đã chuẩn hóa CRLF/LF, `AppliedAt` UTC. Chạy lại bỏ qua script có cùng checksum; thay nội dung script đã ghi nhận sẽ bị từ chối. Từ thời điểm này, bổ sung thay đổi bằng **migration mới**, không sửa script đã áp dụng. Database cũ không có lịch sử được đối chiếu bằng các script additive/idempotent, không gán trạng thái thành công chỉ vì có một bảng bất kỳ.

Danh sách đã áp dụng/ghi nhận thành công trên HTBAM:

| Script | Nội dung |
| --- | --- |
| 001_schema.sql | Baseline v3; bổ sung bảng/index còn thiếu, giữ bảng và dữ liệu có sẵn |
| 002_seed.sql | Vai trò, danh mục nền và chính sách/rule mặc định; không có tài khoản đăng nhập |
| 004_add_llm_comment_metadata.sql | Metadata nhận xét |
| 005_add_catalog_notes.sql | Ghi chú danh mục; đã bỏ `USE HTBAM` hardcode |
| 006_add_annotated_videos.sql | Nhiệm kỳ, dạy thay, giảng viên gốc và phân loại video |
| 007_academic_scheduling.sql | Học kỳ, giai đoạn, lịch, tuần, giờ, quyền lớp, liên kết buổi |
| 008_schema_integrity.sql | Nhãn/trạng thái kỳ DEMO, cờ session DEMO, FK và unique index bổ sung |
| 009_partial_schema_relations.sql | Bổ sung FK bị thiếu khi cột đã được tạo từ một lần triển khai dở |

`003_dev_sample.sql` là bộ dữ liệu mẫu SQL cũ, **không phải migration bắt buộc và không được runner chạy**. Development fixture mới do backend tạo. `database/legacy/004_upgrade_v1_to_v2.sql` không nằm trong luồng Docker tự động.

007 đã tách các phép thêm cột ghép để xử lý cột chỉ tồn tại một phần, sửa các bảng học vụ tạo dở và bổ sung ràng buộc còn thiếu. 009 nhận diện FK tương đương qua metadata, không phụ thuộc tên FK tự sinh. Dữ liệu vi phạm unique/FK hoặc bảng tạo dở thiếu thông tin bắt buộc không thể suy ra an toàn sẽ làm migration dừng với lỗi, thay vì tự xóa hay sửa dữ liệu nghiệp vụ.

Backend không dùng `EnsureCreated` thay migration. Trước khi mở cổng hoặc seed, nó kiểm tra checksum của toàn bộ migration và sự tồn tại thực tế của mọi bảng/cột mà EF sử dụng. Lỗi schema dừng ngay với migration/cột cần xử lý; không retry seed truy vấn bảng chưa migrate. Migration và seed chạy bằng service riêng; nhiều backend không tự chạy migration.

## Schema và mô hình

Tái sử dụng toàn bộ bảng hiện có. Các bảng học vụ của 007: `AcademicTerms`, `TermPhases`, `AcademicWeekCalendars`, `BellTimeVersions`, `BellPeriods`, `ClassSchedules`, `ScheduleExceptions`, `ClassSectionGrants`. Tám bảng này đã có khi kiểm tra database hiện tại; runner cũng đã được thử trên database schema 006 chưa có chúng.

Bảng mới thực tế trong database đang dùng: `SchemaMigrations`. Các bổ sung chính:

- `AcademicTerms.IsDevelopment`, `Status` (`DRAFT`/`ACTIVE`/`COMPLETED`), giữ unique năm học + học kỳ.
- `Sessions.IsDevelopment`; chỉ session DEMO đã có dòng lịch mới được phép hoàn tất fixture báo cáo khi chưa có camera/video. Luồng production vẫn yêu cầu nguồn hợp lệ để chạy phân tích.
- FK chính sách chuyên cần của lớp, người cấp quyền và các FK lịch/tuần/phiên bản giờ còn thiếu.
- Unique mã phiên bản giờ và ngoại lệ ngày học; index phòng/khoảng ngày.
- Đồng bộ 117 ánh xạ độ dài/kiểu chuỗi EF với SQL và các FK scalar không có navigation. SQL integration test đối chiếu mọi FK và unique index trong model với metadata database.

Loại phòng dùng trường `Rooms.RoomType`, năm học dùng `AcademicTerms.AcademicYear`; không tạo bảng trùng ý nghĩa. Phân công chính dùng `ClassSections.TeacherId`, dạy thay dùng `SessionSubstitutions`, quyền lớp dùng `ClassSectionGrants`. Báo cáo được lấy từ `Attendance`, `StudentSessionSummaries`, `ClassSessionSummaries` và sự kiện sẵn có, không tạo một hệ báo cáo song song.

## Phân lớp seed và dữ liệu đã thêm

1. `SystemSeed`: role, attendance policy, alert rules; không tạo tài khoản demo.
2. `AcademicSeed`: học kỳ 2025–2026 còn nháp và bảng giờ A/B theo dữ liệu được cung cấp; phục hồi tiết còn thiếu, giữ giá trị đang có.
3. `DemoDataSeeder`: chỉ được lệnh `--seed` gọi khi **đồng thời** `ASPNETCORE_ENVIRONMENT=Development` và `Seed__Development=true`. Mặc định ứng dụng ngoài Compose là false. Production chỉ seed hệ thống/học vụ, kể cả nếu bật nhầm cờ development.

Seed mới dùng tiền tố `DEMO-`, user `demo.`, email `example.invalid`, khóa dòng lịch/request xác định bằng SHA-256. Chỉ thêm khi chưa có mã/khóa. Không reset mật khẩu, vai trò, hồ sơ, trạng thái, chức vụ đã thu hồi, grant hoặc lịch của bản ghi tồn tại. Một lớp đã tồn tại không được sinh lại buổi. Toàn bộ fixture nằm trong transaction và dùng khóa nghiệp vụ học vụ. Nếu có lỗi, rollback toàn bộ fixture.

Số lượng **bổ sung của fixture mới** và tổng database đã xác minh:

| Nhóm | DEMO thêm | Tổng sau seed |
| --- | ---: | ---: |
| User / user-role | 13 / 13 | 20 / 20 |
| Teacher | 13 | 18 |
| Khoa / bộ môn | 1 / 4 | 3 / 8 |
| Nhiệm kỳ quản lý | 5: 4 active, 1 đã kết thúc | 11 |
| Lớp sinh hoạt | 3 | 5 |
| Sinh viên | 60: 57 active, 3 inactive | 65 |
| Môn học | 13: 12 active, 1 inactive | 16 |
| Phòng | 15 | 16 |
| Học kỳ / giai đoạn | 2 / 10 | 4 / 10 |
| Lớp học phần | 12: 11 active, 1 inactive | 16 |
| Enrollment | 228 | 232 |
| Dòng lịch / quyền lớp | 14 / 2 | 14 / 2 |
| Buổi học | 75: 3 completed, 72 draft | 79 |
| Roster buổi học | 1.425 | 1.433 |
| Điểm danh | 57 | 57 |
| Stable identity / track / identity link | 45 / 45 / 45 | 45 / 45 / 45 |
| Behavior / alert | 45 / 39 | 45 / 39 |
| Student summary / class summary | 57 / 3 | 57 / 3 |
| Audit đánh dấu fixture | 1 | Thêm audit đăng nhập khi kiểm thử API |

Phòng gồm 5 THEORY, 4 COMPUTER, 2 LAB, 3 ZOOM và 1 THEORY inactive. Môn có đủ tín chỉ 1/2/3. Có một khoa DEMO riêng, ba bộ môn có trưởng và bộ môn `DEMO-EMPTY` trống, kèm một nhiệm kỳ đã thu hồi để thử bổ nhiệm lại. Không chiếm hoặc chuyển chức vụ của khoa/bộ môn đang có.

Lịch có môn nguyên kỳ, các giai đoạn 1/2/3, LT/TH hai thứ trong tuần, đổi phòng giữa hai khoảng ngày, học Zoom và chọn tuần lẻ. Các lớp có roster 19 sinh viên active. Dùng chính `ClassSchedulePlanner.Prepare/Apply` và `AcademicRules` để kiểm tra xung đột/sinh buổi, không chèn buổi bằng SQL riêng. Tại ngày 24/10/2026 có 38 buổi DEMO trong quá khứ và 37 trong tương lai; không seed điểm danh cho buổi tương lai.

Ba buổi báo cáo có 33 PRESENT, 12 PARTIAL (fixture đến trễ 35 phút), 12 ABSENT. Mô hình hiện tại biểu diễn đi trễ qua `PARTIAL` và thời gian có mặt, chưa có enum `LATE` riêng. Dữ liệu tracking/identity/behavior là **giả lập có nhãn `DEMO-SYNTHETIC-NOT-INFERENCE`/`DEMO_FIXTURE`**, không phải kết quả nhận diện thực. Tổng hợp dùng `SummaryService`, cảnh báo dùng `AlertEngine`. Có sáu nhãn hành vi hiện có. Không tạo ảnh sinh trắc, embedding, file video giả, camera giả hoặc analysis job giả; 4 video và 18 ảnh đã có được giữ nguyên.

## Tài khoản kiểm thử

Tất cả tài khoản mới có User, UserRole và Teacher. Chức vụ quản lý nằm trong `ManagementAssignments`, không tạo role “trưởng khoa” song song.

| Tài khoản | Role / chức vụ / trường hợp kiểm thử |
| --- | --- |
| demo.admin | ADMIN; quản trị hệ thống, không được tạo giảng viên qua API |
| demo.dean | LECTURER + trưởng khoa DEMO |
| demo.head1 | LECTURER + trưởng bộ môn DEMO-HTTT |
| demo.head2 | LECTURER + trưởng bộ môn DEMO-CNPM |
| demo.head3 | LECTURER + trưởng bộ môn DEMO-KHMT |
| demo.lecturer1 … demo.lecturer6 | Sáu giảng viên không có chức vụ quản lý đang hiệu lực |
| demo.editor | Chủ lớp DEMO-LHP-01; được thêm/sửa/xóa chi tiết và quản lý roster đúng lớp này; không được ngừng lớp |
| demo.viewer | Chủ lớp DEMO-LHP-04; chỉ xem, các cờ sửa/roster đều false |

Mật khẩu tài khoản mới lấy từ `SEED_DEFAULT_PASSWORD`. Nếu không đặt, fallback **chỉ Development cục bộ** là `HTBAM-Local-Demo!2026#`. Database lưu hash ASP.NET Identity; log seed chỉ in tên tài khoản và role/chức vụ, không in mật khẩu hoặc hash. Đổi biến không reset mật khẩu tài khoản đã có. Tài khoản cũ `admin`, `dean`, `lecturer`… và mật khẩu của chúng được giữ nguyên; danh sách thử khuyến nghị ở trên dùng prefix `demo.`.

## Ngày DEMO và dữ liệu chính thức còn thiếu

Giữ nguyên HK1/HK2 2025–2026 dưới dạng chưa xác nhận. HK2 có `StartsOn=2026-01-12` theo thông tin người dùng cung cấp; không tự điền ngày kết thúc/giai đoạn/T1 của học kỳ này.

Fixture dùng **hai học kỳ khác**, năm 2026–2027, để không ghi đè cấu hình đã có:

- HK1 DEMO: 17/08/2026–20/12/2026, T1 thứ Hai 17/08/2026.
- HK2 DEMO: 11/01/2027–16/05/2027, T1 thứ Hai 11/01/2027.
- Các khoảng chia 2 và chia 3 được tính độc lập và lưu dưới `EXPLICIT`; `DatesConfirmed`, `PhasesConfirmed`, `WeekMappingConfirmed` vẫn false. `IsDevelopment=true` cho phép planner dùng chúng chỉ khi development được bật.
- Trang ứng dụng có cảnh báo **“Dữ liệu lịch mẫu, chưa phải lịch chính thức”**, danh mục/lớp có nhãn DEMO. API cũng trả nhãn này. Production không được dùng planner với học kỳ DEMO.

Bảng giờ có 16 tiết A và 16 tiết B đúng các giờ được cung cấp, hiệu lực từ **12/01/2026**. Không seed phiên bản trước ngày này và không áp dụng ngược. Lịch cũ thiếu bảng giờ được báo thiếu cấu hình qua API; seed và startup không cố sinh lại buổi cũ.

Cần thay bằng nguồn xác nhận trước khi dùng chính thức: ngày bắt đầu/kết thúc HK1 2025–2026; ngày kết thúc HK2; hai bộ ranh giới giai đoạn độc lập; mốc tuần T1 theo từng nhóm; ngày nghỉ/ngoại lệ và bảng giờ trước 12/01/2026. Mọi ngày/giai đoạn/T1 của hai kỳ 2026–2027 kể trên đều là dữ liệu kiểm thử.

## Vận hành Docker

Từ `infra`:

```powershell
docker compose config --quiet
docker compose build backend web
docker compose up -d --force-recreate
docker compose ps -a
```

Thứ tự: SQL healthy → `migrate` exit 0 → `seed` exit 0 → backend healthy → web. MinIO có healthcheck và backend chờ healthy để tạo bucket; AI có healthcheck riêng, backend chỉ cần service được khởi động, không yêu cầu model đã sẵn sàng. `/health` là liveness nhanh, không gọi AI/Gemini; `/api/health` kiểm tra từng phụ thuộc.

Chạy lại riêng migration/seed và chứng minh idempotence:

```powershell
docker compose run --rm --no-deps migrate
docker compose run --rm --no-deps seed
# Từ thư mục gốc, khi không có tác vụ ghi khác chạy đồng thời:
powershell -NoProfile -ExecutionPolicy Bypass -File tests/verify_development_seed.ps1
```

Tắt tạo thêm development data bằng `Seed__Development=false` trong `infra/.env`, rồi recreate service; hoặc đặt `ASPNETCORE_ENVIRONMENT=Production`. Việc tắt không xóa dữ liệu DEMO đã tạo. `SEED_DEFAULT_PASSWORD` chỉ ảnh hưởng tài khoản mới. `MSSQL_SA_PASSWORD` phải khớp volume đang có; đổi biến không đổi mật khẩu SA trong volume.

Chạy ngoài Docker từ `services/backend/src/HTBAM.Api` với connection string đã đặt:

```powershell
dotnet run -- --migrate
$env:ASPNETCORE_ENVIRONMENT='Development'
$env:Seed__Development='true'
dotnet run -- --seed
dotnet run
```

Không dùng `EnsureCreated`, không cần xóa volume hoặc chạy lại 003 SQL để có dữ liệu thử. Migration chỉ tạo database khi tên đích chưa tồn tại; không drop/truncate database đang dùng.

## Kết quả xác minh thực tế

| Kiểm tra | Kết quả |
| --- | --- |
| `dotnet build .../HTBAM.Api.csproj --no-restore` | Đạt, 0 lỗi/0 cảnh báo |
| Backend tests với `HTBAM_TEST_SQL_CONNECTION` | **103/103 đạt, 0 bỏ qua** |
| Migration v6 → schema mới, bảng/cột 007 tạo dở | Đạt trên SQL Server thật, giữ bản ghi nghiệp vụ |
| Lỗi cuối batch 007 | Rollback cả DDL và seed, không ghi history 007; chạy lại thành công |
| Migration lặp / checksum đổi | Bỏ qua lần hai; từ chối script đã bị thay đổi |
| FK và unique index model ↔ SQL | Đạt đối chiếu metadata; trùng năm học/học kỳ bị SQL từ chối |
| Sinh mã đồng thời | 24 tạo phòng đồng thời trên SQL và 2.000 mã song song không trùng |
| Seed lặp / bảo toàn sửa hồ sơ và thu hồi chức vụ | Đạt integration test; số lượng **mọi bảng** trên HTBAM không đổi sau replay |
| Quy tắc tín chỉ, giờ hiệu lực, tuần, nhiều dòng, sinh buổi, xung đột | Đạt backend tests và planner thật |
| `npm test` | 13/13 kiểm thử DOM/HTTP doubles, kiểm tra cú pháp 33 tệp, fixture thời khóa biểu và SQL mirror đạt |
| `npm run build` | Angular production đạt; initial bundle 375,76 kB |
| API cách ly `tests/academic_api_smoke.ps1` | **43 kiểm tra HTTP đạt**, gồm tạo giảng viên, chuyển chức vụ, lưu/replay lớp, quyền/thu hồi và chặn thao tác |
| Production / Development tắt seed | Đã chạy CLI seed trên database cách ly: không tạo tài khoản hoặc kỳ DEMO khi Production, không tạo tài khoản khi `Seed__Development=false` |
| API Docker `tests/development_seed_api_smoke.cjs` | **47 kiểm tra HTTP đạt**, đủ 13 đăng nhập, dashboard, roster, báo cáo, bộ lọc, grants, từ chối vượt quyền, LLM chưa cấu hình |
| Angular assets / Nginx proxy | Index, 2 script khởi động và `/api/health` qua cổng web đều HTTP 200 |
| Compose config/build/up | Đạt; migrate và seed exit 0 |
| SQL / backend / AI / MinIO | Healthy; backend restart count 0 |
| Web / Swagger / health | HTTP 200; basic health qua localhost IPv4 khoảng 2–3 ms trong lần đo bằng curl |
| Gemini chưa có key | Backend healthy; `configured=false`, có fallback; không gọi Gemini thật trong smoke test |

Truy cập: web `http://127.0.0.1:5173`, API health `http://127.0.0.1:5000/health`, Swagger `http://127.0.0.1:5000/swagger/index.html`, AI `http://127.0.0.1:8001/health`, MinIO `http://127.0.0.1:9000/minio/health/live`. `curl.exe -4 http://localhost:5000/health` và web localhost IPv4 đều 200. Một số client trên máy này timeout khi dùng localhost tự chọn IPv6; dùng 127.0.0.1 hoặc `curl -4`. Không sửa cấu hình mạng/hosts của Windows.

## Tệp thay đổi trong đợt database này

- Infra: `.dockerignore`, `infra/docker-compose.yml`, `infra/.env.example`, `services/backend/Dockerfile`.
- SQL: 001, 005, 007; thêm 008 và 009 trong `database/sqlserver` và mirror root; `database/sync-sqlserver.cjs`.
- Startup: `HTBAM.Api/Program.cs`, `appsettings.json`; controllers `SystemController`, `ClassSectionsController`; support `ClassSchedulePlanner`, `DemoDataSeeder`.
- Data/domain: `AcademicScheduling.cs`, `Entities.cs`, `AcademicRules.cs`; `AppDbContext.cs`, `AppDbContext.Academic.cs`, `AppDbContext.Relationships.cs`, `AppDbContext.SqlColumnSizes.cs`, `SqlMigrationRunner.cs`, `SystemSeed.cs`, `AcademicSeed.cs`, fallback password trong helper `DevelopmentSeeder.cs` cũ. Helper cũ không còn được startup tự gọi.
- Frontend: `package.json`, `core/models.ts`, `layout/shell.component.ts`, `features/academic-configuration.component.ts`, `features/class-sections.component.ts`.
- Tests: `AcademicSqlServerTests.cs`, `DemoDataSeederTests.cs`, `academic_api_smoke.js/.ps1`, `development_seed_api_smoke.cjs`, `run_frontend_checks.cjs`, `verify_development_seed.ps1`.
- Tài liệu: báo cáo này, README và RUNBOOK. Các thay đổi giao diện/học vụ có trước phiên này được giữ, không phải tất cả dòng trong `git diff` đều do đợt database này tạo.

## Cập nhật lớp học phần ngày 24/10/2026

Importer đã được cập nhật tiếp để đọc trực tiếp HUIT 10.202 dòng/41 cột, nhận diện header 7–8, phân trang và không ghi SQL ở preview. Xem [báo cáo nhập HUIT](HUIT_CLASS_SECTION_IMPORT.md) để có giới hạn/API/kết quả mới nhất; đợt này không thay schema hoặc seed.

Đợt tiếp theo đã thêm migration `010_class_section_import_and_numbering.sql`, cấp mã lớp theo năm/học kỳ/môn/nhóm, wizard Excel và cấu hình Mốc bắt đầu Tuần 1. Bản sửa mới nhất thêm `011_source_class_section_code.sql`, thống nhất trường nguồn `SourceClassSectionCode` và đưa **Lớp học phần thành tab trong Danh mục dữ liệu**, tại `/management?tab=class-sections`; URL `/class-sections` chuyển đúng vào tab này. Xem [báo cáo triển khai và kiểm thử mới](CLASS_SECTION_IMPORT_AND_NUMBERING.md) để biết schema, lệnh vận hành, file demo và kết quả hiện tại. Những số lượng và giới hạn kiểm thử ở phần trước phản ánh đợt triển khai trước; lớp cũ giữ nguyên mã.

## Giới hạn còn lại

Chưa có lịch chính thức cho các cấu hình đã liệt kê; DEMO không được coi là lịch HUIT chính thức. Pipeline AI live vẫn phụ thuộc capability/model thật, dữ liệu báo cáo DEMO không chứng minh inference hoạt động. Không có video/ảnh thật mới để thử upload/inference. Chưa chạy kiểm thử trực quan viewport bằng trình duyệt; frontend tests hiện là hành vi DOM doubles và build. Build Docker có thông báo npm audit 17 mục (6 moderate, 9 high, 2 critical); chưa nâng dependency vì nằm ngoài thay đổi database này. Chưa triển khai Production hoặc chứng nhận migration tự động cho mọi biến thể schema v1 tùy chỉnh.

Không xóa dữ liệu/volume của người dùng, không drop bảng hay truncate để seed. Các test SQL/HTTP dùng database mới `HTBAM_AcademicTests_<GUID>` cách ly và chỉ dọn database thử do chính bài kiểm tra tạo. Không reset/checkout/clean/stash/commit/push.

Đối chiếu cuối: không còn database `HTBAM_AcademicTests_*`, backend restart count 0, log container hiện tại không có `Invalid object name`, lỗi migration hoặc startup exception. Replay cuối giữ nguyên số lượng mọi bảng; audit tổng tại lần chụp cuối là 681 do hai lượt đăng nhập 13 tài khoản cộng một log đánh dấu fixture.
