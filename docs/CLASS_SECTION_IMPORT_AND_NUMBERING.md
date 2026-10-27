# Lớp học phần: nhập Excel, mốc T1 và cấp mã — 24/10/2026

**Cập nhật HUIT 25/10/2026:** tiêu đề 7–8/dữ liệu từ dòng 9 được tách khỏi giới hạn dò 20 dòng; chỉnh tay chỉ mở khi yêu cầu. Ánh xạ tuần dùng cả nhóm T1–T26 (L–AK), dropdown cột có tên/mẫu/tìm kiếm/chống dùng trùng và bảng xem nhanh 5 dòng. Importer hỗ trợ file thật 10.202 dòng/41 cột, wizard 6 bước, preview 100–200 dòng và preview không ghi SQL. Xem [báo cáo HUIT mới nhất](HUIT_CLASS_SECTION_IMPORT.md) cho API, kết quả kiểm thử và danh mục/cấu hình còn thiếu. Các phần import/kiểm thử bên dưới là lịch sử triển khai; số liệu mới nhất ở báo cáo HUIT.

Đã triển khai trực tiếp trên nhánh làm việc hiện có. Không commit/push/reset/stash/clean; không xóa volume hoặc dữ liệu HTBAM. Không tìm thấy `AGENTS.md` áp dụng trong workspace hoặc các thư mục cha đã kiểm tra. Đã đọc code và đối chiếu thay đổi chưa commit trước khi sửa. Các thay đổi sẵn có của người dùng được giữ lại; danh sách `git status` toàn dự án lớn hơn phạm vi đợt này.

## Vấn đề và kết quả

Trước thay đổi, lớp mới dùng `InternalCode.New("LHP")` chứa GUID; mã Excel được nhập trực tiếp trong form và chưa có import workbook. Dropdown mốc tuần nằm trên từng dòng mặc dù database thực tế không có nhóm mốc nào (`AcademicWeekCalendars` có 0 bản ghi). Bộ nhập tuần là chuỗi tự do; kiểm tra tuần mới chỉ giới hạn 1–53. Ngày dùng input date phụ thuộc locale trình duyệt. Menu lớp đứng riêng ngoài Danh mục. Runner kết nối master rồi mở database ngay, chưa chờ trạng thái ONLINE; lỗi CLI có thể thoát không được xử lý.

Hiện **Lớp học phần là tab trong chính trang Danh mục dữ liệu**. Thứ tự cho tài khoản đủ quyền: **Khoa → Bộ môn → Lớp sinh hoạt → Môn học → Lớp học phần → Giảng viên → Phòng học → Camera**. Sidebar chỉ còn **Danh mục dữ liệu**; đã bỏ mục lớp riêng bên dưới và nút lớp riêng ở góc phải tiêu đề. Tab nhúng lại component danh sách/form/wizard hiện có, không chuyển hay nhân đôi nghiệp vụ.

URL chuẩn là **`/management?tab=class-sections`**. URL cũ **`/class-sections`** được guard chuyển tới URL chuẩn, giữ query/fragment và kiểm tra quyền. Breadcrumb là **Trang chủ / Danh mục dữ liệu / Lớp học phần**. Thay đổi query/tab và Back/Forward cập nhật đúng nội dung active. Mặc định tài khoản quản lý mở tab Khoa; người chỉ có quyền xem lớp được chuyển vào tab lớp và không truy cập các tab danh mục khác. Tab lớp cần `CLASS_SECTION_VIEW`; nút tạo/import cần `CLASS_SECTION_CREATE`; quyền sửa từng lớp vẫn lấy từ backend. Tài khoản chỉ xem không có nút thay đổi dữ liệu.

Nút **Nhập Excel** nằm cạnh **Thêm lớp học phần** bên trong tab. Tab có `tablist/tab/tabpanel`, trạng thái `aria-selected`, phím Left/Right/Home/End; dãy tab cuộn ngang trên màn hình hẹp. Popup thêm/sửa/chi tiết/import giữ nguyên tab và breadcrumb. Backend tiếp tục kiểm tra chức vụ/phạm vi bộ môn, không chỉ dựa vào nút ẩn.

