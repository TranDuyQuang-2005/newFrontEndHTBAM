# REVIEW TOÀN BỘ WEB HTBAM HIỆN TẠI

Nguồn review: source nhánh `main` của `nguyengiahung1665/newFrontEndHTBAM`, đối chiếu với kiến trúc/luồng nghiệp vụ HTBAM hiện tại.

## Kết luận

Không nên viết lại web. Phần quản trị và nghiệp vụ nền đã khá đầy đủ; khoảng trống chính là runtime AI production.

### Ma trận màn hình

| Màn hình | Trạng thái hiện tại | Theo đồ án |
|---|---|---|
| Login | Tốt | JWT/RBAC |
| Tổng quan | Có | Dữ liệu realtime phụ thuộc full AI |
| Sinh viên | Tốt | CRUD, CSV, active/inactive, Face Enrollment |
| Face Enrollment | Workflow backend/UI có | Stage-2F1 bổ sung ArcFace runtime thật |
| Danh mục | Khá đầy đủ | Khoa/bộ môn/lớp/môn/GV/lớp HP/phòng/camera/roster |
| Cơ cấu & phân quyền | Có | Management Assignment |
| Video | Tốt | Upload/preview/delete; annotation thật còn thiếu |
| Buổi học | Tốt về lifecycle | Start thật còn phụ thuộc sessionInference |
| Chi tiết buổi học | Có contract | Stable identity/behavior realtime cần worker |
| Cảnh báo | Tốt | ACK/CLOSE/REOPEN |
| Lịch sử | Có | behaviors/attendance/identities |
| Báo cáo | Tốt | session/student/class-section, PDF/Excel, auto-comment |
| Chuyên cần | Có | Attendance Policy |
| Tìm kiếm | Có component | Sidebar đang ẩn |
| Trạng thái hệ thống | Tốt | Backend/DB/MinIO/AI/Face/LLM |
| Tài khoản | Có | User profile |
| Admin users | Có | User/role |
| Audit logs | Có | Nhật ký |

## Luồng đúng của đồ án

### Nhận diện sinh viên

Sinh viên đăng ký khuôn mặt
→ AI tạo `arcface.npy` 512-D
→ backend lưu `StudentFaceTemplate`
→ khi buổi học bắt đầu backend gửi `rosterCandidates`
→ AI chỉ so khuôn mặt người trong video với template sinh viên trong roster
→ chưa đủ tin cậy: UNKNOWN nhưng vẫn phân tích hành vi
→ đủ tin cậy: StudentId/MSSV
→ backend backfill behavior/alert trước đó.

Không dùng D5 offline cũ làm database sinh viên.

### Hành vi

Video/RTSP
→ person detection/tracking
→ stable person
→ exact D6 feature extraction
→ MViT 768
→ causal T=3
→ HTBAM-X `htbamx-prod-20261022`
→ FOCUSED / DISTRACTED / SLEEPY / ACTIVE.

`PHONE_USE` là auxiliary/rule signal.
`OUT_OF_VIEW` là state/quality/tracking status.

## Khoảng trống production còn lại

1. Face Enrollment runtime — Stage-2F1 đang bổ sung.
2. Full session worker `/jobs/start`.
3. Identity resolver video → roster template.
4. RTSP/camera live worker.
5. Annotated video output.
6. `sessionInference=true` chỉ bật sau khi full smoke PASS.

## Các điểm UI/UX nên cải thiện sau Stage-2F1

1. History/Reports/Alerts đang yêu cầu nhập ID thủ công ở nhiều chỗ; nên đổi sang searchable dropdown.
2. Search page tồn tại nhưng sidebar đang `visible:false`.
3. Notification icon topbar chưa thấy luồng unread alert hoàn chỉnh.
4. System page nên hiển thị rõ version HTBAM-X và ArcFace.
5. Session Detail nên phân biệt UNKNOWN / IDENTIFIED / CONFLICT bằng badge rõ.
6. Khi Start Session bị chặn, UI nên nêu capability nào thiếu.
7. Camera cần preview/test feedback rõ hơn.
8. Annotated video action chỉ nên hiện khi capability true.
9. Reports nên có chart trực quan hơn progress bar/table.
10. Kiểm tra responsive table/face enrollment trên màn hình nhỏ.

## Documentation mismatch

README đang ghi `apps/web/ React UI` nhưng source thực tế là Angular.
README phần AI cũng đã cũ: HTBAM-X classifier hiện đã tích hợp và exact video offline smoke đã PASS.

## Không cần làm lại

- Auth/JWT/RBAC.
- Student CRUD/import.
- Catalogs.
- Roster.
- Session entities/lifecycle.
- MinIO.
- Alert engine/CRUD.
- History API.
- Reports PDF/Excel.
- Audit.
- LLM provider abstraction.
- HTBAM-X classifier core.

## Checklist review thủ công khi mở web

Lecturer:
1. Tổng quan
2. Sinh viên
3. Import CSV
4. Tạo/sửa/ngừng hoạt động sinh viên
5. Đăng ký khuôn mặt
6. Danh mục
7. Roster
8. Video upload/preview/delete
9. Buổi học
10. Chi tiết buổi học
11. Cảnh báo
12. Lịch sử
13. Báo cáo PDF/Excel
14. Chuyên cần
15. Trạng thái hệ thống
16. Tài khoản

Admin:
17. Cơ cấu & phân quyền
18. Người dùng & phân quyền
19. Audit logs

## Lệnh chạy

```bat
cd /d E:\newFrontEndHTBAM
docker compose -f infra\docker-compose.yml build ai backend web
docker compose -f infra\docker-compose.yml up -d
docker compose -f infra\docker-compose.yml ps
start http://127.0.0.1:5173
```

Health:

```bat
curl http://localhost:5000/health
curl http://localhost:8001/health
```

Logs:

```bat
docker compose -f infra\docker-compose.yml logs --tail=120 ai
docker compose -f infra\docker-compose.yml logs --tail=120 backend
docker compose -f infra\docker-compose.yml logs --tail=120 web
```
