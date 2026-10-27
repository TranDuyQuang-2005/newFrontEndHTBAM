# Mã giảng viên và header danh mục — 25/10/2026

Đã triển khai trực tiếp tại `F:\KLCN\HTBAM-Git`. Giữ nguyên các thay đổi có trước phiên làm việc; không reset, stash, clean, commit, push hoặc xóa volume/dữ liệu người dùng. Không tìm thấy `AGENTS.md` trong dự án và các thư mục cha được kiểm tra. Bản chụp đầu phiên, hash và kết quả kiểm thử nằm tại `.runtime-fix-backup-lecturer-codes/` (đã được Git bỏ qua).

## Nguyên nhân và quy tắc cấp mã

Trước đây, nhánh tạo giảng viên của `CatalogsController` gọi `InternalCode.New("GV")`, ghép `GV-` với GUID. Hai đường seed còn dùng các mã cố định `GV-DEAN`, `GV-HEAD`, `DEMO-GV-*`. Các nguồn cấp mã này không cùng một quy tắc nghiệp vụ.

Giảng viên tạo mới hiện dùng `GV-{Department.Code bỏ BM-}-{số thứ tự D4}`:

| Bộ môn | Mã đầu tiên | Mã tiếp theo |
| --- | --- | --- |
| BM-CNPM | GV-CNPM-0001 | GV-CNPM-0002 |
| BM-ATTT | GV-ATTT-0001 | GV-ATTT-0002 |
| BM-CNPM-02 | GV-CNPM-02-0001 | GV-CNPM-02-0002 |

Mã bộ môn phải hợp lệ theo `^BM-[A-Z0-9]+(?:-[0-9]{2})?$`, giới hạn 36 ký tự và loại trừ hậu tố GUID 32 ký tự. Bộ môn cũ chưa hợp lệ bị chặn với thông báo: “Bộ môn chưa có mã hợp lệ. Vui lòng chuẩn hóa mã bộ môn trước khi thêm giảng viên.” Không tự chuẩn hóa mã cũ trong tác vụ này.

Backend bỏ qua `TeacherCode` do client gửi; trường `Code` ngoài DTO cũng không được dùng để cấp mã. Response tạo mới trả về đối tượng với mã chính thức. Popup không có ô nhập mã, hiển thị “Tự động tạo khi lưu” và mã dự kiến sau khi chọn bộ môn. Preview không tiêu thụ số; có hủy request cũ và kiểm tra bộ môn trước khi cập nhật kết quả để tránh hiển thị mã của lựa chọn trước. Danh sách dùng ngay mã trong response và bỏ bộ lọc tìm kiếm cũ đang che bản ghi mới.

Các quyền hiện hữu được giữ nguyên: Trưởng khoa tạo trong phạm vi khoa được giao (`TEACHER_CREATE`); Admin bị chặn cả ở frontend và API, kể cả trường hợp frontend nhận quyền cũ. Giảng viên thường, Trưởng bộ môn và người ngoài phạm vi không được tạo. Bộ môn phải hoạt động và thuộc khoa hoạt động.

## Transaction, đồng thời và migration 013

`LecturerCodeSequences` lưu `DepartmentId` (PK/FK), `LastNumber` và `UpdatedAt`. API tạo mới, API xem mã dự kiến và cả hai đường development seed dùng cùng khóa `HTBAM:academic-scheduling`. SQL Server thực thi `sp_getapplock` độc quyền trong transaction `Serializable`; cấp số, lưu giảng viên và audit cùng transaction. Lỗi audit làm rollback cả giảng viên lẫn số đã cấp. Execution strategy xóa trạng thái tracking cũ trước mỗi lần thử lại.

