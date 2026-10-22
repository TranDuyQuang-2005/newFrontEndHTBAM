# Quy trình xử lý và lưu trữ HTBAM Web System v3-complete

Tài liệu này mô tả contract triển khai của source. Nguyên tắc: Web/Backend không chạy PyTorch; SQL Server là nguồn dữ liệu nghiệp vụ; Object Storage giữ file lớn/sinh trắc; AI Service xử lý camera/video và trả event có idempotency key.

## 1. Đăng nhập và phân quyền
1. Client gửi username/password đến Backend.
2. Backend kiểm tra trạng thái User, hash password và role.
3. Backend phát JWT; endpoint nghiệp vụ dùng `[Authorize]`/role.
4. Thao tác nhạy cảm ghi `AuditLogs`.

Lưu: `Users`, `Roles`, `UserRoles`, `AuditLogs`. Không lưu plaintext password.

## 2. Danh mục, lớp học phần và roster
1. Admin tạo Faculty, Department, StudentClass, Course, Teacher, ClassSection, Room, Camera.
2. Admin gán roster bằng danh sách Student đang active.
3. Khi tạo Session, Backend snapshot roster vào `SessionStudents`; roster của Session không phụ thuộc việc lớp bị sửa sau đó.

Lưu: `Faculties`, `Departments`, `StudentClasses`, `Students`, `Teachers`, `Courses`, `ClassSections`, `Enrollments`, `Rooms`, `Cameras`, `SessionStudents`.

## 3. Face Enrollment
### 3.1. Thu ảnh
1. Admin mở hồ sơ Student và tạo `FaceEnrollment`.
2. Upload nhiều JPEG/PNG/WebP; source có thể là UPLOAD hoặc CAMERA.
3. Backend kiểm tra dung lượng <= 8 MB, magic bytes, pose, SHA-256 chống trùng.
4. File được ghi vào bucket private theo object key ngẫu nhiên; SQL chỉ ghi metadata.
5. Preview Web nhận presigned GET URL thời hạn ngắn; bucket không public.

Object key ảnh: `face-enrollment/{studentId}/{enrollmentId}/{guid}.{ext}`.

SQL: `FaceEnrollments`, `StudentFaceImages`.

### 3.2. Submit và quality/template
1. Chỉ submit khi có tối thiểu 6 ảnh và đủ FRONT/LEFT/RIGHT.
2. Enrollment chuyển `PENDING_AI`.
3. Khi Face AI chưa sẵn sàng, hệ thống giữ PENDING_AI, không tạo embedding giả.
4. Khi model thật sẵn sàng, Backend cấp cho AI manifest gồm presigned GET từng ảnh + presigned PUT cho template.
5. AI: face detect -> align -> quality assessment -> ArcFace -> aggregate template.
6. AI upload template trước, rồi callback quality từng ảnh + metadata model.
7. Backend chỉ chuyển READY nếu: số ảnh PASS đủ, pose PASS đủ, `TemplateRef` đúng và object template thực sự tồn tại trong storage.
8. Template cũ được `IsActive=false`; template mới active.

Object key template: `face-templates/{studentId}/{enrollmentId}/arcface.npy`.

SQL chỉ lưu `TemplateRef`, quality, pose coverage, model/version/dimension; không gửi raw embedding xuống Web.

Trạng thái: `IN_PROGRESS -> PENDING_AI -> PROCESSING -> READY`; hoặc `NEEDS_RETAKE/FAILED`. Không cho cancel khi PROCESSING để tránh race với callback/upload template.

## 4. Video upload
1. Backend nhận video qua multipart, stream qua file tạm để tránh giữ video lớn trong RAM.
2. Tính SHA-256, kiểm tra extension/dung lượng, upload bucket private.
3. SQL lưu `StorageRef`, hash, size, content type, status.
4. Web preview dùng presigned URL ngắn hạn.
5. Khi chạy AI bằng video, AI nhận internal presigned URL, không nhận MinIO access/secret key.

Lưu: file ở Object Storage; metadata ở `Videos`.

## 5. Tạo và Start Session
1. Chọn ClassSection + AttendancePolicy + đúng một source (Camera hoặc Video).
2. Backend kiểm tra class active, roster tồn tại, camera active/đúng room hoặc video READY.
3. Tạo `Sessions` trạng thái READY và snapshot roster `SessionStudents`.
4. Start: READY -> STARTING, tạo `AnalysisJob` STARTING.
5. Backend health-check AI, resolve RTSP/internal video URL và gọi `/jobs/start`.
6. Chỉ khi AI trả RUNNING: `AnalysisJob` và `Session` chuyển RUNNING, ghi `StartedAt`.
7. Lỗi start: job FAILED, Session quay về READY để retry; không báo RUNNING giả.

`RowVersion` trên Session giảm double-start đồng thời.

## 6. Tracking, identity, UNKNOWN và backfill
1. AI YOLO/MOT tạo track; Identity Resolver quản lý `stable_id` xuyên track/re-entry.
2. Backend tạo `StableIdentity` ngay cả khi `StudentId=null`.
3. Track history ghi `TrackSegments`; face/Re-ID evidence ghi `IdentityLinks`.
4. Candidate StudentId phải nằm trong `SessionStudents`.
5. Nếu confidence/margin chưa đủ: giữ UNKNOWN.
6. Một StudentId không được hai stable_id chưa CLOSED cùng claim; trường hợp này thành CONFLICT/DUPLICATE_CLAIM.
7. Khi UNKNOWN được xác minh thành StudentId: backfill BehaviorEvent/Alert trước đó cùng stable identity.
8. Backend không tự đổi Student A sang Student B chỉ vì score mới cao hơn. Việc sửa assignment cần explicit correction và vẫn không được tạo duplicate claim.

