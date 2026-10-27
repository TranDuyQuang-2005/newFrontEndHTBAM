# Triển khai lịch học và phân quyền — 24/10/2026

Đã tích hợp có chọn lọc frontend Angular, API .NET, model EF và migration SQL Server từ nhánh tham chiếu `hwng` vào nhánh làm việc `codex/academic-integration-hwng`. Không commit/push/reset. Các thay đổi camera trình duyệt, nhận diện và AI realtime của bản đích được giữ.

Phần mềm đã có luồng cấu hình và sinh lịch, nhưng **chưa đủ căn cứ để sinh lịch chính thức HK1/HK2 2025–2026**. Các cấu hình chưa xác nhận tiếp tục chặn việc lưu lịch phụ thuộc chúng. Migration 001–013 đã được kiểm thử trên SQL Server cách ly nhưng chưa áp dụng, và bản build mới chưa được triển khai vào hệ thống Docker đang chạy.

## Yêu cầu → phần triển khai

| Yêu cầu | Frontend | API/domain | Database |
| --- | --- | --- | --- |
| Mã nội bộ tự sinh, không đổi khi sửa | Form mã chỉ đọc, thông báo tạo khi lưu | `InternalCode`, CRUD danh mục/sinh viên/chính sách | Mã GUID 128 bit có tiền tố, unique indexes; giữ `SourceCode`/`SourceAnonymousCode` và MSSV |
| Lớp học phần riêng | Trang riêng, liên kết từ Danh mục/Buổi học, popup đầy đủ; Enter mở hàng, thao tác trong hàng không mở kèm | `ClassSectionsController` | Tái sử dụng `ClassSections`/`Enrollments` |
| Học kỳ 1/2 và giai đoạn | Cấu hình học vụ, ngày chỉ đọc ở form lớp; đổi môn/kỳ xóa lựa chọn không hợp lệ | `AcademicRules`, API range | `AcademicTerms`, `TermPhases` |
| Giờ A/B có phiên bản | Chọn tiết và hiển thị giờ, nhóm theo loại phòng; phòng chưa chuẩn hóa cần chọn nhóm | Chọn phiên bản theo từng ngày phát sinh; chặn qua ca | `BellTimeVersions`, `BellPeriods` |
| Nhiều lịch, tuần, đổi phòng, ngày nghỉ | Các dòng lịch độc lập, tuần chọn và lịch tuần theo nhóm | Mở rộng ngày thực tế, kiểm tra trùng lớp/phòng/giảng viên, múi giờ Việt Nam | `ClassSchedules`, `AcademicWeekCalendars`, `ScheduleExceptions` |
| Sinh buổi khi lưu lớp | Xem trước số buổi tạo/thay thế/giữ lại | Transaction, khóa nghiệp vụ, revision, khóa chống gửi trùng; vẫn giữ API tạo buổi thủ công hiện có | Unique occurrence; tham chiếu dòng lịch, ngày, tiết, phiên bản giờ |
| Sửa lịch giữ lịch sử | Xác nhận bản xem trước mới nhất | Chỉ thay buổi tương lai sạch; giữ buổi đã chạy/có dữ liệu, không hồi sinh buổi hủy tay | Lưu phiên bản dòng lịch; buổi thay thế được đánh dấu và hủy, không xóa |
| Danh sách buổi theo bộ môn | Dropdown bộ môn thay ô tìm kiếm, reset trang khi đổi | Scope trước khi lọc và phân trang ở server | Truy vấn lớp → môn → bộ môn |
| Bổ nhiệm/thu hồi quản lý | Trang Cơ cấu; chuyển giao có xác nhận; sửa hồ sơ trưởng khoa | Dùng giảng viên hiện có, kiểm tra lại trong transaction; Admin không tạo giảng viên | Tái sử dụng `ManagementAssignments`, unique active slot và audit |
| Nhãn chức vụ đúng | Sidebar/hồ sơ/danh sách dùng nhãn server; tải lại sau thao tác, khi focus và định kỳ | `PositionLabels`, `AuthProfileSupport`, nhiệm kỳ còn hiệu lực | Không suy đoán chức vụ từ tên hoặc thêm role song song |
| Bộ môn còn trống | Dropdown chỉ bộ môn trống, làm mới sau bổ nhiệm/thu hồi | Tái kiểm tra vị trí lúc lưu, xử lý hai yêu cầu đồng thời | Khóa transaction và ràng buộc vị trí hiện có |
| Quyền từng lớp | Hộp cấp quyền, roster và ghi chú theo lớp | Kiểm tra từng lớp/giảng viên/thao tác mỗi request; thu hồi có hiệu lực với JWT cũ | `ClassSectionGrants`, `Enrollments.ClassNote`, audit |
| Dropdown CRUD dùng chung | `SearchSelect`: 10 gợi ý, tải thêm, bàn phím, Escape/click ngoài, portal | `LookupsController`: scope, tìm toàn bộ, mã/tên, không dấu, phân trang | Dùng dữ liệu danh mục hiện có, không chỉ trang đầu |