Allocator đối chiếu bộ đếm với các mã cùng prefix có đúng bốn chữ số cuối, trong vùng đã có khóa. Việc đối chiếu bao gồm giảng viên đã chuyển bộ môn để không tái dùng mã lịch sử. Không lấy `MAX + 1` bên ngoài transaction/khóa. Hết 9.999 số thì báo lỗi thay vì đổi độ dài hoặc quay vòng. Khóa chung hiện tại ưu tiên tính nhất quán với các thao tác học vụ; các lần tạo mới được tuần tự hóa.

Migration mới: [`013_lecturer_business_codes.sql`](../database/sqlserver/013_lecturer_business_codes.sql), có bản mirror ở `database/`. Migration thêm bảng cấp số và bảo đảm unique index không lọc trên `Teachers.TeacherCode`; tận dụng index tương đương đã tồn tại. Script chạy lặp lại an toàn, không cập nhật mã, ID hay quan hệ cũ, không tạo lại bảng dữ liệu. `SqlMigrationRunner` đã đăng ký 013.

Đã đối chiếu hash **24 tệp SQL 001–012** ở cả hai cây với đầu phiên: **0 tệp bị thay đổi trong tác vụ này**. Một số migration cũ vốn đã khác Git HEAD trước phiên; các thay đổi đó được giữ nguyên. `node database/sync-sqlserver.cjs --check` đạt.

Development seed mới dùng allocator này; hồ sơ đã có được tìm theo `UserId` trước mã seed cũ. Chạy lại seed không đổi mã, không chuyển giảng viên về bộ môn cũ và không khôi phục chức vụ đã thu hồi. SQL seed lịch sử trong migration cũ được giữ nguyên; các hồ sơ mới do đường development seed hiện tại tạo dùng quy tắc mới.

## Giữ mã và dữ liệu lịch sử

Sửa tên/email, bổ nhiệm/thu hồi Trưởng bộ môn hoặc Trưởng khoa đều không cấp lại mã. Chuyển bộ môn chỉ sửa `DepartmentId`; quyền và cơ cấu hiện tại dựa trên quan hệ này. Popup sửa hiển thị: “Mã giảng viên đã cấp sẽ được giữ nguyên để bảo toàn dữ liệu lịch sử.” khi thay đổi bộ môn. Ràng buộc sẵn có vẫn chặn chuyển khi còn lớp học phần hoặc chức vụ hiệu lực; cần xử lý phân công trước.

Các mã dưới đây được xác nhận vẫn còn nguyên trong database đang chạy:

| Mã cũ | Trạng thái |
| --- | --- |
| GV001, GV002 | Giữ nguyên |
| GV-DEAN, GV-HEAD | Giữ nguyên |
| Test002 | Giữ nguyên |
| GV-382bc72e1995494faa64084d82fa02d1 | Giữ nguyên |
| GV-b2fe24d8a17f4dc69512137104945ee8 | Giữ nguyên |
| DEMO-GV-ADMIN, DEMO-GV-DEAN, DEMO-GV-EDITOR | Giữ nguyên |
| DEMO-GV-HEAD1 đến HEAD3 | Giữ nguyên |
| DEMO-GV-LECTURER1 đến LECTURER6, DEMO-GV-VIEWER | Giữ nguyên |

Danh sách gắn nhãn “Mã cũ” nếu không khớp `^GV-[A-Z0-9]+(?:-[0-9]{2})?-[0-9]{4}$`. Không chạy công cụ đổi mã hàng loạt; không thay tài khoản, lớp, buổi học hay quan hệ lịch sử để đạt quy tắc mới.

## Vị trí nút và component dùng chung

`CatalogSectionHeaderComponent` nhận `title`, `description`, nhận actions qua content projection; component cha kiểm tra quyền trước khi cung cấp action. Cả màn hình danh mục chung và phần Lớp học phần dùng component này. Cấu hình mô tả được tập trung trong `catalog-navigation.ts`.

Thứ tự giao diện: thông báo DEMO → tiêu đề/mô tả chung → tab → header của tab → bộ lọc → bảng. Tiêu đề chung không còn chứa nút. Bên trái header là tên và mô tả, bên phải là action tương ứng:

