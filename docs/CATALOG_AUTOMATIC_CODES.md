# Mã Bộ môn và Môn học tự động — 25/10/2026

**Cập nhật tiếp theo:** mã giảng viên `GV-{mã bộ môn bỏ BM-}-{D4}`, migration 013 và header dùng chung dưới thanh tab đã được triển khai. Xem [báo cáo mã giảng viên và header danh mục](LECTURER_CODES_AND_CATALOG_HEADERS.md) cho kiểm thử, mã thật và ảnh desktop/mobile. Nội dung BM/MH bên dưới vẫn áp dụng.

Đã sửa trực tiếp trên `F:\KLCN\HTBAM-Git`. Đã kiểm tra git status, không tìm thấy `AGENTS.md` áp dụng trong workspace/thư mục cha. Giữ các thay đổi có sẵn, kể cả phần khởi tạo khoa và các regression test được thêm trước đợt này. Snapshot đối chiếu ở `.runtime-fix-backup-catalog-codes` (gitignore). Không commit/push/reset/stash/clean, không xóa volume hoặc dữ liệu HTBAM.

## Nguyên nhân

- `AddDepartment` và bộ môn đầu tiên khi khởi tạo khoa dùng `InternalCode.New("BM")`, tạo mã chứa GUID.
- `AddCourse` vẫn nhận `Code` client gửi; nếu trống thì dùng `InternalCode.New("MH")`. Chưa có số thứ tự theo Bộ môn.
- Popup thêm môn còn ô nhập mã và gửi nguyên form. Form chung đặt mã/tên cạnh nhau, nên helper và chiều cao nhãn làm bố cục lệch; dropdown dùng chiều rộng tối thiểu 260px có thể vượt trường hẹp.
- Sau lưu chỉ gọi lại danh sách nhưng giữ từ khóa tìm kiếm cũ, nên môn vừa tạo có thể đã lưu nhưng không thấy trên bảng.

## Quy tắc và transaction

**Bộ môn:** bỏ dấu tiếng Việt (gồm Đ/đ), tách từ chữ/số, bỏ từ nối `và`, `của`, `về`, `cho`, `các`, lấy chữ cái đầu, viết hoa. Ví dụ Công nghệ dữ liệu → `BM-CNDL`. Trùng mã toàn hệ thống dùng `BM-CNDL-02`, `-03` … `-99`. Tên rỗng/không tạo được viết tắt hoặc quá dài bị từ chối. Không dùng mã client, GUID hay timestamp làm mã hiển thị. Đổi tên Bộ môn giữ mã cũ.

**Môn học:** `MH-` + phần sau `BM-` của mã Bộ môn + `-001` … `-999`. Bộ môn `BM-CNDL-02` tạo `MH-CNDL-02-001`. Số tăng riêng theo ID Bộ môn; đổi tên/tín chỉ giữ mã. Chế độ sửa khóa Bộ môn; API trả 409 nếu cố chuyển Bộ môn. Môn đã có lớp học phần vẫn không được thay tín chỉ làm đổi ngữ cảnh lịch sử.

Mã Bộ môn phải khớp `^BM-[A-Z0-9]+(?:-[0-9]{2})?$`; phần GUID 32 ký tự hex bị loại cả khi đã viết hoa. Giới hạn độ dài bảo đảm mã môn vẫn nằm trong 40 ký tự mà nghiệp vụ lớp học phần đang hỗ trợ. Mã cũ không dùng để cấp môn mới; API báo đúng:

> Bộ môn chưa có mã hợp lệ. Vui lòng chuẩn hóa mã bộ môn trước khi tạo môn học.

Các đường tạo Bộ môn/Môn học, khởi tạo bộ môn đầu tiên và development seed đều dùng chung transaction Serializable + `sp_getapplock`, resource `HTBAM:academic-scheduling`. Quyền/phạm vi được kiểm tra sau khi lấy khóa. Bảng `CourseCodeCounters` lưu số cuối theo Bộ môn. Việc dò mã hiện có để khởi tạo/đối chiếu số chỉ diễn ra trong transaction có khóa; không dùng MAX+1 bên ngoài khóa. Cấp số, bản ghi và audit tạo mới cùng commit/rollback. Unique index mã danh mục là lớp bảo vệ cuối cùng. Hết hậu tố cho phép thì trả lỗi thay vì đổi sang GUID hoặc tăng số chữ số.

API xem mã dự kiến chỉ đọc, không tăng số/không tạo counter. Request lưu cấp lại mã chính thức từ backend; mã client gửi luôn bị bỏ qua. Mã dự kiến không phải đặt chỗ.

## Migration 012 và seed

Thêm `database/sqlserver/012_catalog_business_codes.sql`, mirror ở `database/012_catalog_business_codes.sql`; đăng ký trong `SqlMigrationRunner.Files`.

