@echo off
setlocal
cd /d E:\newFrontEndHTBAM

echo ============================================================
echo HTBAM FULL PIPELINE BUILD
echo ============================================================
echo IMPORTANT: Docker cache is used. --no-cache is NOT used.
echo.

docker compose -f infra\docker-compose.yml build ai backend web
if errorlevel 1 goto :fail

docker compose -f infra\docker-compose.yml up -d --force-recreate ai backend web
if errorlevel 1 goto :fail

docker compose -f infra\docker-compose.yml ps
echo.
curl http://localhost:8001/health
echo.
curl http://localhost:8001/capabilities
echo.
curl http://localhost:5000/health
echo.
start "" http://127.0.0.1:5173
goto :eof

:fail
echo.
echo BUILD FAILED.
echo Check disk space, then inspect:
echo docker compose -f infra\docker-compose.yml logs --tail=200 ai
echo docker compose -f infra\docker-compose.yml logs --tail=200 backend
exit /b 1