Form không còn ô nhập mã Excel hoặc dropdown mốc tuần. Mã tạo mới hiển thị “Tự động tạo khi lưu”. Chi tiết hiển thị mã nguồn ở chế độ đọc. Ngày trong form lớp và cấu hình dùng control văn bản `dd/MM/yyyy`, kiểm tra ngày thực và chuyển sang ISO khi gửi backend. Nút xem trước giải thích chính xác cấu hình còn thiếu. Dòng lịch giữ `LineKey`; phòng xác định nhóm giờ A/B và gợi ý LT/TH; backend vẫn giữ quy tắc LT/TH độc lập với nhóm giờ của phòng. Tóm tắt hiển thị giờ và tổng tiết. Chip tuần có giới hạn theo ngày; chế độ mặc định là học tất cả tuần, lưu `[]`.

## Migration và schema

Migration mới nhất của đợt sửa tab: **`011_source_class_section_code.sql`**, nguồn chuẩn trong `database/sqlserver`, mirror cùng tên trong `database/`. Migration 010 từ đợt trước được giữ nguyên. Hash của toàn bộ **20 tệp SQL `001`–`010`** ở cả hai thư mục không đổi so với snapshot trước đợt sửa tab; các dòng báo modified có từ trước vẫn được giữ nguyên.

Schema hiện tại sau 010 và 011:

- Migration **011 đổi tên metadata cột** `ClassSections.SourceCode` thành **`SourceClassSectionCode`** (`nvarchar(128)`), không thay giá trị hoặc tạo thêm cột song song. Entity/DTO/model/frontend thống nhất `SourceClassSectionCode` và JSON camelCase `sourceClassSectionCode`.
- Unique index mới **`UX_ClassSections_SourceClassSectionCode_Term`** trên `(AcademicTermId, SourceClassSectionCode)`, lọc NULL/chuỗi rỗng, thay index tên cũ trong cùng transaction migration. Phạm vi là học kỳ. Nếu cả tên cột cũ và mới cùng tồn tại, migration dừng để đối chiếu thay vì tự gộp hay ghi đè.
- Bảo đảm unique index một cột `ClassSections.Code`; nếu schema đã có index tương đương thì dùng lại.
- Bảng `ClassSectionCounters`: ID kỹ thuật, FK học kỳ và môn; unique `(AcademicTermId, CourseId)`; `LastNumber` trong 0–99.
- Bảng `ClassSectionImportPreviews`: token unique 64 ký tự, FK người dùng, thời hạn, snapshot JSON và kết quả JSON.
- Dùng lại `AcademicTerms.AcademicYear`, `Semester`, `WeekOneMonday`, `WeekMappingConfirmed`, `IsDevelopment`; không thêm bảng năm học/mốc trùng ý nghĩa.

Đã áp dụng 011 trên HTBAM và đối chiếu toàn bộ danh sách trước/sau: **mọi ID, mã lớp và giá trị mã nguồn giống hệt**. DTO vẫn nhận tên JSON `sourceCode` từ client cũ nhưng chỉ xuất tên chuẩn mới. Token import đã hoàn thành ở phiên trước được đọc tương thích và trả lại kết quả chuẩn mới, không tạo thêm dữ liệu. Preview chưa xác nhận có hash kế hoạch theo phiên cũ có thể cần xem trước lại; backend không âm thầm ghi kế hoạch khác. Không sửa JSON snapshot cũ trong database.

Từ `infra`, quy trình áp dụng/lặp lại:

```powershell
docker compose config --quiet
docker compose build backend web
docker compose stop backend
docker compose run --rm --no-deps migrate
docker compose run --rm --no-deps seed
docker compose up -d backend web
```

