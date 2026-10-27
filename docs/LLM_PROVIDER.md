# LLM Provider cho HTBAM

LLM chỉ diễn giải dữ liệu tổng hợp đã có trong SQL Server thành nhận xét tiếng Việt. LLM không phân tích video, không nhận diện sinh viên, không xác định nhãn hành vi, không thay đổi `BehaviorEvent`/`Attendance` và không thay thế HTBAM.

## A. Model hiện tại

- Provider: Google Gemini
- Model: `gemini-3.5-flash-lite`
- SDK backend: `Google.GenAI`
- Fallback: `RULE_BASED_FALLBACK`

## VỊ TRÍ THAY GEMINI API KEY THẬT

=========================================================
VỊ TRÍ THAY GEMINI API KEY THẬT
=========================================================

File: `infra/.env`

Dòng cần sửa:

```dotenv
GEMINI_API_KEY=PASTE_YOUR_REAL_GEMINI_API_KEY_HERE
```

Ví dụ định dạng (không phải key thật):

```dotenv
GEMINI_API_KEY=xxxxxxxxxxxxxxxxxxxxxxxx
```

Tạo file bằng cách sao chép `infra/.env.example` thành `infra/.env`. Không điền key vào `appsettings.json`, `appsettings.Development.json`, `docker-compose.yml`, React `.env`, mã TypeScript hoặc Git repository.

## B. Đổi sang model Gemini khác

Chỉ sửa trong `infra/.env`:

```dotenv
GEMINI_MODEL=<MODEL_ID_MOI>
```

Hoặc sửa `Llm:Model` trong `services/backend/src/HTBAM.Api/appsettings.json`:

```json
"Llm": {
  "Model": "<MODEL_ID_MOI>"
}
```

Biến môi trường `GEMINI_MODEL` được ưu tiên hơn `Llm:Model`. Nếu API/SDK vẫn tương thích thì không sửa `ReportsController`, `AutoCommentService`, database, frontend hoặc business logic report.

## C. Thêm provider khác

Ví dụ OpenAI:

1. Tạo `OpenAiLlmProvider : ILlmProvider` trong Infrastructure.
2. Đăng ký provider với DI và thêm nhánh vào `LlmProviderResolver`.
3. Đặt `Llm:Provider=OPENAI`.
4. Đưa secret vào biến môi trường backend, ví dụ `OPENAI_API_KEY`.

Không sửa `ReportsController` hoặc `AutoCommentService`.

## D. Dùng local model

Tạo `LocalLlmProvider : ILlmProvider` để gọi Ollama, vLLM hoặc inference server nội bộ; đăng ký provider rồi đặt `Llm:Provider=LOCAL`.

## Cấu hình

```json
"Llm": {
  "Enabled": true,
  "Provider": "GEMINI",
  "Model": "gemini-3.5-flash-lite",
  "TimeoutSeconds": 45,
  "Temperature": 0.2,
  "MaxOutputTokens": 500
}
```

Thứ tự đọc key là `GEMINI_API_KEY`, sau đó mới tới `GOOGLE_API_KEY`. Key chỉ được đọc trong backend và không bao giờ được trả về API.

Với database đã tạo từ schema cũ, chạy một lần `database/sqlserver/004_add_llm_comment_metadata.sql`. Cài mới hoàn toàn chỉ cần dùng `database/sqlserver/001_schema.sql` đã cập nhật.

## API

- `GET /api/llm/status`: trạng thái cấu hình, không gọi Gemini và không trả secret.
- `POST /api/llm/test`: kiểm tra kết nối thật; chỉ `ADMIN`/`TECH_AI`, có timeout, không trả secret.
- `POST /api/reports/sessions/{id}/auto-comment`: nhận xét buổi học.
- `POST /api/reports/sessions/{sessionId}/students/{studentId}/auto-comment`: nhận xét sinh viên trong buổi học.
- `POST /api/reports/class-sections/{id}/auto-comment`: nhận xét tổng hợp lớp học phần.

## Fallback và dữ liệu gửi đi

Khi LLM bị tắt, thiếu key, provider không hỗ trợ hoặc Gemini lỗi, `AutoCommentService` dùng rule-based và lưu provider là `RULE_BASED_FALLBACK`. Gemini chỉ nhận mã đối tượng cần thiết, các tỷ lệ Focused/Distracted/Sleepy/Active, số cảnh báo, chất lượng quan sát và dữ liệu chuyên cần tổng hợp nếu có. Ảnh, video, FaceEmbedding, raw biometric data, RTSP URL, password và email không được gửi.