| Tab | Nút dưới thanh tab, khi có quyền |
| --- | --- |
| Khoa | + Thêm Khoa |
| Bộ môn | + Thêm Bộ môn |
| Lớp sinh hoạt | + Thêm Lớp sinh hoạt |
| Môn học | + Thêm Môn học |
| Lớp học phần | + Thêm Lớp học phần, sau đó Nhập Excel |
| Giảng viên | + Thêm Giảng viên |
| Phòng học | + Thêm Phòng học |
| Camera | + Thêm Camera |

Chỉ Lớp học phần có import thực tế nên chỉ tab này có “Nhập Excel”. Nút thêm dùng primary, import dùng secondary; cao 42px, cách nhau 10px. Vùng action rỗng được ẩn. Dưới 900px, actions xuống dưới mô tả; trên mobile, nút co giãn/xuống hàng trong viewport. Dropdown bộ môn bị giới hạn trong popup, cao tối đa 320px và không che footer. Focus, Tab và Escape dùng cơ chế overlay hiện có.

## Tạo thật qua UI và kiểm tra cơ cấu

Đã đăng nhập bằng Trưởng khoa và tạo **đúng hai hồ sơ** qua giao diện, một ở desktop và một ở mobile mô phỏng. Cùng bộ môn **BM-CNDL**, `DepartmentId = 40003`:

| ID | Tên | Mã backend cấp |
| --- | --- | --- |
| 50002 | Kiểm chứng mã giảng viên desktop | GV-CNDL-0001 |
| 50003 | Kiểm chứng mã giảng viên mobile | GV-CNDL-0002 |

Hai hồ sơ được giữ lại để đối chiếu theo yêu cầu, không liên kết tài khoản (`UserId = null`), có ghi chú kiểm chứng UI. Trang Giảng viên và Cơ cấu & phân quyền cùng hiển thị hai mã trên; cơ cấu hiển thị Bộ môn Công nghệ dữ liệu. Bộ đếm bộ môn sau kiểm tra là 2. Script lưu `browser/records.json` để các lượt chạy lại chỉ dùng hồ sơ đã tạo, không thêm bản sao.

Ảnh nằm trong `.runtime-fix-backup-lecturer-codes/browser/`. Có **30 ảnh kiểm chứng hợp lệ**: 22 ảnh header (8 tab của Trưởng khoa và 3 tab Admin, mỗi tab desktop/mobile), 4 ảnh popup/dropdown lúc tạo thật, 4 ảnh danh sách/cơ cấu sau lưu. `failure.png` là ảnh chẩn đoán từ lượt trước khi sửa finding, không nằm trong bộ ảnh đạt.

| Nội dung | Desktop | Mobile |
| --- | --- | --- |
| Header Giảng viên | [Ảnh](../.runtime-fix-backup-lecturer-codes/browser/desktop-dean-Giảng%20viên.png) | [Ảnh](../.runtime-fix-backup-lecturer-codes/browser/mobile-dean-Giảng%20viên.png) |
| Popup tạo | [Ảnh](../.runtime-fix-backup-lecturer-codes/browser/desktop-teacher-create.png) | [Ảnh](../.runtime-fix-backup-lecturer-codes/browser/mobile-teacher-create.png) |
| Dropdown bộ môn | [Ảnh](../.runtime-fix-backup-lecturer-codes/browser/desktop-teacher-dropdown.png) | [Ảnh](../.runtime-fix-backup-lecturer-codes/browser/mobile-teacher-dropdown.png) |
| Hai mã trong danh sách | [Ảnh](../.runtime-fix-backup-lecturer-codes/browser/desktop-teacher-list.png) | [Ảnh](../.runtime-fix-backup-lecturer-codes/browser/mobile-teacher-list.png) |
| Hai mã trong cơ cấu | [Ảnh](../.runtime-fix-backup-lecturer-codes/browser/desktop-organization.png) | [Ảnh](../.runtime-fix-backup-lecturer-codes/browser/mobile-organization.png) |
| Header Lớp học phần | [Ảnh](../.runtime-fix-backup-lecturer-codes/browser/desktop-dean-Lớp%20học%20phần.png) | [Ảnh](../.runtime-fix-backup-lecturer-codes/browser/mobile-dean-Lớp%20học%20phần.png) |
| Admin Phòng học | [Ảnh](../.runtime-fix-backup-lecturer-codes/browser/desktop-admin-Phòng%20học.png) | [Ảnh](../.runtime-fix-backup-lecturer-codes/browser/mobile-admin-Phòng%20học.png) |