Môn 3 tín chỉ dùng nguyên kỳ và không có giai đoạn. Môn 2 tín chỉ có hai giai đoạn; môn 1 tín chỉ có ba giai đoạn, tính độc lập. Chính sách `EXPLICIT` cần đủ các khoảng liên tiếp đã xác nhận; `EQUAL_DAYS` chỉ được dùng khi quản trị viên chủ động xác nhận chính sách và căn cứ. Tín chỉ khác 1–3 bị báo chưa có quy tắc. Không suy số tiết bắt buộc từ tín chỉ.

Phòng `THEORY`/`ZOOM` dùng A kể cả lịch TH; `COMPUTER`/`PRACTICE`/`LAB`/`DEFENSE`/`PE` dùng B. `UNKNOWN` cần chọn rõ nhóm. Phòng ngừng sử dụng vẫn có trong danh mục nhưng không chọn cho lịch mới. Giờ lưu UTC; ngày nguồn và giờ hiển thị theo Việt Nam. Khoảng nghỉ được lấy từ từng tiết, không cộng liên tục 50 phút.

Buổi tự sinh ban đầu là `DRAFT`, có thể chưa có camera/video. Giảng viên chọn nguồn hợp lệ tại Buổi học trước khi chuyển sang `READY`; không sửa ngày/phòng qua API nguồn. Các API khởi chạy/điểm danh hiện có vẫn được dùng, không giả lập kết quả AI.

## Kết quả đọc tệp Excel gốc

Nguồn đọc chỉ để đối chiếu: `C:\Users\ADMIN\Downloads\thoi-khoa-bieu-du-kien-hk1-nam-hoc-2025-2026-he-dhcq-20250530040845-e.xls`. Đọc bản sao bằng chế độ chia sẻ để không thay tệp đang mở trong Excel. Không nhập toàn bộ vào database nghiệp vụ, không thêm thư viện đọc Excel vào dependency ứng dụng.

- SHA-256: `c877ff03f12fa57c034fa0bdd6d6b85706d2f7db844bd068ff5451d85ce79c8c`.
- Có 10.190 dòng lịch hợp lệ, 5.447 mã LHP phân biệt; mã dài 12 hoặc 13 ký tự được giữ dạng chuỗi, kể cả số 0 đầu.
- Cả 10.190 dòng có ngày bắt đầu/kết thúc khớp quy ước thứ 2–8. Riêng 801 dòng có thứ 8 đều rơi vào Chủ nhật, sau chuẩn hóa là ISO 7.
- Cả 10.190 dòng có `Tổng số tiết = số tuần đánh dấu × số tiết của dòng`. `Số tiết` danh nghĩa được giữ riêng; không cộng lặp ở các đoạn đổi phòng.