- Thêm `CourseCodeCounters`, khóa chính/FK `DepartmentId`, check số 0–999.
- Thêm `CatalogSeedIdentities`, khóa seed ổn định và FK tới Bộ môn hoặc Môn học. Nhờ đó đổi tên một bản ghi seed không làm lần seed sau tạo bản sao.
- Kiểm tra unique index/constraint không lọc trên `Departments.Code`, `Courses.Code`; chỉ tạo khi chưa có. Không tạo index trùng khi schema hiện có đã bảo vệ mã.
- Không `UPDATE`, đổi mã/ID, xóa bản ghi hoặc sửa các migration đã áp dụng. Đối chiếu hash xác nhận **22 tệp SQL cũ của 001–011 không đổi** so với đầu đợt.
- Seed trên database mới dùng mã BM/MH đúng quy tắc. Trên database cũ, nhận diện mã `DEMO-*`, `HTBAM` có sẵn và gắn khóa seed ổn định, giữ nguyên ID/mã; không tạo bản sao chỉ vì thay quy tắc cấp mã. Seed lặp lại giữ nguyên số bản ghi mọi bảng.

**Các mã Bộ môn cũ còn trên HTBAM:** `ATTT`, `bm0001`, `BM-8f6c523117f842b4a5e40eff155feafc`, `CNPM`, `HTTT`, `DEMO-CNPM`, `DEMO-EMPTY`, `DEMO-HTTT`, `DEMO-KHMT`. Chúng được giữ để không đổi danh mục và quan hệ lịch sử. Migration này không chạy chuẩn hóa mã cũ; việc đó cần quy trình riêng có xem trước/xác nhận. Các môn cũ như `HTBAM`, `DEMO-MH-*` cũng không bị đổi mã.

## Popup và danh sách

Popup Môn học: tên một dòng toàn chiều rộng; Bộ môn/Tín chỉ theo tỷ lệ 2/3–1/3; mã tự động dạng information box toàn chiều rộng; ghi chú; Hoạt động; footer. Trường nhập cao 46px, grid căn đầu hàng. Trên mobile chuyển một cột. Bộ môn khi sửa bị khóa và có hướng dẫn giữ nguyên mã.

Dropdown Bộ môn tìm mã/tên qua API, mỗi đợt tối đa 10 gợi ý và có Tải thêm. Nhãn `BM-CNDL — Công nghệ dữ liệu`; mã không chuẩn có badge **Mã cũ**. Chiều rộng dropdown bằng trường Bộ môn, tối đa cao 320px, tôn trọng header/footer và focus trap. Giao diện chặn Lưu khi Bộ môn cũ không hợp lệ, đang lấy mã dự kiến hoặc có lỗi. Không gửi `code` trong payload Bộ môn/Môn học.

Sau tạo: dùng ngay ID/mã response để cập nhật bảng, xóa bộ lọc che bản ghi mới, đóng popup và tải lại đúng dữ liệu. Hủy request danh sách cũ để response chậm không ghi đè danh sách mới. Không yêu cầu reload trang.

## API

| API | Thay đổi |
| --- | --- |
| `POST /api/catalogs/departments` | Code client không bắt buộc/bị bỏ qua, backend chuẩn hóa tên/cấp hậu tố và trả mã chính thức |
| `POST /api/catalogs/courses` | Code client bị bỏ qua; kiểm tra DepartmentId/quyền/Bộ môn hợp lệ; cấp số trong transaction |
| `GET /api/catalogs/courses/next-code?departmentId=...` | Mới; mã dự kiến có kiểm tra quyền/phạm vi, không ghi database |
| `PUT /api/catalogs/courses/{id}` | Giữ mã; từ chối chuyển Bộ môn; giữ bảo vệ tín chỉ khi đã có lớp |
| `POST /api/catalogs/faculties` | Bộ môn đầu tiên cũng dùng quy tắc mới; giữ nguyên luồng khởi tạo khoa đã có |

## Kết quả thực tế

| Kiểm tra | Kết quả |
| --- | --- |
| Backend toàn bộ | **182 pass, 0 fail, 0 skip**, bật SQL thật và file HUIT thật |
| Mã viết tắt | CNDL, CNPM, ATTT, HTTT, KHDL và từ nối đạt |
| SQL đồng thời | Hai Bộ môn cùng tên nhận hai mã khác; **16 request Môn học đồng thời** nhận mã 001–016 không trùng |
| Chống giả mã/sửa mã | Code client không ghi đè; sửa tên/tín chỉ giữ mã; chuyển Bộ môn bị từ chối |
| Legacy và unique | 5 dạng mã Bộ môn cũ bị chặn tạo môn; unique index chặn mã trùng cả hai bảng |
| Rollback | Audit tạo môn lỗi → không có môn mới, số cuối vẫn 16 |
| Seed/migration | Seed mới đúng mã, seed giữ tên người dùng sửa, replay 012 và replay migrate/seed Compose đều đạt; **mọi số bản ghi trước/sau replay bằng nhau** |
| Frontend | **32 pass**, thêm kiểm tra preview theo Bộ môn, hủy response cũ, chặn legacy, không gửi mã và hiện ngay mã backend |
| Angular production | Đạt trên máy và Docker; initial **383,75 kB** |
| Browser | **54 kiểm tra đạt**, Chromium headless desktop 1366×900 và mobile giả lập 390×844; **10 ảnh** đã mở và đọc |
| Docker/API | Build/up thành công, migration/seed exit 0; **47 HTTP checks** hệ thống, web assets và Nginx proxy đạt |
| Diff/SQL mirror | Self-review toàn bộ thay đổi của đợt + tệp mới; `git diff --check`, mirror check đạt |

