# Nhập Excel thời khóa biểu HUIT — cập nhật 25/10/2026

## Cập nhật nhận diện và ánh xạ ngày 25/10

- `maxHeaderScanRows = 20` chỉ là hằng nội bộ để dò bảng. API inspection trả riêng `headerStartRow = 7`, `headerRowCount = 2`, `headerEndRow = 8`, `dataStartRow = 9`. Giữ `headerRow` như alias tương thích API cũ; không dùng giới hạn dò làm dòng tiêu đề.
- Mặc định bước 2 chỉ hiển thị **Tiêu đề: dòng 7–8. Dữ liệu bắt đầu: dòng 9.** Nút **Chỉnh nhận diện thủ công** mới mở hai trường chỉnh. Bản chỉnh chưa áp dụng ẩn thông báo dòng cũ và chặn đi tiếp; **Áp dụng nhận diện** gọi lại backend, thay đồng thời metadata, ánh xạ và 5 dòng mẫu. Đóng chỉnh thủ công hủy bản chỉnh chưa áp dụng.
- Bước 3 tách cột đơn và nhóm **Các tuần có lịch**. HUIT hiện **Đã ánh xạ T1–T26, cột L–AK**; chi tiết chỉ đọc từng cột, không có 26 dropdown tuần hoặc dropdown tuần dạng cột đơn. Backend cũng từ chối đổi riêng một cột tuần hoặc ánh xạ nhóm tuần HUIT sang một cột đơn.
- Ghép hai tầng thành `Lịch học / Thứ`, `Lịch học / Từ tiết`, `Lịch học / Đến tiết`, `Lịch học / Loại lịch`, `Lịch học / Số tiết`, `Thời gian học / Bắt đầu`, `Tuần học / T1` … `Tuần học / T26`, `Thời gian học / Kết thúc`. Cột độc lập giữ tên chính, không lấy ô dữ liệu làm tên và không có `Cột M` thay cho T2.
- Dropdown cột đơn có tìm kiếm tên/cột/mẫu, nhãn **Tên cột — cột Excel — dữ liệu mẫu**. Cột đang dùng cho trường khác bị vô hiệu hóa kèm lý do; backend kiểm tra lại chống ánh xạ trùng. Khi cần đổi chéo hai cột, bỏ một ánh xạ trước rồi chọn lại.
- Bảng xem nhanh dùng tối đa **5 dòng nguồn**, cập nhật theo ánh xạ đang chọn: mã LHP, môn, loại, thứ, tiết, phòng, ngày, tập tuần. Đây là mẫu để kiểm tra cột; bước **Tiếp tục xem trước** mới kiểm tra toàn bộ. Bước 2 dùng nút **Tiếp tục ánh xạ cột**.
- Dropdown giới hạn **320 px**, tính khoảng trống bên trên/bên dưới trong modal và trước footer; giữ trong focus trap. Cuộn bánh xe/chạm ngoài dropdown bị khóa khi danh sách mở; Escape đóng danh sách, Tab rời trường, mũi tên/Enter/Home/End hỗ trợ chọn. Khi đổi viewport/bàn phím ảo, danh sách đóng để tránh vị trí cũ. Bỏ vùng cuộn dọc riêng của bảng ánh xạ; chỉ thân popup cuộn dọc.
- Tuần được chọn bằng `x`, `X`, `1`, `true` hoặc `✓/✔/☑/√`; ô trống/false/0 không chọn, ký hiệu khác báo lỗi. Chuẩn hóa tăng dần, không trùng. Test tách khoảng ngày cùng lớp với tập **T1–T10** và **T12,T15–T18** xác nhận tạo hai lịch và nhập lại không trùng.

API không thêm endpoint; `POST .../sheets` và `POST .../inspect` bổ sung các thuộc tính header ở trên, `columns[].sample`, `sampleRows` (5 dòng), `columnGroups` (nhóm tuần). `POST .../preview` kiểm tra ánh xạ đơn trùng và bảo toàn nhóm tuần. Không đổi schema, dependency, Dockerfile hay Compose trong đợt này.