SQL Server phải đang chạy; `--no-deps` dùng để chạy lại sau khi dependency đã được khởi động. Tạm dừng backend cũ khi áp dụng 011 vì nó còn truy vấn tên cột cũ; khởi động lại bằng ảnh mới ngay sau migration/seed. Khi schema đã ở 011, có thể dùng `docker compose up -d` để Compose tự chạy chuỗi SQL healthy → migrate → seed → backend → web. Không cần xóa volume. Kiểm tra mirror bằng `node database/sync-sqlserver.cjs --check`.

## Mã lớp và bảo vệ cấp số

Mã mới: `LHP-{YY đầu}{YY sau}-HK{1|2}-{MÃ MÔN}-{01..99}`. Ví dụ `LHP-2627-HK1-HTBAM-01`. Năm phải có dạng `YYYY-YYYY`, hai năm liên tiếp. Mã môn nhận 1–40 ký tự `[A-Z0-9][A-Z0-9-]*`, không dấu/khoảng trắng. Môn mới cho phép nhập mã hợp lệ hoặc backend cấp mã `MH-` viết hoa. Mã môn/lớp cũ không tự được chuẩn hóa. Database hiện có mã môn **`test`** không phù hợp; tạo lớp mới cho môn đó bị chặn với thông báo chỉ rõ mã.

Cấp số, tạo lớp, import và seed dùng chung `BusinessTransaction`, isolation Serializable và `sp_getapplock` Exclusive trên `HTBAM:academic-scheduling`, chủ khóa là transaction. Bộ đếm tăng theo học kỳ/môn. Chỉ khi chưa có bộ đếm mới đối chiếu các mã chuẩn đang có để khởi tạo số cuối, cũng bên trong khóa. Không dùng MAX+1 không bảo vệ. Unique index là lớp bảo vệ cuối. Khóa chờ tối đa 15 giây; tranh chấp trả thông báo nghiệp vụ rõ, execution strategy SQL có retry hữu hạn. Hết nhóm 99 trả lỗi chủ đích.

Update không gọi bộ cấp số. Đổi giảng viên, phòng hoặc lịch giữ nguyên mã. Replay creation key không cấp số lại. Seed tìm cả mã DEMO cũ và creation key ổn định, chỉ cấp mã chuẩn cho bản ghi mới; không đổi mã seed đã tồn tại. Mã nguồn seed DEMO vẫn riêng trong `SourceClassSectionCode`.

## Nhập Excel