Đã tạo dữ liệu thật qua UI theo yêu cầu, giữ lại trong HTBAM để đối chiếu:

| Thiết bị giả lập | Loại | ID | Mã backend |
| --- | --- | --- | --- |
| Desktop | Bộ môn Công nghệ dữ liệu | 40003 | `BM-CNDL` |
| Desktop | Môn kiểm chứng 1 | 50003 | `MH-CNDL-001` |
| Desktop | Môn kiểm chứng 2 | 50004 | `MH-CNDL-002` |
| Mobile | Bộ môn Công nghệ dữ liệu | 40004 | `BM-CNDL-02` |
| Mobile | Môn kiểm chứng | 50005 | `MH-CNDL-02-001` |

Counter thực tế sau QA: Bộ môn 40003 = 2, Bộ môn 40004 = 1. Tên môn có nhãn kiểm chứng desktop/mobile và ghi chú ngày 25/10/2026. Không xóa/dọn những bản ghi này. Ảnh: [popup desktop](../.runtime-fix-backup-catalog-codes/browser/desktop-course.png), [dropdown desktop](../.runtime-fix-backup-catalog-codes/browser/desktop-dropdown.png), [chặn mã cũ](../.runtime-fix-backup-catalog-codes/browser/desktop-legacy.png), [popup mobile](../.runtime-fix-backup-catalog-codes/browser/mobile-course.png), [thêm Bộ môn mobile](../.runtime-fix-backup-catalog-codes/browser/mobile-department.png). TRX ở `.runtime-fix-backup-catalog-codes/test-results/backend.trx`, mã/ID/54 checks ở `browser/report.json`.

Self-review sửa điều kiện hậu tố hai chữ số để khớp regex yêu cầu, rồi chạy lại backend/frontend/build. Fixture regression cũ được đổi sang Bộ môn hợp lệ khi test tạo môn; SQL test lớp học phần lấy mã seed thực tế thay vì hardcode mã cũ. Không chỉnh chức năng khởi tạo khoa, phân quyền hay lịch học ngoài điểm cấp mã Bộ môn đầu tiên.

Chưa chạy trên điện thoại vật lý hoặc bàn phím ảo thật. Kiểm thử trình duyệt dùng Chromium/Playwright đã cài, không thêm dependency vào dự án. Test SQL tạo database riêng `HTBAM_AcademicTests_<GUID>` và chỉ dọn database do chính test tạo.

## Tệp sửa/thêm

- Backend sửa: `Controllers/CatalogsController.cs`, `Support/DemoDataSeeder.cs`, `HTBAM.Infrastructure/Data/AppDbContext.cs`, `SqlMigrationRunner.cs`.
- Backend thêm: `Support/CatalogNumbering.cs`, `HTBAM.Application/Services/CatalogCodes.cs`, `HTBAM.Domain/Entities/CourseCodeCounter.cs` (gồm entity counter và seed identity).
- SQL thêm: `012_catalog_business_codes.sql` trong nguồn/mirror.
- Frontend sửa: `features/management.component.ts`, `shared/search-select.component.ts`, `core/api.service.ts`, `styles.scss`.
- Tests sửa: `FeatureRegressionTests.cs`, `ClassSectionImportSqlTests.cs`, `tests/class_section_ui_check.js`.
- Tests thêm: `CatalogCodeTests.cs`, `tests/catalog_codes_browser_smoke.cjs`.
- Docs: báo cáo này và phần dẫn cập nhật trong `DATABASE_MIGRATION_AND_DEVELOPMENT_SEED.md`.

Các đường dẫn backend tính từ `services/backend/src`; tests C# từ `services/backend/tests/HTBAM.Tests`; frontend từ `apps/web/src/app` trừ stylesheet ở `apps/web/src`.

Lệnh tái kiểm tra: `dotnet test services/backend/tests/HTBAM.Tests/HTBAM.Tests.csproj` (đặt `HTBAM_TEST_SQL_CONNECTION`, `HTBAM_HUIT_WORKBOOK` để không skip); `npm.cmd --prefix apps/web test`; `npm.cmd --prefix apps/web run build`; `node tests/development_seed_api_smoke.cjs`; `powershell -NoProfile -ExecutionPolicy Bypass -File tests/verify_development_seed.ps1`. Browser script cần `HTBAM_PLAYWRIGHT_MODULE` tới module đã cài; **chạy lại script sẽ tạo thêm bản ghi kiểm chứng**, không tự xóa bản ghi đã tạo.