Self-review đối chiếu toàn bộ thay đổi của đợt với snapshot `.runtime-fix-backup-huit-mapping` và đọc các tệp mới. Đã sửa finding về dropdown cột/chính sách bị sự kiện cuộn sau focus đóng ngay khi vừa mở, thêm regression test; đã ẩn thông báo header cũ khi bản chỉnh thủ công đang chờ áp dụng. Đọc ảnh mobile phát hiện tiêu đề bảng tràn và nút footer ngắt quá nhiều dòng: đã cho tiêu đề xuống dòng và footer hai cột. Các thay đổi có trước đợt này được giữ nguyên.

Tệp thay đổi của đợt: component import, stylesheet, `shared/search-select.component.ts` (thêm chế độ `boundToModal` dùng cho riêng các ô chọn trong importer), `ClassSectionWorkbook.cs`, `ClassSectionExcelImport.cs`, DTO import, `HuitWorkbookImportTests.cs`, `tests/class_section_ui_check.js`, `tests/search_select_check.js`, `tests/huit_import_api_smoke.cjs`, tài liệu này và phần dẫn ở báo cáo lớp học phần. Tệp mới: `apps/web/src/app/shared/import-column-select.component.ts`, `tests/huit_import_browser_smoke.cjs`.

Đã sửa trực tiếp importer và wizard trong tab **Danh mục dữ liệu → Lớp học phần → Nhập Excel**. Giữ mọi thay đổi có sẵn, không commit/push/reset/stash/clean, không xóa volume hay dữ liệu. Không có `AGENTS.md` áp dụng được tìm thấy trong workspace/thư mục cha. Đã chụp snapshot trước sửa trong `.runtime-fix-backup-huit-import` (gitignore).

## File thật và nguyên nhân

Đã đọc file thật được tìm thấy tại Downloads, tên `thoi-khoa-bieu-du-kien-hk1-nam-hoc-2025-2026-he-dhcq-20250530040845-e.xls` (không có hậu tố `(1)` trong tên trên máy). Dung lượng **4.021.760 byte**, SHA-256 `fd4312a086d35236f40a76de795f946211f6cd4d61f26efcb07b799145837741`. Đã dùng bản sao nguyên byte khi kiểm thử để không can thiệp file đang mở trong Excel.

File có một sheet `Sheet1`, **10.202 dòng tài liệu / 41 cột**, tiêu đề tài liệu dòng 4 và tiêu đề bảng dòng **7–8**. Có thêm 10 dòng trống chỉ có định dạng ở cuối vùng Excel; reader loại các dòng trống cuối này khi báo số dòng. Sau tiêu đề và các dòng ghi chú cuối bảng có **10.190 dòng lịch**, gom thành **5.447 Mã LHP**. Trong đó **801 dòng dùng Thứ = 8 cho Chủ nhật**.

Importer cũ chặn ở 3.000 dòng/40 cột và chỉ đọc tiêu đề dòng 1. Nó cũng yêu cầu Mã môn/Mã GV dạng bảng phẳng, lưu preview vào SQL và trả mọi nhóm về trình duyệt. Những điểm đó không phù hợp file thật.

## Quy tắc nhận diện và giới hạn mới

- Nhận `.xls` và `.xlsx`; kiểm tra MIME, extension, chữ ký OLE/ZIP và cấu trúc workbook.
- Giới hạn **20.000 dòng / 64 cột / 10 MB**, tối đa 30 sheet và 64 MB sau giải nén XLSX. File thật 3,84 MB nằm trong giới hạn, không cần tăng dung lượng upload.
- Dò `Mã LHP` trong **20 dòng đầu**; nhận diện HUIT khi dòng chính có bộ STT, Mã LHP, Tên môn học, Số TC, Lớp học, Lịch học, Thời gian học, Tuần học, Tổng số tiết, Phòng, Giảng viên. Dòng tiếp theo cung cấp Thứ, Từ/Đến tiết, Loại lịch, Số tiết, Bắt đầu, T1–T26, Kết thúc.
- Ghép tiêu đề chính/phụ thành nhãn cột hoàn chỉnh; riêng Bắt đầu/Kết thúc được gắn với Thời gian học. Không yêu cầu sửa/xóa phần đầu file.
- Bảng phẳng có thể có tiêu đề trong 20 dòng đầu, không bắt buộc dòng 1. File mẫu tải xuống vẫn dùng dòng 1 cho dễ nhập.
- Cho chọn lại dòng tiêu đề 1–20, số dòng tiêu đề 1/2, rồi sửa từng ánh xạ cột. Thiếu trường bắt buộc báo tên trường rõ ràng.
- Giới hạn thông thường là hướng dẫn trung tính. Error box ban đầu rỗng; lỗi chỉ xuất hiện sau thao tác kiểm tra file. Lỗi vượt dòng/cột có dạng `File có ... dòng và ... cột. Giới hạn hiện tại là 20.000 dòng và 64 cột trong workbook.`