Lưu: `StableIdentities`, `TrackSegments`, `IdentityLinks`.

## 7. AI callback và idempotency
1. Mỗi callback có `event_id` duy nhất <=128 ký tự.
2. `AiEventReceipts` unique theo ExternalEventId chặn retry ghi lặp cho TRACK/IDENTITY/BEHAVIOR/ALERT.
3. Event chỉ nhận khi Session RUNNING/FINALIZING.
4. ObservationQuality được kiểm tra 0..1.

Lưu: `AiEventReceipts` cộng với bảng nghiệp vụ tương ứng.

## 8. HTBAM và BehaviorEvent
1. Mỗi stable_id có buffer segment riêng; không trộn dữ liệu giữa người.
2. HTBAM-X dùng causal context T=3: `[t-2,t-1,t] -> t`; mỗi segment 2 giây/16 frame ở 8 FPS, kết hợp MViT RGB embedding + 14 semantic features + pose + temporal mask. Temporal smoothing/EventEngine tạo segment trạng thái ổn định.
3. Backend nhận segment có `StartedAt < EndedAt`, label, probability, quality, model/threshold version.
4. 5 state timeline FOCUSED/DISTRACTED/SLEEPY/ACTIVE/OUT_OF_VIEW không được chồng lấn trên cùng stable_id.
5. `PHONE_USE` là signal/rule phụ có thể chồng state nên không dùng để cộng valid observed duration.

Lưu: `BehaviorEvents`. Không mặc định lưu từng frame/raw 14-feature nếu không cần nghiên cứu.

## 9. Alert
1. `AlertEngine` đọc `AlertRules` theo BehaviorLabel.
2. Chỉ tạo alert nếu duration, confidence và observation quality đều đạt rule.
3. External alert id được dẫn xuất từ behavior event + rule để chống duplicate.
4. Giảng viên ACK và ghi note; thao tác ghi AuditLog.
5. OUT_OF_VIEW là loại riêng, không tự diễn giải thành Distracted/Sleepy.

Lưu: `AlertRules`, `Alerts`, `AuditLogs`.

## 10. Realtime dashboard
1. Backend persist DB trước/đồng thời với event nghiệp vụ.
2. SignalR chỉ đẩy payload gọn: session status, identity/state/quality/alert; không đẩy raw video hoặc embedding.
3. Client mất SignalR sẽ reconnect; đồng thời fallback snapshot API định kỳ.
4. DB là source of truth, SignalR là kênh thông báo realtime.

## 11. Stop, finalize, attendance và report
1. RUNNING -> FINALIZING; AnalysisJob cũng FINALIZING.
2. Backend gọi AI stop; AI thật phải flush/close event đang mở trước khi trả thành công.
3. Backend đặt EndedAt rồi tổng hợp.
4. `PresentSeconds` = union các TrackSegment của Student trong khoảng Session, clamp <= session duration.
5. `ObservedSeconds` = union các state HTBAM hợp lệ FOCUSED/DISTRACTED/SLEEPY/ACTIVE; OUT_OF_VIEW và PHONE_USE không cộng.
6. `PresenceRatio = PresentSeconds / session duration`.
7. Behavior ratio = duration từng state / valid observed duration.
8. ObservationQuality tính có trọng số thời lượng.
9. Class summary tính weighted theo ObservedSeconds, tránh sinh viên gần như không quan sát được có trọng số ngang người đủ dữ liệu.
10. Tạo/cập nhật `Attendance`, `StudentSessionSummaries`, `ClassSessionSummaries`; đóng stable identity.
11. Thành công: Session/Job COMPLETED.
12. Nếu AI finalize hoặc aggregate lỗi: Session/Job `FINALIZE_FAILED`, giữ nguyên event đã persist và cho `retry-finalize`; không xóa dữ liệu.

Attendance không hard-code: Session tham chiếu `AttendancePolicy`. Policy seed mặc định dùng PRESENT >= 0.80, PARTIAL >= 0.50, nhưng Admin có thể cấu hình threshold/score/version mà không sửa code.

## 12. Phân vùng lưu trữ
| Dữ liệu | Nơi lưu | Lý do |
|---|---|---|
| User, role, student, roster, session | SQL Server | nghiệp vụ/quan hệ/transaction |
| Face image gốc | MinIO private | file lớn, quyền truy cập có hạn |
| Face template/embedding file | MinIO private | dữ liệu sinh trắc, không trả client |
| Metadata face/template | SQL Server | truy vết version/quality/active |
| Video upload | MinIO private | file lớn |
| Track/identity evidence | SQL Server | audit identity continuity |
| BehaviorEvent/Alert | SQL Server | timeline nghiệp vụ |
| Raw frame | Không lưu mặc định | giảm dung lượng và rủi ro riêng tư |
| Snapshot/evidence | Chỉ lưu nếu policy cho phép | cần retention/access control |
| Attendance/Summary | SQL Server | báo cáo chính thức |
| Realtime SignalR | Không phải storage | transport, DB mới là source of truth |

## 13. Khi model train xong
Không sửa Web/DB chính. Chỉ hiện thực `ProductionInferenceEngine` và các adapter bên `services/ai`:
- detector/tracker: YOLO + BoT-SORT;
- Face Enrollment/verification: ArcFace;
- Re-ID: OSNet;
- Stable Identity Resolver + session gallery;
- feature extractor 14 chiều;
- HTBAM GRU + Transformer + smoothing/EventEngine.

Giữ các contract `/capabilities`, `/jobs/start`, `/jobs/{id}/stop`, `/face-enrollments/start` và callback Backend. Mỗi event phải kèm stable_id, event_id, model_version, threshold_version và quality.
