@echo off
setlocal
cd /d E:\newFrontEndHTBAM

echo ============================================================
echo HTBAM - BUILD + RUN FULL WEB
echo ============================================================

docker compose -f infra\docker-compose.yml build ai backend web
if errorlevel 1 goto :fail

docker compose -f infra\docker-compose.yml up -d
if errorlevel 1 goto :fail

echo.
docker compose -f infra\docker-compose.yml ps

echo.
echo AI health:
curl http://localhost:8001/health
echo.
echo Backend health:
curl http://localhost:5000/health
echo.

echo.
echo Web      : http://127.0.0.1:5173
echo Backend  : http://127.0.0.1:5000
echo AI       : http://127.0.0.1:8001
echo MinIO    : http://127.0.0.1:9001
echo SQL      : localhost:1433
echo.
echo Dev accounts:
echo admin    / Admin@123456
echo lecturer / Lecturer@123456
echo.

start "" http://127.0.0.1:5173
start "" http://127.0.0.1:8001/health
goto :eof

:fail
echo.
echo BUILD/RUN FAILED.
echo docker compose -f infra\docker-compose.yml logs --tail=150 ai
echo docker compose -f infra\docker-compose.yml logs --tail=150 backend
exit /b 1