## Ánh xạ HUIT

| Cột nguồn | Trường/ý nghĩa |
| --- | --- |
| A · STT | Nhận biết dòng dữ liệu, phân biệt ghi chú cuối bảng |
| B · Mã LHP | `SourceClassSectionCode`, giữ mã nguồn dạng văn bản và số 0 đầu; không dùng làm PK/mã nội bộ |
| C · Tên môn học | Đối chiếu tên chuẩn hóa với danh mục trong phạm vi quản lý |
| D · Số TC | Đối chiếu tín chỉ; lệch danh mục báo lỗi, không sửa môn |
| E · Lớp học | Tên lớp/nhóm sinh viên |
| F · Thứ | 2–7 là thứ Hai–Bảy; **8 hoặc CN là Chủ nhật** trong HUIT |
| G/H · Từ tiết/Đến tiết | Tiết bắt đầu/kết thúc |
| I · Loại lịch | LT/TH |
| J · Số tiết | Số tiết nguồn theo dòng, giữ riêng metadata |
| K · Bắt đầu | Ngày bắt đầu |
| L–AK · T1–T26 | Dấu tuần được chọn |
| AL · Kết thúc | Ngày kết thúc |
| AM · Tổng số tiết | Tổng tiết nguồn, giữ riêng metadata |
| AN · Phòng | Đối chiếu mã hoặc tên phòng duy nhất; hỗ trợ `B308 - địa chỉ` bằng mã B308 |
| AO · Giảng viên | Tách `Mã - Tên`, giữ số 0 đầu của mã; đối chiếu danh mục hoạt động |

Không suy mã môn từ Mã LHP. Nếu không có Mã môn, đối chiếu **tên môn chuẩn hóa đầy đủ**, không dùng fuzzy match. Chỉ một kết quả mới tự chọn; không có hoặc có nhiều kết quả thì báo lỗi và cho chọn môn ở preview. Có thể chọn lại giảng viên/phòng cho từng dòng. Không tự tạo môn, giảng viên hoặc phòng.

Giá trị `x`, `X`, `1`, `true` chọn tuần; trống/0/false không chọn. Dấu khác báo đúng cột Tn. Grid HUIT không có tuần đánh dấu bị lỗi, **không** trở thành học tất cả tuần. Với cột chuỗi của bảng phẳng, trống vẫn là học tất cả tuần; chuỗi `T1,T3,T5` được kiểm tra, loại trùng và sắp tăng.

Mỗi chip Tn kèm ngày đầu/cuối tuần theo mốc T1 học kỳ. Thiếu mốc đã xác nhận thì không suy từ ngày bắt đầu từng dòng; preview báo rõ và có liên kết Cấu hình học vụ. Mốc DEMO được ghi nhãn Mốc tuần mẫu. Ngày, giai đoạn, bảng giờ, thứ, tiết và xung đột tiếp tục theo `AcademicRules`/planner hiện có.

Nguồn Số tiết/Tổng số tiết không bị coi là số tiết của một buổi, không ghi đè tín chỉ hay bảng giờ. Sau import, metadata nguồn, tên/mã thô, dòng Excel, hash file, sheet và định dạng được lưu trong `ClassSchedules.SourceDataJson` có sẵn.

## Wizard, preview và transaction