Các ảnh còn lại đặt tên `{desktop|mobile}-{dean|admin}-{tên tab}.png` trong cùng thư mục. Đã mở ảnh để đọc và kiểm tra bố cục, dữ liệu đúng tab và mã thật. Desktop là 1440×1000, mobile Chromium mô phỏng 390×844. Chưa kiểm tra điện thoại vật lý/bàn phím ảo. Sáu ảnh gốc không có tệp ảnh truy cập được trong thư mục attachment của yêu cầu; kiểm tra trực quan dựa trên ứng dụng đang chạy và nội dung yêu cầu.

## Kết quả kiểm thử và tự review

| Kiểm tra | Kết quả thực tế |
| --- | --- |
| Backend đầy đủ, có SQL Server và file HUIT thật | **189 passed, 0 failed, 0 skipped**; TRX tại `test-results/backend.trx` |
| Backend build | Thành công, 0 warning/0 error |
| Frontend `node tests/run_frontend_checks.cjs` | **37 passed**, kiểm tra cú pháp 38 tệp TS, source HUIT và SQL mirrors đạt |
| Angular production build | Thành công; initial bundle **385.41 kB**, không warning |
| SQL concurrency | 8 request đồng thời cùng bộ môn cấp đủ GV-TTNT-0001…0008, không trùng; unique index và rollback được kiểm chứng |
| HTTP auth/học vụ trên database cô lập | **63 assertions** đạt |
| HTTP nhập lớp học phần | **35 assertions** đạt |
| Development seed/system API trên Compose | **47 checks** đạt |
| Browser lần tạo thật | **258 checks**, tạo hai hồ sơ qua UI, 30 ảnh |
| Browser sau sửa finding và chạy lại | **304 checks**, 0 page error; tái dùng hai hồ sơ, cập nhật 26 ảnh, giữ 4 ảnh lúc tạo |
| Docker Compose | Backend/web build thành công; backend, SQL, AI, MinIO healthy; web hoạt động; migrate/seed exit 0 |
| Migration/seed chạy lại | 013 đã áp dụng; số hàng mọi bảng không đổi sau lượt replay được kiểm tra |
| Tính toàn vẹn SQL/Git | 24 SQL cũ không thay đổi so với đầu phiên; mirrors và `git diff --check` đạt |

Backend tests mới bao phủ đánh số riêng từng bộ môn, client giả mã, cập nhật tên/email, bổ nhiệm và thu hồi hai loại chức vụ, chuyển bộ môn giữ mã, chặn bộ môn GUID/legacy, quyền 403, seed mới/replay, migration lặp, index unique, audit rollback và đồng thời. Kiểm thử HTTP dùng database cô lập; việc dọn database riêng của bộ kiểm thử không tác động database ứng dụng hay volume SQL hiện hữu.

Đã tự rà soát diff và sửa các finding hợp lệ:

