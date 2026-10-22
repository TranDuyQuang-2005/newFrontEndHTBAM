> **v3.1 build fix (2026-08-28):** added `Microsoft.Extensions.Configuration.Binder` to `HTBAM.Infrastructure` so Docker/.NET 9 can compile `IConfiguration.GetValue(...)`. No DB schema change.

# HTBAM Web Management System v3-complete

Bộ source nền chính thức cho KLCN **Hệ thống quản lý và phân tích hành vi sinh viên trong lớp học theo thời gian thực**.

## Phạm vi bản này
Bản này hoàn thiện các phần **không phụ thuộc model đã train**: Web Angular, ASP.NET Core API, SQL Server schema, JWT/RBAC, quản lý danh mục, sinh viên, roster, camera/video, Face Enrollment storage, Session lifecycle, realtime contract/SignalR, stable identity storage, alert/rule, attendance policy, history, reports, Excel/PDF, search, audit và UI responsive.

Các khối AI/CV production vẫn cố ý ở trạng thái contract-only vì model chưa train/tích hợp: YOLO, BoT-SORT, ArcFace, OSNet, Stable Identity Resolver, MediaPipe feature extractor, HTBAM (GRU + Transformer), smoothing và KMeans/Intelligent KMeans/pseudo-label pipeline. LLM Provider là module backend độc lập và đã được tích hợp để diễn giải summary có sẵn; hệ thống **không sinh dữ liệu AI/CV giả**.

Flutter chưa nằm trong phạm vi hiện tại theo yêu cầu triển khai hiện tại; Backend API được giữ reusable để thêm Flutter sau.

## Stack
- Web: Angular + TypeScript
- Backend: ASP.NET Core Web API (.NET 9), EF Core, SignalR
- Database: SQL Server
- Object storage: MinIO private bucket
- AI service contract: Python + FastAPI
- Auth: JWT + ASP.NET PasswordHasher + RBAC
- Export: ClosedXML + QuestPDF
- Infra: Docker Compose

## Cấu trúc
```text
apps/web/                 React UI
services/backend/         Domain/Application/Infrastructure/API
services/ai/              FastAPI contract + integration slots
  app/detection/          YOLO (pending model)
  app/tracking/           BoT-SORT (pending model)
  app/identity/           ArcFace/OSNet/stable resolver (pending model)
  app/features/           MediaPipe/OpenCV feature extraction (pending model)
  app/htbam/              GRU + Transformer inference (pending model)
  app/clustering/         KMeans/Intelligent KMeans research pipeline
  app/weak_labeling/      pseudo-label research pipeline
  app/events/             production event engine slot
  app/inference/          production orchestrator slot
database/                 fresh SQL schema/seed/sample
database/legacy/          upgrade script cũ, KHÔNG dùng cho cài mới
infra/                    Docker Compose
docs/                     workflows, requirements, integration, runbook
tests/                    static/contract/workflow/requirements checks
```

## Database khi bạn CHƯA từng chạy v1/v2
Chỉ chạy theo thứ tự:
1. `database/001_schema.sql`
2. `database/002_seed.sql`
3. `database/003_dev_sample.sql` — tùy chọn, nên dùng khi test

**Không chạy `database/legacy/004_upgrade_v1_to_v2.sql`.**

## Tài khoản Development Seeder
Khi backend chạy `Development` với `Seed:Development=true`:
- `admin / Admin@123456`
- `lecturer / Lecturer@123456`

Đổi mật khẩu sau lần đăng nhập đầu nếu dùng ngoài demo.

## Trạng thái AI hiện tại
`GET /capabilities` ở AI service trả:
```json
{"sessionInference":false,"faceEnrollment":false,"loadedModels":[]}
```
Do đó:
- Có thể upload/quản lý ảnh Face Enrollment nhưng dừng tại `PENDING_AI`.
- Có thể tạo/cấu hình Session nhưng backend không cho chuyển `RUNNING` nếu model inference chưa sẵn sàng.
- Không có embedding/prediction/StudentId/behavior giả.

Xem `docs/FEATURE_MATRIX.md`, `docs/WORKFLOWS_AND_STORAGE.md`, `docs/MODEL_INTEGRATION.md`, `docs/RUNBOOK.md` và `docs/VALIDATION_REPORT.md`.

## Google Gemini LLM Provider

Backend dùng Google Gemini qua abstraction `ILlmProvider`; model mặc định là `gemini-2.5-pro`. Angular không gọi Gemini trực tiếp. Khi Gemini tắt, thiếu cấu hình hoặc gặp lỗi, report vẫn sinh nhận xét bằng `RULE_BASED_FALLBACK`.

=========================================================
VỊ TRÍ THAY GEMINI API KEY THẬT
=========================================================

1. Sao chép `infra/.env.example` thành `infra/.env`.
2. Chỉ thay dòng sau trong `infra/.env`:

```dotenv
GEMINI_API_KEY=PASTE_YOUR_REAL_GEMINI_API_KEY_HERE
```

Ví dụ định dạng: `GEMINI_API_KEY=xxxxxxxxxxxxxxxxxxxxxxxx` (không phải key thật).

Không điền key vào `appsettings.json`, `appsettings.Development.json`, `docker-compose.yml`, React `.env`, TypeScript source hoặc Git repository. Muốn đổi model, đặt `GEMINI_MODEL=<MODEL_ID_MOI>` trong `infra/.env`; không cần sửa code nếu API/SDK còn tương thích. Xem hướng dẫn đầy đủ tại `docs/LLM_PROVIDER.md`.