Wizard gồm **6 bước**: Chọn file → Sheet/định dạng → Ánh xạ cột → Xem trước → Xác nhận → Kết quả. Bước 1 hỗ trợ kéo thả/chọn, tên/dung lượng/số sheet và **Tải file mẫu**. Bước 3 đánh dấu trường bắt buộc, chọn học kỳ/chính sách/giai đoạn. Bước 4 gom lớp, mở rộng LT/TH, xem danh mục, giờ, ngày, chip tuần, số tiết nguồn, lỗi/cảnh báo; có tìm kiếm, lọc trạng thái và phân trang. Có tải toàn bộ báo cáo kiểm tra CSV để đối chiếu ngoài ứng dụng.

Preview **không ghi database**. Dữ liệu kiểm tra và token gắn người dùng ở bộ nhớ backend, hạn 30 phút; cache có hạn mức 100.000 dòng. API mặc định chỉ trả **100 dòng**, tối đa **200 dòng/trang**, không trả toàn bộ 10.190 dòng hoặc request ghi nội bộ. Backend đọc danh mục/lịch cần thiết theo lô, dùng dictionary và chỉ mục xung đột theo ngày; không truy vấn SQL từng dòng Excel/nhóm khi xem trước. Giao diện chỉ render trang được trả về; kết quả import cũng phân trang 100 bản ghi.

Đổi ánh xạ làm mất xác nhận và yêu cầu kiểm tra lại để có token mới. Đổi file/sheet/dòng tiêu đề bỏ các chọn tay cũ để không áp nhầm sang dữ liệu khác. Bước 5 và API xác nhận đều **chặn khi còn lỗi bắt buộc**; không âm thầm nhập phần hợp lệ trong file còn lỗi. Nhóm lớp có lỗi không được ghi.

Chỉ **xác nhận** mới mở transaction `BusinessTransaction`, lấy application lock, kiểm tra lại quyền/phạm vi, planner/token và xung đột. Receipt/result bền vững mới được tạo trong SQL lúc này. Ghi lỗi nhóm sau rollback cả các nhóm trước, số cấp, lịch, session và receipt. Cùng token trả lại kết quả đã lưu; import lại cùng mã nguồn/lịch phân loại trùng. Unique `(AcademicTermId, SourceClassSectionCode)` và khóa giao dịch của schema 011 được dùng lại. Dòng trùng so theo loại lịch/thứ/tiết/nhóm giờ/phòng/ngày/tuần, không theo GUID tạm của preview. Không ghi đè lớp nguồn cũ có nội dung khác.

Không tạo migration mới: schema, unique index và bảng receipt hiện có đã đủ. Toàn bộ **22 tệp SQL 001–011** trong nguồn/mirror không đổi so với snapshot đầu đợt. Preview SQL cũ/result đã hoàn thành vẫn được đọc tương thích. Cache preview mới mất khi backend restart/evict; người dùng cần tải lại file, còn receipt xác nhận đã commit vẫn replay được. Hiện Compose có một backend; triển khai nhiều replica cần sticky session hoặc cache dùng chung.

## API đã thêm/thay đổi

| API | Hành vi |
| --- | --- |
| `POST /api/class-sections/import/sheets` | Multipart file; trả tên sheet, kích thước thật, định dạng, header, cột/ánh xạ và giới hạn |
| `POST /api/class-sections/import/inspect` | Multipart file/sheet/headerRow/headerRowCount; nhận diện lại theo lựa chọn |
| `POST /api/class-sections/import/preview` | Như trước, thêm `settings` JSON cho header, mapping, courseSelections theo mã nguồn, teacherSelections/roomSelections theo dòng; trả trang 1 và tổng hợp |
| `GET /api/class-sections/import/preview/{token}` | `page`, `pageSize` (1–200), `q`, `status`; đọc cache của đúng người dùng |
| `GET /api/class-sections/import/preview/{token}/download` | Báo cáo CSV toàn bộ dòng đã kiểm tra, không ghi database |
| `GET /api/class-sections/import/template` | File XLSX 14 cột đúng yêu cầu, dòng 1 là header, sheet hướng dẫn và ví dụ giữ mã nguồn dạng text |
| `POST /api/class-sections/import/confirm` | Chỉ nhận token, chặn lỗi bắt buộc, transaction và durable replay |
| `GET /api/class-sections/import/result/{token}/download` | CSV kết quả của chính người đã xác nhận |