1. Khi đổi query tab, bảng còn giữ dữ liệu của tab trước trong lúc chờ HTTP. Đã làm mới dữ liệu từ cache theo tab ngay trong callback chọn tab; thêm regression assertion.
2. Admin/Trưởng bộ môn tải Sinh viên dù không có `STUDENT_GLOBAL_VIEW`, làm hiện lỗi quyền không liên quan tại Phòng học/Camera. Chỉ gọi API khi có quyền; thêm test hành vi và browser assertion không có error box.
3. HTTP smoke còn khẳng định mã GUID/cố định trong seed cũ. Đã chuyển sang tìm ID/quan hệ, kiểm tra quy tắc mới và ánh xạ import rõ ràng để cùng chạy với dữ liệu lịch sử.
4. Đường seed cũ cần xóa tracking trước retry transaction. Đã đồng bộ với `BusinessTransaction` và chạy lại toàn bộ backend sau sửa.

Sau các sửa này đã chạy lại frontend, full backend, build Docker và browser. Không phát hiện finding còn lại trong phạm vi thay đổi.

## Tệp thay đổi trong tác vụ này

Backend:

- Mới: `services/backend/src/HTBAM.Domain/Entities/LecturerCodeSequence.cs`.
- Mới: `services/backend/src/HTBAM.Infrastructure/Data/LecturerCodeAllocator.cs`.
- Sửa: `services/backend/src/HTBAM.Infrastructure/Data/AppDbContext.cs`, `DevelopmentSeeder.cs`, `SqlMigrationRunner.cs`.
- Sửa: `services/backend/src/HTBAM.Api/Controllers/CatalogsController.cs`, `Support/DemoDataSeeder.cs`.
- Mới: `database/sqlserver/013_lecturer_business_codes.sql` và mirror `database/013_lecturer_business_codes.sql`.

Frontend:

- Mới: `apps/web/src/app/shared/catalog-section-header.component.ts`.
- Sửa: `apps/web/src/app/shared/catalog-navigation.ts`, `core/api.service.ts`, `features/management.component.ts`, `features/class-sections.component.ts`, `apps/web/src/styles.scss`.

Kiểm thử:

- Mới: `services/backend/tests/HTBAM.Tests/LecturerCodeTests.cs`, `tests/lecturer_headers_browser_smoke.cjs`.
- Sửa: backend `DevelopmentSeederTests.cs`, `AcademicSchedulingTests.cs`, `AcademicSqlServerTests.cs`, `ClassSectionImportSqlTests.cs`.
- Sửa: `tests/class_section_ui_check.js`, `tests/academic_api_smoke.js`, `tests/class_section_import_api_smoke.cjs`.

Tài liệu: báo cáo này, liên kết cập nhật trong `DATABASE_MIGRATION_AND_DEVELOPMENT_SEED.md` và `CATALOG_AUTOMATIC_CODES.md`. Các thay đổi khác trong `git status` đã có trước tác vụ và được giữ lại.

## Chạy lại

```powershell
node tests/run_frontend_checks.cjs
node database/sync-sqlserver.cjs --check
dotnet test services/backend/tests/HTBAM.Tests/HTBAM.Tests.csproj
cd apps/web
npm.cmd run build
```

Để chạy SQL tests, đặt `HTBAM_TEST_SQL_CONNECTION` qua môi trường cục bộ; để kiểm thử HUIT thật, đặt `HTBAM_HUIT_WORKBOOK` tới `.runtime-fix-backup-huit-import/huit-real.xls`. Không đưa connection string/token vào báo cáo hoặc Git.

Browser smoke dùng `HTBAM_PLAYWRIGHT_MODULE` trỏ tới Playwright có sẵn trên máy rồi chạy `node tests/lecturer_headers_browser_smoke.cjs` từ gốc dự án, với web `http://127.0.0.1:5173` và API `http://127.0.0.1:5000`. Lượt xác minh này dùng Playwright Core 1.63/Chromium có sẵn, không thêm dependency vào dự án. Giữ `browser/records.json` để chạy lại không tạo thêm dữ liệu. Kết quả máy đọc tại `browser/report.json`.