| Mã nguồn | Các dòng cần giữ chung một lớp |
| --- | --- |
| `010110215301` | LT thứ 4, tiết 4–6, Zoom48, 20/08–22/10/2025; TH thứ 6, tiết 1–3, B308, 22/08–24/10/2025 |
| `010100000501` | Thứ 5 tiết 4–6: B407 ở tuần 1–2, 21/08–28/08; Zoom39 ở tuần 3–10, 04/09–23/10. Tổng tương ứng 6 và 24, mỗi dòng vẫn ghi số tiết danh nghĩa 30 |

Đối chiếu ngày đầu tiên được đánh dấu cho thấy **ba mốc T1 có thể suy ra**: 18/08/2025 (6.203 dòng), 01/09/2025 (3.811 dòng), 14/07/2025 (176 dòng). Đây là kết quả phân tích cấu trúc, **không phải xác nhận lịch tuần chính thức**. Không seed các mốc này. Model hỗ trợ nhiều lịch tuần có tên trong cùng học kỳ và chọn theo dòng; không ép toàn bộ Excel vào một mốc T1.

Fixture nhỏ gồm các ví dụ trên và một mã 13 ký tự được lưu tại [`tests/fixtures/huit-timetable-reference.json`](../tests/fixtures/huit-timetable-reference.json). Tệp toàn bộ và dữ liệu phân tích tạm nằm trong thư mục scratch bị Git bỏ qua.

## Bảng quyền cuối cùng

Quyền quản lý lấy từ nhiệm kỳ còn hiệu lực, gắn với giảng viên đang hoạt động; scope kiểm tra ở server. Các chức năng AI/kỹ thuật ngoài thay đổi này giữ cơ chế quyền hiện có.

| Thao tác | Admin | Trưởng khoa | Trưởng bộ môn | Giảng viên được phân công |
| --- | --- | --- | --- | --- |
| Bổ nhiệm/chuyển giao/thu hồi trưởng khoa; sửa hồ sơ trưởng khoa | Có, dùng hồ sơ hiện có | Không | Không | Không |
| Tạo hồ sơ giảng viên | **Không, kể cả API** | Trong khoa | Không | Không |
| Bổ nhiệm/thu hồi trưởng bộ môn | Không | Bộ môn trong khoa, vị trí trống | Không | Không |
| Cấu hình kỳ/giai đoạn/giờ/tuần/ngày nghỉ | Có | Chỉ đọc cấu hình | Chỉ đọc cấu hình | Chỉ đọc cấu hình |
| Tạo/sửa/ngừng lớp | Không | Trong khoa | Trong bộ môn | Không tạo/ngừng; sửa chi tiết theo từng quyền được cấp |
| Xem lớp/roster/buổi/điểm danh | Không mở quyền học vụ toàn cục | Trong khoa | Trong bộ môn | Lớp được phân công; điểm danh theo quyền buổi hiện có |
| Thêm/sửa/xóa dòng lịch | Không | Trong khoa | Trong bộ môn | Mặc định không; từng cờ add/edit/delete của đúng lớp |
| Cấp/thu hồi quyền lớp | Không | Lớp trong khoa, chỉ giảng viên đang được phân công | Không | Không |
| Thêm/bỏ sinh viên khỏi roster, sửa ghi chú lớp | Không | Trong khoa | Trong bộ môn | Chỉ khi có `CanManageStudents` của đúng lớp |
| Quản lý hồ sơ sinh viên toàn cục | Không | Trong khoa | Không | **Không, kể cả được cấp quyền lớp** |
| Tạo buổi học thủ công | Theo quyền phiên hiện có | Theo quyền phiên hiện có | Theo quyền phiên hiện có | Theo quyền phiên hiện có |