Các API đều kiểm tra quyền ở backend; viewer bị từ chối cả đọc preview, template và download. CSV có UTF-8 BOM, escape ô và phòng trường hợp giá trị bắt đầu bằng ký tự công thức. File mẫu không cần font hệ thống để xuất được trong Docker Linux.

## Kết quả kiểm thử thực tế

| Kiểm tra | Kết quả |
| --- | --- |
| Backend | **151 pass / 0 fail / 0 skip**, có bật SQL thật và file HUIT thật |
| HUIT XLS và XLSX giả lập | Header dòng 7–8, 41 cột, tách mã GV, dấu x/X/1/true, ngày, số tiết nguồn, LT/TH, nhóm và replay đạt |
| File HUIT thật | **10.202 dòng, 41 cột, 10.190 dòng dữ liệu, 5.447 nhóm**, tất cả 801 Chủ nhật mã 8 ánh xạ đúng |
| Môn không có/trùng tên | Báo lỗi; chọn tay có cảnh báo; không tự tạo danh mục |
| Thiếu mã LHP/lịch/GV/phòng/T1, grid tuần trống | Báo đúng dòng/cột và chặn ghi |
| Header/mapping thủ công, giới hạn cột | Đạt; lỗi giới hạn có số cột thực tế |
| Preview và rollback SQL | Không tạo receipt ở preview; lỗi ghi sau rollback toàn bộ; replay/unique/cấp số đồng thời vẫn đạt |
| Frontend | **29 pass / 0 fail**: thêm header/draft, nhóm tuần, mẫu, chống ánh xạ trùng, bố trí dropdown, khóa cuộn và regression focus/scroll cho cột và danh mục |
| Angular production | Đạt trên máy và Docker, initial bundle **382,60 kB** |
| Docker backend/web | Build thành công, backend healthy, restart count 0 |
| HTTP HUIT thật | **35 kiểm tra đạt**, gồm header/dataStartRow, nhóm tuần, nhãn K–AO, 5 dòng mẫu, metadata/inspect, phân trang, tìm/lọc, template, CSV, quyền và confirm bị chặn |
| Preview không ghi SQL | HTTP smoke đối chiếu số bản ghi **mọi bảng** trước/sau toàn bộ kiểm tra (chụp sau đăng nhập): giống hệt |
| Bảng phẳng/API import | **33 kiểm tra HTTP đạt**: XLS/XLSX, nhiều sheet, LT/TH, duplicate, replay và quyền |
| API hệ thống | **47 kiểm tra HTTP đạt**; web assets/Nginx proxy đạt |
| Compose config/migrate/seed/startup | Đạt; migrate/seed exit 0, replay giữ số bản ghi mọi bảng |
| File mẫu tải xuống | Đã tải và đọc lại: 14 cột, 2 sheet, mã nguồn dạng text giữ số 0 đầu, ngày dd/MM/yyyy, ví dụ tuần trống |
| Trình duyệt với file HUIT thật | **43 kiểm tra đạt** trên Chromium headless, desktop **1366×900** và mobile giả lập **390×844**; header, chỉnh tay, nhóm tuần, 5 dòng mẫu, tìm kiếm/khóa cột đã dùng, phím, cuộn, footer và preview |
| Trực quan desktop/mobile | Đã mở và đọc ảnh chụp từ trình duyệt; lưu **10 ảnh** và `report.json` trong `.runtime-fix-backup-huit-mapping/browser`. Computer Use native không khả dụng nên dùng Playwright/Chromium đã cài, không thêm dependency vào dự án. Chưa chạy trên điện thoại vật lý/bàn phím ảo thật. |

Ảnh kiểm tra: [nhận diện mobile](../.runtime-fix-backup-huit-mapping/browser/mobile-recognition.png), [nhóm tuần mobile](../.runtime-fix-backup-huit-mapping/browser/mobile-mapping.png), [dropdown mobile](../.runtime-fix-backup-huit-mapping/browser/mobile-dropdown.png), [xem nhanh desktop](../.runtime-fix-backup-huit-mapping/browser/desktop-quick-preview.png), [footer mobile ở preview](../.runtime-fix-backup-huit-mapping/browser/mobile-preview.png). Ảnh và workbook thật nằm trong thư mục runtime được gitignore, không đưa dữ liệu nguồn vào fixture công khai.