Wizard hiện gồm 6 bước: chọn file → sheet/nhận diện định dạng → ánh xạ cột → xem trước phân trang → xác nhận → kết quả. Cả `.xls` BIFF8, `.xlsx` và file HUIT thật đã được chạy thực tế. Parser dùng [ExcelDataReader](https://github.com/ExcelDataReader/ExcelDataReader) 3.8.0; ClosedXML có sẵn tiếp tục dùng để tạo mẫu và fixture kiểm thử.

Giới hạn mới: 10 MB upload, 64 MB sau giải nén, tối đa 30 sheet/**20.000 dòng/64 cột**, mỗi lớp tối đa 32 dòng lịch theo planner. Kiểm tra extension, MIME Excel/octet-stream, chữ ký ZIP/OLE và cấu trúc workbook; không nhận macro XLSX. Không dùng tên file làm đường dẫn và không thực thi công thức/macro. File không đọc được trả thông báo tiếng Việt, không trả stack trace.

Bảng phẳng có các cột sau; header được tìm trong 20 dòng đầu (không phân biệt hoa/thường, dấu và khoảng trắng). File mẫu vẫn để header dòng 1. Định dạng HUIT hai dòng header và đối chiếu tên môn không có Mã môn được mô tả riêng trong báo cáo HUIT:

| Bắt buộc | Ý nghĩa |
| --- | --- |
| Mã LHP | Mã nguồn, dùng gom lớp; không phải mã nội bộ |
| Mã môn / Mã GV / Phòng | Mã danh mục đang hoạt động, đúng bộ môn/phạm vi |
| Loại lịch | LT hoặc TH |
| Thứ | 2–7 hoặc CN |
| Tiết bắt đầu / Tiết kết thúc | Số nguyên, đúng bảng giờ và cùng ca |
| Từ ngày / Đến ngày | Ô ngày Excel, `dd/MM/yyyy` hoặc ISO |

Cột tùy chọn: `Tên lớp`, `Các tuần có lịch`, `Giai đoạn`, `Nhóm giờ` (cần khi loại phòng chưa xác định). Tuần nhận `T1, T3, T5` hoặc `1; 3; 5`, loại trùng/sắp tăng; trống nghĩa là tất cả tuần. Không nhận chuỗi sai hoặc tuần ngoài học kỳ/khoảng ngày.

Các dòng cùng mã nguồn phải thống nhất môn, giảng viên, tên lớp và giai đoạn. Xem trước trình bày nhóm, số dòng Excel, LT/TH, thứ, tiết, giờ, phòng, ngày, tuần, trạng thái hợp lệ/cảnh báo/lỗi/trùng. Dòng lịch trùng hệt trong sheet được bỏ qua. **Còn lỗi bắt buộc thì chặn xác nhận toàn file**, cần sửa ánh xạ/cấu hình và xem trước lại. Mọi nhóm được chấp nhận trong một lần xác nhận nằm chung transaction; lỗi xảy ra khi ghi nhóm sau rollback cả lớp, lịch, session, bộ đếm và kết quả của lần xác nhận.

Chống trùng có ba tầng: snapshot/token gắn người dùng và hạn 30 phút; khóa transaction cùng unique mã nguồn/học kỳ; so sánh metadata và nội dung lịch với lớp nguồn đã có. Gửi lại cùng token trả kết quả đã lưu. Import lại cùng nội dung bằng token/file mới phân loại trùng và không tạo thêm. Cùng mã nguồn nhưng dữ liệu khác bị lỗi, không tự ghi đè hay cập nhật lớp cũ. `UpdatedClasses` vì thế luôn 0. Thay đổi lớp đã nhập phải qua luồng sửa/xem ảnh hưởng. Token xác nhận chỉ nhận định danh, không nhận payload ghi từ frontend; planner kiểm tra lại quyền, cấu hình, xung đột và token kế hoạch trước khi ghi.

API:

- `POST /api/class-sections/import/sheets`: multipart `file`.
- `POST /api/class-sections/import/preview`: multipart `file`, `sheet`, `academicTermId`, `attendancePolicyId`, `phase` tùy chọn.
- `POST /api/class-sections/import/confirm`: `{ "token": "..." }`.
- API tạo/sửa thủ công không cho ghi/thay `SourceClassSectionCode`; giá trị sẵn có chỉ được giữ nguyên khi sửa.

## Mốc bắt đầu Tuần 1

Quản trị viên mở **Cấu hình học vụ → Mốc bắt đầu Tuần 1**, chọn năm học/học kỳ, nhập thứ Hai `dd/MM/yyyy`, căn cứ và xác nhận. Có API đọc theo năm/kỳ `GET /api/academic-configuration/week-one?academicYear=2026-2027&semester=1`, và ghi `PUT /api/academic-configuration/terms/{id}/week-one`. API ghi chỉ dành ADMIN theo chính sách cấu hình học vụ có sẵn; quyền quản lý lớp không tự cấp quyền thay cấu hình toàn trường.

T1 là tuần chứa ngày bắt đầu học kỳ; T2 cách T1 bảy ngày. Form lớp hiển thị mốc chỉ đọc, DEMO có nhãn **Mốc tuần mẫu**. Preview Excel có câu “T1 được ánh xạ tới ngày …” hoặc “Chưa cấu hình Mốc bắt đầu Tuần 1 cho học kỳ này”. Thiếu mốc đã xác nhận thì không sinh lịch theo số tuần; lịch tất cả tuần theo khoảng ngày không cần mốc T1. Không suy ra mốc từ từng dòng. Mốc đã dùng cho lịch chọn tuần không được đổi sang ngày khác nếu chưa có quy trình đối chiếu ảnh hưởng. Nhóm tuần cũ/API cũ được giữ tương thích, không hiển thị bộ tạo nhóm không cần thiết trong UI mới.

## Runner và kết quả kiểm thử thực tế

Runner chờ master nhận kết nối, kiểm tra database đích tồn tại/ONLINE và thử kết nối trực tiếp database đích. Retry gồm SQL 4060, các lỗi recovery/network liên quan và SQL 0 khi TCP chưa sẵn sàng ngay sau restart. Mặc định chờ 120 giây, cách 3 giây; có thể đặt `Database__ReadinessTimeoutSeconds` trong 1–600. Từng retry ghi lý do/số lần, không ghi connection string/mật khẩu. Migration timeout được CLI bắt và trả exit 2; lỗi migration khác exit 1.

| Nhóm kiểm tra | Kết quả cuối |
| --- | --- |
| Backend toàn bộ | **147 pass / 0 fail / 0 skip**, gồm file HUIT thật; chi tiết đợt import mới ở báo cáo HUIT |
| SQL Server thật trong tổng trên | **8 pass**, gồm migration nâng cấp/idempotence/rollback, seed, metadata FK/index, cấp số, import rollback, đích OFFLINE và 011 bảo toàn ID/mã/nguồn/index |
| Cấp mã đồng thời | 24 lớp SQL cùng năm/kỳ/môn, không trùng; có `LHP-2627-HK1-HTBAM-01`, `02` đến `24` |
| Frontend unit/hành vi | **24 pass / 0 fail**, gồm tab/Back/Forward/deep link/quyền, date control, tuần, wizard phân trang và overlay/search select |
| Kiểm tra nguồn frontend/mirror | Cú pháp 36 tệp, fixture thời khóa biểu, SQL mirror đạt |
| Angular production build | Đạt trên máy và trong Docker; initial bundle cuối 381,14 kB |
| Docker image backend/web | Build thành công; backend Release không cảnh báo/lỗi biên dịch |
| API cách ly | **43 HTTP assertions đạt**, seed Production/Development, tạo/sửa/replay lớp, phân công/quyền |
| API import Docker | **33 kiểm tra đạt**, XLS/XLSX, sheet, LT/TH, mốc T1, replay, import lại và quyền |
| API fixture Docker | **47 HTTP kiểm tra đạt**; roster/báo cáo/bộ lọc/phân quyền/13 tài khoản |
| Compose config/migrate/seed/startup | Config đạt, migrate/seed exit 0, backend healthy |
| Migration/seed replay | Số bản ghi của mọi bảng không đổi sau replay, trước và sau demo import |
| SQL restart rồi migrate ngay | Đạt trên ảnh mới: lần 1/2 retry `SQL 0`, lần 3 retry **`SQL 4060`**, lần 4 kết nối đích ONLINE → bỏ qua migration 001–011 đã áp dụng, exit 0 |
| Timeout SQL không sẵn sàng | Đạt: log retry SQL 10061, `MIGRATION_TIMEOUT_EXIT=2` |
| Đích OFFLINE/master nhận kết nối | SQL integration test xác nhận phải chờ đích ONLINE |
| Health/web/deep link | `/health`, `/api/health`, `/management?tab=class-sections` và `/class-sections` HTTP 200; API báo SQL/AI/object storage `ok`; guard chuyển tab được unit test, chưa kiểm tra render trình duyệt |
| Desktop/mobile trực quan | **Chưa chạy được**: runtime Computer Use báo native pipe unavailable, inventory không có browser |

Frontend unit tests dùng DOM/HTTP doubles và kiểm tra template; kết quả đó không thay thế kiểm thử render desktop/mobile. Không tuyên bố đã kiểm tra screenshot hoặc thao tác trình duyệt. Trong quá trình chạy smoke nhiều lần có gặp rate limit 429 (20 đăng nhập/60 giây); giữ nguyên bảo vệ này và chạy lại sau hết cửa sổ, không tắt rate limiter.

Lệnh tái kiểm tra từ gốc dự án:

```powershell
dotnet test services/backend/tests/HTBAM.Tests/HTBAM.Tests.csproj
# Đặt HTBAM_TEST_SQL_CONNECTION để chạy 8 integration tests SQL, không in biến này ra log.
npm.cmd --prefix apps/web test
npm.cmd --prefix apps/web run build
powershell -NoProfile -ExecutionPolicy Bypass -File tests/academic_api_smoke.ps1
node tests/class_section_import_api_smoke.cjs
node tests/development_seed_api_smoke.cjs
powershell -NoProfile -ExecutionPolicy Bypass -File tests/verify_development_seed.ps1
```

Các integration/HTTP test tạo database có tên `HTBAM_AcademicTests_<GUID>` riêng và chỉ dọn database thử do chúng tạo; không drop/reset HTBAM. Smoke import Docker dùng lớp DEMO có mã nguồn ổn định và bản xem trước/audit; chạy lại không tạo lớp/lịch mới. Báo cáo TRX của lượt cuối nằm trong `.runtime-fix-backup-catalog-tab/test-results/backend.trx`; đối chiếu lớp trước/sau là `classes-before.txt`/`classes-after.txt` cùng thư mục (không đưa vào git).

## Demo và dữ liệu còn thiếu

File [class-section-demo.xlsx](../tests/fixtures/class-section-demo.xlsx) và [class-section-demo.xls](../tests/fixtures/class-section-demo.xls) có hai sheet và cùng một mã LHP với hai dòng LT/TH. Dùng `demo.dean`, HK1 năm 2026–2027, chính sách đang hoạt động, sheet “Lịch”. Lớp mẫu đã nhập trên Docker là **`LHP-2627-HK1-DEMO-MH-01-01`**, mã nguồn **`DEMO-EXCEL-01`**, hai dòng lịch và sáu buổi. Dùng lại file sẽ báo hai dòng trùng. File `class-section-unit.xls/.xlsx` dành riêng cho unit test với mã danh mục fixture 2030.

Seed bổ sung môn **HTBAM**, 3 tín chỉ, bộ môn DEMO-HTTT, khi chưa tồn tại; không sửa môn HTBAM có sẵn và không tạo sẵn lớp cho môn này. Có thể chọn năm 2026–2027/HK1, môn HTBAM, giảng viên thuộc bộ môn và lịch không xung đột để thực hiện demo thủ công với nhóm 01/02. Kiểm thử tạo mã đúng `HTBAM-01`, `HTBAM-02` và các lớp đồng thời đã chạy trên database cách ly; không thêm 24 lớp kiểm thử vào HTBAM đang dùng. Các lớp seed mới dùng chuẩn mã, còn các lớp DEMO cũ giữ nguyên mã đã có.

Dữ liệu chính thức chưa có: HK1 2025–2026 thiếu ngày bắt đầu/kết thúc; HK2 đã có 12/01/2026 nhưng thiếu ngày kết thúc; cả hai chưa có mốc T1 xác nhận và ranh giới giai đoạn chính thức. Các ngày/mốc 2026–2027 là DEMO. Bảng giờ hiện có hiệu lực từ 12/01/2026, không có bảng chính thức trước ngày đó; không tự áp ngược. Mẫu workbook kèm theo là mẫu tương thích triển khai này, chưa phải mẫu Excel chính thức được đơn vị xác nhận.

Giới hạn còn lại: chưa kiểm tra render/viewport trình duyệt; import chỉ tạo/bỏ qua, không cập nhật lớp cũ; giới hạn 99 nhóm/tổ hợp; chưa có quy trình thay đổi mốc T1 đã dùng kèm đối chiếu ảnh hưởng; preview hết hạn được giữ trong database, chưa có tác vụ dọn định kỳ. Dữ liệu AI DEMO không chứng minh pipeline inference thực tế.

## Tệp sửa/thêm riêng trong đợt chuyển thành tab và chuẩn hóa tên nguồn

- Frontend: `features/management.component.ts`, `class-sections.component.ts`, `class-section-import.component.ts`; `core/auth.guard.ts`, `core/models.ts`; `app.routes.ts`, `layout/shell.component.ts`, `styles.scss`; thêm **`shared/catalog-navigation.ts`**. Không đổi dependency frontend.
- Migration: thêm **`database/sqlserver/011_source_class_section_code.sql`** và mirror root; cập nhật danh sách migration trong `SqlMigrationRunner.cs`.
- Backend: `ClassSectionsController.cs`, `ClassSchedulePlanner.cs`, `ClassSectionExcelImport.cs`, `DemoDataSeeder.cs`; `AcademicContracts.cs`, `ClassSectionImportContracts.cs`, `Entities.cs`, `AppDbContext.Academic.cs`, `AppDbContext.SqlColumnSizes.cs` để thống nhất `SourceClassSectionCode`.
- Tests: `ClassSectionImportTests.cs`, `ClassSectionImportSqlTests.cs`, `AcademicSqlServerTests.cs`; `class_section_ui_check.js`, `academic_api_smoke.js`, `class_section_import_api_smoke.cjs`, `development_seed_api_smoke.cjs`.
- Tài liệu: báo cáo này và cập nhật liên kết/trạng thái trong `DATABASE_MIGRATION_AND_DEVELOPMENT_SEED.md`. Dockerfile, Compose, migration 001–010 không sửa.

## Tệp của đợt triển khai nền trước đó

- `.gitignore`: bỏ qua kết quả test .NET.
- Frontend: `apps/web/src/app/core/api.service.ts`; `features/management.component.ts`, `academic-configuration.component.ts`, `class-sections.component.ts`, **`class-section-import.component.ts`**; **`shared/date-input.component.ts`**; `layout/shell.component.ts`; `apps/web/src/styles.scss`.
- SQL: **`database/sqlserver/010_class_section_import_and_numbering.sql`** và mirror **`database/010_class_section_import_and_numbering.sql`**.
- API: `Controllers/CatalogsController.cs`, `AcademicConfigurationController.cs`, `ClassSectionsController.cs`, **`ClassSectionImportsController.cs`**; `HTBAM.Api.csproj`, `Program.cs`; `Support/ApiExceptionMiddleware.cs`, `ClassSchedulePlanner.cs`, `DemoDataSeeder.cs`, **`ClassSectionNumbering.cs`**, **`ClassSectionExcelImport.cs`**.
- Application: `DTOs/AcademicContracts.cs`, **`DTOs/ClassSectionImportContracts.cs`**, `Services/AcademicRules.cs`, **`Services/ClassSectionCodes.cs`**.
- Domain/Infrastructure: `HTBAM.Domain/Entities/AcademicScheduling.cs`; `HTBAM.Infrastructure/Data/AppDbContext.Academic.cs`, `SqlMigrationRunner.cs`.
- Backend tests: `AcademicSchedulingTests.cs`, `AcademicSqlServerTests.cs`, `FeatureRegressionTests.cs`, **`ClassSectionImportTests.cs`**, **`ClassSectionImportSqlTests.cs`**.
- Frontend/API checks: `tests/run_frontend_checks.cjs`, `academic_api_smoke.js`, `development_seed_api_smoke.cjs`, **`class_section_ui_check.js`**, **`class_section_import_api_smoke.cjs`**, **`generate_class_section_fixtures.cjs`** và bốn fixture Excel mới.
- Tài liệu: báo cáo này và liên kết cập nhật trong `DATABASE_MIGRATION_AND_DEVELOPMENT_SEED.md`.

Các tên tệp API/Application ở danh sách trên đều nằm dưới `services/backend/src`; backend tests nằm dưới `services/backend/tests/HTBAM.Tests`. Tệp in đậm là mới trong đợt này, kể cả một số tệp còn lại vốn đã untracked trước khi bắt đầu. Dockerfile/Compose và migration 001–009 không bị sửa trong đợt này.