Quyền roster chỉ sửa thành viên và ghi chú của lớp, không sửa MSSV/họ tên/email/trạng thái hồ sơ sinh viên dùng chung. Bỏ khỏi roster không xóa hồ sơ; roster buổi tương lai sạch được đồng bộ, lịch sử buổi giữ nguyên. Giảng viên luôn bị chặn ngừng lớp qua UI/API, kể cả có đủ quyền sửa/xóa chi tiết. Buổi thủ công tiếp tục tạo/sửa theo API và quyền phiên hiện có; riêng buổi đã sinh từ lịch học phải chỉnh lịch qua luồng xem trước để bảo toàn revision và lịch sử.

Admin vẫn quản lý tài khoản, khoa, phòng/camera và cấu hình kỹ thuật theo quyền hiện có. `TECH_AI` vẫn có danh mục/kỹ thuật phù hợp, không tự nhận quyền học vụ chỉ vì có hồ sơ giảng viên. Thu hồi chức vụ không xóa tài khoản/hồ sơ giảng viên hoặc dữ liệu giảng dạy. Seeder không tự bổ nhiệm lại chức vụ đã thu hồi và không ghi đè hồ sơ đã chỉnh sửa khi khởi động lại.

Khi tạo khoa mới, Admin nhập thêm tên bộ môn đầu tiên và chọn Trưởng khoa từ hồ sơ giảng viên đã có (`initialDepartmentName`, `initialHeadTeacherId`). API khởi tạo khoa, bộ môn và nhiệm kỳ Trưởng khoa trong cùng transaction; chuyển giảng viên được chọn về bộ môn mới. Chỉ nhận giảng viên hoạt động có tài khoản LECTURER, không có ADMIN, chưa có lớp/lịch sử buổi học hoặc dạy thay và không có chức vụ hiện tại hay đã lên lịch. Danh sách chọn dùng lookup `faculty-initial-heads` chỉ dành Admin và kiểm tra lại lúc lưu. Không tạo User/Teacher mới, không mở quyền tạo giảng viên cho Admin hoặc quyền quản lý khoa khác. Sau đó Trưởng khoa dùng các API bộ môn/giảng viên hiện có trong khoa mới.

Luồng tạo/sửa học kỳ và luồng lưu riêng Mốc Tuần 1 đều kiểm tra mốc đã xác nhận là thứ Hai và tuần chứa mốc phải bao gồm ngày bắt đầu học kỳ. Đổi ngày bắt đầu học kỳ cũng phải kiểm tra lại mốc T1 còn hiệu lực; mốc chưa xác nhận có thể lưu dạng nháp.

## Các tệp chính thay đổi

- Frontend mới: `apps/web/src/app/features/class-sections.component.ts`, `academic-configuration.component.ts`, `apps/web/src/app/shared/search-select.component.ts`.
- Frontend tích hợp: `features/management.component.ts`, `sessions.component.ts`, `organization.component.ts`, `students.component.ts`, `policies.component.ts`, `account.component.ts`; `core/api.service.ts`, `models.ts`, `auth.service.ts`, `auth.guard.ts`; `app.routes.ts`, `layout/shell.component.ts`, `styles.scss`, `shared/ui.ts`. Các tệp UI đã sửa từ đợt trước vẫn giữ trong working tree.
- API mới: `Controllers/ClassSectionsController.cs`, `AcademicConfigurationController.cs`, `LookupsController.cs`.
- API tích hợp: `CatalogsController.cs`, `SessionsController.cs`, `StudentsController.cs`, `ManagementAssignmentsController.cs`, `AuthController.cs`, `AttendancePoliciesController.cs`, `Program.cs`.
- API support: `ClassSchedulePlanner.cs`, `BusinessTransaction.cs`, `AccessScope.Academic.cs`, `AccessScope.cs`, `PositionLabels.cs`, `AuthProfileSupport.cs` trong `services/backend/src/HTBAM.Api/Support/`.
- Domain/application: `Entities/AcademicScheduling.cs`, `Entities/InternalCode.cs`, `Entities/Entities.cs`; `DTOs/AcademicContracts.cs`, `DTOs/Contracts.cs`, `Services/AcademicRules.cs` trong các project tương ứng.
- Infrastructure: `AppDbContext.Academic.cs`, `AppDbContext.cs`, `AcademicSeed.cs`, `DevelopmentSeeder.cs` trong `services/backend/src/HTBAM.Infrastructure/Data/`.
- Database: các migration tiến tới [`database/sqlserver/007_academic_scheduling.sql`](../database/sqlserver/007_academic_scheduling.sql) đến `013_lecturer_business_codes.sql`, cùng các bản mirror tương ứng trong `database/`.
- Kiểm thử: `AcademicSchedulingTests.cs`, `AcademicSqlServerTests.cs`, `FeatureRegressionTests.cs`, `DevelopmentSeederTests.cs`; `tests/search_select_check.js`, `timetable_source_check.js`, `academic_api_smoke.ps1`, `academic_api_smoke.js`, fixture Excel nhỏ. `overlay_focus_check.js` thuộc phần UI đã có.

