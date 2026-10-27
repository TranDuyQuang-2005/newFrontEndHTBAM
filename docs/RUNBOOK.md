# Runbook HTBAM

## Khởi động trên Windows bằng Docker

Từ thư mục gốc repository:

```powershell
Copy-Item infra/.env.example infra/.env   # chỉ khi chưa có .env; không ghi đè file hiện có
docker volume inspect infra_htbam-sql
docker compose -f infra/docker-compose.yml config --quiet
docker compose -f infra/docker-compose.yml build ai backend web migrate seed
docker compose -f infra/docker-compose.yml up -d
docker compose -f infra/docker-compose.yml ps -a
```

Có thể dùng `RUN_HTBAM_FULL_WEB.bat` cho cùng quy trình. Không cần và không nên dùng `--no-cache` cho AI khi chỉ cập nhật học vụ.

Volume SQL `infra_htbam-sql` là volume ngoài (external) để giữ database hiện có. Không chạy `docker compose down -v`, không xóa volume và không đổi `MSSQL_SA_PASSWORD` nếu volume đang dùng mật khẩu cũ.

Thứ tự tự động là: SQL healthy → `migrate` hoàn tất → `seed` hoàn tất → backend healthy → web. Runner áp dụng tuần tự migration `database/sqlserver/001..013`, lưu checksum trong `SchemaMigrations`, rollback migration lỗi và từ chối nội dung đã bị sửa sau khi áp dụng. Database cũ chưa có bảng lịch sử chỉ được nhận baseline 001 khi toàn bộ bảng baseline đã hiện diện; schema dở dang sẽ dừng với lỗi thay vì tự ghi nhận.

Development seed mặc định tắt. Chỉ bật rõ ràng trong môi trường Development:

```text
ASPNETCORE_ENVIRONMENT=Development
Seed__Development=true
SEED_DEFAULT_PASSWORD=<mật khẩu dành cho tài khoản demo mới>
```

Seed không reset tài khoản, mật khẩu hoặc dữ liệu nghiệp vụ đã có. Tắt seed không xóa dữ liệu demo đã tạo.

## Chạy migration/seed riêng

Từ thư mục gốc:

```powershell
docker compose -f infra/docker-compose.yml run --rm --no-deps migrate
docker compose -f infra/docker-compose.yml run --rm --no-deps seed
docker compose -f infra/docker-compose.yml logs --tail=200 migrate seed backend
```

Ngoài Docker, đặt connection string rồi chạy từ project API:

```powershell
dotnet run --project services/backend/src/HTBAM.Api -- --migrate
dotnet run --project services/backend/src/HTBAM.Api -- --seed
dotnet run --project services/backend/src/HTBAM.Api
```

`database/sqlserver` là nguồn migration chuẩn. Kiểm tra bản mirror ở `database/`:

```powershell
node database/sync-sqlserver.cjs --check
```

Không sửa migration đã áp dụng; mọi thay đổi schema tiếp theo phải là migration tiến tới mới.

## Kiểm tra sau khởi động

```powershell
curl.exe http://localhost:8001/health
curl.exe http://localhost:8001/capabilities
curl.exe http://localhost:5000/health
```

- Web: `http://127.0.0.1:5173`
- Backend: `http://127.0.0.1:5000`
- AI: `http://127.0.0.1:8001`
- MinIO console: `http://127.0.0.1:9001`
- SQL Server: `localhost:1433`

Kiểm thử thủ công tối thiểu: đăng nhập theo từng vai trò; cấu hình học kỳ/bảng giờ/mốc tuần; tạo hoặc import lớp và xem preview; lưu lịch để sinh buổi; chọn đúng một nguồn video, camera RTSP hoặc camera BROWSER; chạy start/stop/finalize; kiểm tra dashboard, overlay/chunk; và xác nhận đăng ký khuôn mặt vẫn tạo/upload/preview/xóa/hủy/xử lý template được.

## Build và test cục bộ

```powershell
dotnet build services/backend/src/HTBAM.Api/HTBAM.Api.csproj --configuration Release
dotnet test services/backend/tests/HTBAM.Tests/HTBAM.Tests.csproj
Push-Location apps/web
npm.cmd test
npm.cmd run build
Pop-Location
```

Các SQL integration test cần `HTBAM_TEST_SQL_CONNECTION` trỏ đến SQL Server dùng cho kiểm thử và tự tạo/xóa database cách ly. Không trỏ biến này vào database thật. Test workbook HUIT đầy đủ chỉ chạy khi đặt `HTBAM_HUIT_WORKBOOK` tới file nguồn thật.

Xem thêm [cơ chế migration và development seed](DATABASE_MIGRATION_AND_DEVELOPMENT_SEED.md) và [triển khai lịch học](ACADEMIC_SCHEDULING_IMPLEMENTATION.md).