Lượt HTTP ngày 25/10 trên file thật, chạy trên localhost, hoàn thành khoảng **3,49 giây** cho cả chuỗi kiểm tra; trang 100 dòng khoảng **268.431 byte**. Đây là số đo trên máy hiện tại, không phải cam kết hiệu năng cho mọi database. Chi tiết tại `.runtime-fix-backup-huit-import/api-report.json`; TRX mới ở `.runtime-fix-backup-huit-mapping/test-results/backend.trx`. Các integration tests chỉ dọn database `HTBAM_AcademicTests_<GUID>` do chính chúng tạo; không drop/reset HTBAM.

## Dữ liệu chưa thể tự đối chiếu

Với danh mục và học kỳ đang có trên HTBAM, file thật còn:

- **10.190 dòng chưa đối chiếu môn** bằng tên đầy đủ trong phạm vi quản lý hiện tại.
- **10.190 dòng chưa đối chiếu giảng viên** bằng mã/tên nguồn.
- **10.136 dòng chưa đối chiếu phòng**; 54 dòng đã đối chiếu được.
- HK1 2025–2026 thiếu ngày bắt đầu/kết thúc, mốc T1 xác nhận và ranh giới giai đoạn chính thức. Bảng giờ hiện có bắt đầu hiệu lực 12/01/2026, không tự áp ngược cho lịch 2025.

Vì vậy kiểm thử với **file thật dừng ở preview và xác nhận bị chặn đúng quy tắc**. Không tuyên bố đã nhập các lịch thật thành công. Cần đối chiếu/chọn danh mục, xác nhận cấu hình học kỳ/bảng giờ có căn cứ rồi xem trước lại. Các file HUIT giả lập và bảng phẳng đã được kiểm thử ghi/replay thành công bằng fixture danh mục phù hợp.

## Tệp sửa/thêm

- Sửa: `apps/web/src/app/core/api.service.ts`, `features/class-section-import.component.ts`, `apps/web/src/styles.scss`.
- Sửa: `services/backend/src/HTBAM.Api/Controllers/ClassSectionImportsController.cs`, `Support/ClassSectionExcelImport.cs`, `HTBAM.Application/DTOs/ClassSectionImportContracts.cs`.
- Thêm: `HTBAM.Api/Support/ClassSectionWorkbook.cs`, `ImportPlanningContext.cs`, `ImportPreviewStore.cs`.
- Sửa tests: `ClassSectionImportTests.cs`, `ClassSectionImportSqlTests.cs`, `tests/class_section_ui_check.js`.
- Thêm tests/fixtures: `HuitWorkbookImportTests.cs`, `tests/huit_import_api_smoke.cjs`, `tests/generate_huit_import_fixtures.cjs`, `tests/fixtures/huit-two-header.xls` và `.xlsx`.
- Tài liệu: báo cáo này, `CLASS_SECTION_IMPORT_AND_NUMBERING.md`, `DATABASE_MIGRATION_AND_DEVELOPMENT_SEED.md`.

Các đường dẫn support/DTO ở trên tính từ `services/backend/src`; backend tests từ `services/backend/tests/HTBAM.Tests`. Không sửa dependency, Dockerfile, Compose, migration hay seed trong đợt này.

Lệnh kiểm tra từ gốc (không in connection string/mật khẩu):

```powershell
# Đặt HTBAM_TEST_SQL_CONNECTION và HTBAM_HUIT_WORKBOOK trước khi chạy để không skip test SQL/file thật.
dotnet test services/backend/tests/HTBAM.Tests/HTBAM.Tests.csproj
npm.cmd --prefix apps/web test
npm.cmd --prefix apps/web run build
node tests/huit_import_api_smoke.cjs
# Đặt thêm HTBAM_PLAYWRIGHT_MODULE tới module Playwright/playwright-core đã cài.
node tests/huit_import_browser_smoke.cjs
node tests/class_section_import_api_smoke.cjs
node tests/development_seed_api_smoke.cjs
powershell -NoProfile -ExecutionPolicy Bypass -File tests/verify_development_seed.ps1
```

Triển khai từ `infra`: `docker compose config --quiet`, `docker compose build backend web`, `docker compose up -d backend web`. Không cần migration mới và không dùng `down -v`.