## Migration, seed và áp dụng

Migration 007–013 là các nâng cấp bổ sung sau schema 001 và các nâng cấp 004–006. Không xóa bảng/lịch sử, không đổi mã cũ, không tạo lại buổi cũ. `AcademicTermId`/nguồn lịch của lớp và buổi cũ có thể để null; không tự gán chúng vào học kỳ mới. Các bản SQL mirror đã được kiểm tra đồng bộ và toàn bộ chuỗi được kiểm tra chạy lặp trên SQL Server.

Thứ tự áp dụng cho hệ thống hiện có:

1. Sao lưu database và tạm dừng backend trước khi nâng cấp.
2. Chạy migration runner trên đúng database đích; runner tự áp dụng phần còn thiếu theo thứ tự, kiểm tra checksum và rollback khi lỗi:

   ```powershell
   docker compose -f infra/docker-compose.yml run --rm --no-deps migrate
   ```

3. Build và chạy lại backend/web. Với Docker Compose hiện có:

   ```powershell
   docker compose -f infra/docker-compose.yml up -d --build backend web
   ```

4. Đăng nhập lại/tải lại hồ sơ; Admin mở Cấu hình học vụ để nhập các căn cứ còn thiếu, chuẩn hóa loại phòng. Sau đó quản lý khoa/bộ môn tạo hoặc xem trước cập nhật lớp.

Với database mới, dùng cùng migration runner để áp dụng `001`, `002`, rồi `004`–`013` theo thứ tự. `003_dev_sample.sql` là dữ liệu demo SQL cũ, không phải migration bắt buộc và runner không tự chạy. Development seed mới chỉ chạy khi được bật rõ trong môi trường Development.

SQL 007 seed đúng 16 tiết A và 16 tiết B trong thông báo, hiệu lực 12/01/2026, không seed bảng giờ thi. `AcademicSeed` dùng cùng dữ liệu và chạy idempotent trong Development. Seed kỳ 1 chỉ là bản nháp; kỳ 2 có mốc bắt đầu 12/01/2026 nhưng chưa xác nhận đủ khoảng ngày. Không tạo giai đoạn/ngày kết thúc/giờ cũ giả định.

Các lớp cũ thiếu cấu hình vẫn xem được. Muốn sửa thành lịch mới cần chọn cấu hình hợp lệ, xem trước ảnh hưởng; lớp có lịch sử được bảo vệ không đổi môn/kỳ/giai đoạn nền. Thay phiên bản giờ hoặc thêm ngày nghỉ không tự sửa âm thầm các buổi đã lưu: quản lý phải xem trước và áp dụng từng lớp. Bản xem trước quá hạn do trạng thái/quyền/cấu hình thay đổi bị từ chối.

## Kiểm thử đã chạy

| Kiểm tra | Kết quả |
| --- | --- |
| `dotnet test services/backend/tests/HTBAM.Tests/HTBAM.Tests.csproj --no-restore` với `HTBAM_TEST_SQL_CONNECTION` | **189 đạt, 0 lỗi, 1 bỏ qua**; ca bỏ qua cần workbook HUIT thật |
| SQL Server cách ly | Toàn bộ migration 001–013, nâng cấp DB cũ không history, chạy lặp/checksum/rollback và cấp mã đồng thời đạt; container không gắn volume và được xóa sau kiểm thử |
| `npm.cmd test` tại `apps/web` | **37/37 đạt**; gồm UI học vụ/import, searchable select, focus overlay, fixture và đồng bộ SQL mirror |
| `npm.cmd run build` tại `apps/web` | Production build đạt; initial bundle 386,33 kB, không lỗi budget |
| `dotnet build ...HTBAM.Api.csproj --configuration Release` | Đạt, 0 lỗi và 0 cảnh báo |

Kiểm thử backend bao gồm chia kỳ 1/2/3 tín chỉ, mọi tiết A/B, chặn áp dụng giờ mới ngược năm 2025, thiếu mốc tuần, ngày nghỉ, đổi phòng/xung đột, idempotence, bản xem trước hết hiệu lực, giữ lịch sử và buổi hủy tay; scope/IDOR, thu hồi quyền, vị trí trống và seeder không khôi phục chức vụ.

Các kiểm thử SQL/HTTP tạo database `HTBAM_AcademicTests_<GUID>` riêng và dọn sau chạy. Smoke script dùng `sqlcmd` và backend Debug đã build, chỉ mở cổng loopback 5511. Không còn database thử sau khi kết thúc. Khi rà bộ chạy thử, phát hiện script 005 cũ có `USE HTBAM`; kiểm tra metadata xác nhận các cột Note đã tồn tại từ tháng 9 nên lệnh này không đổi schema nghiệp vụ. Bộ chạy đã được sửa và chạy lại. Các bảng học vụ mới chưa có trong database HTBAM đang dùng.

**Chưa kiểm tra trực quan desktop/mobile:** browser runtime trả về `No browser is available`. Build Angular và kiểm tra DOM doubles không thay thế việc mở trình duyệt để đánh giá bố cục, cuộn/portal và thao tác thực tế. Không có ảnh chụp màn hình mới hoặc kết luận đạt kiểm thử viewport.

Phần thay logo của đợt UI trước vẫn cần tệp ảnh HUIT gốc đọc được trên máy: ảnh có trong hội thoại nhưng không có asset tương ứng trong thư mục đính kèm hoặc tệp HUIT phù hợp ở Downloads. Chưa thay ba biểu tượng thương hiệu bằng ảnh khác hoặc tự vẽ lại logo. Chi tiết phần UI đã bàn giao tại [`docs/UI_REVIEW_2026-10-23.md`](UI_REVIEW_2026-10-23.md).

## Cấu hình còn thiếu để dùng lịch chính thức

1. Ngày bắt đầu/kết thúc chính thức của HK1 2025–2026; ngày kết thúc HK2 2025–2026. Mốc bắt đầu HK2 12/01/2026 đã giữ đúng năm học.
2. Ranh giới riêng của phương án chia hai và chia ba giai đoạn, hoặc văn bản xác nhận chính sách chia đều ngày được chọn. Không dùng hai phần đầu của cách chia ba cho môn 2 tín chỉ.
3. Căn cứ xác nhận T1 và nhóm lớp áp dụng cho từng lịch tuần trong Excel. Các mốc suy ra từ dữ liệu không được coi là chính thức.
4. Phiên bản bảng giờ học có căn cứ cho các ngày trước 12/01/2026. Không áp ngược phiên bản mới.
5. Chuẩn hóa loại phòng chưa biết và các ngày nghỉ/ngoại lệ thực tế cần áp dụng. Hệ thống không đoán phòng từ tên hoặc tự bổ sung ngày nghỉ.

Các thông tin thiếu này là dữ liệu cấu hình cần nhà trường xác nhận; không được thay bằng ngày nhỏ nhất/lớn nhất của Excel, số tín chỉ hoặc dữ liệu demo 2030 trong kiểm thử.
