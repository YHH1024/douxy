@echo off
setlocal EnableExtensions EnableDelayedExpansion
chcp 65001 >nul
title Paraformer ASR Web
cd /d "%~dp0"

set "PORT=8010"
set "URL=http://127.0.0.1:%PORT%"
set "PYTHON_EXE="
set "PYTHON_HINT_FILE=%~dp0python.local.txt"

REM Priority 1: explicit environment variable
if not "%ASR_PYTHON%"=="" if exist "%ASR_PYTHON%" set "PYTHON_EXE=%ASR_PYTHON%"

REM Priority 2: local override file, first line is python.exe full path
if "%PYTHON_EXE%"=="" if exist "%PYTHON_HINT_FILE%" (
  set /p PYTHON_EXE=<"%PYTHON_HINT_FILE%"
  if not exist "!PYTHON_EXE!" set "PYTHON_EXE="
)

REM Priority 3: legacy conda env path
if "%PYTHON_EXE%"=="" if exist "C:\Users\admin\miniconda3\envs\asr\python.exe" (
  set "PYTHON_EXE=C:\Users\admin\miniconda3\envs\asr\python.exe"
)

REM Priority 4: python in PATH
if "%PYTHON_EXE%"=="" (
  for /f "delims=" %%i in ('where python 2^>nul') do (
    if "%PYTHON_EXE%"=="" set "PYTHON_EXE=%%i"
  )
)

if "%PYTHON_EXE%"=="" goto no_python

REM --- Is the port already in use? (service already running) ---
netstat -ano -p tcp | findstr /C:"LISTENING" | findstr /C:":%PORT% " >nul
if not errorlevel 1 goto alreadyrunning

echo ============================================================
echo   Paraformer ASR Web Service
echo   URL: %URL%
echo   Python: %PYTHON_EXE%
echo   Close this window to stop the service.
echo ============================================================
echo.

"%PYTHON_EXE%" -c "import fastapi,uvicorn,torch,pynvml,funasr" 1>nul 2>nul
if errorlevel 1 goto missing_dep

REM open browser after model load delay
start "" /min cmd /c "timeout /t 8 /nobreak >nul & start %URL%"

"%PYTHON_EXE%" -m uvicorn app:app --host 127.0.0.1 --port %PORT%
if errorlevel 1 echo [FAILED] service stopped unexpectedly. Check Python env, ffmpeg, GPU and model files.

echo.
echo Service stopped. Press any key to close this window.
pause
exit /b

:alreadyrunning
echo ============================================================
echo   Service is already running on port %PORT%.
echo   Opening browser: %URL%
echo ============================================================
start "" %URL%
timeout /t 3 >nul
exit /b 0

:missing_dep
echo [FAILED] Python environment is missing required packages.
echo.
echo Required packages:
echo   fastapi
echo   uvicorn
echo   torch
echo   pynvml
echo   funasr
echo.
echo Current Python:
echo   %PYTHON_EXE%
echo.
echo You can fix this in either way:
echo   1. Set environment variable ASR_PYTHON to your python.exe
echo   2. Create file python.local.txt next to this bat and put python.exe full path on first line
echo.
pause
exit /b 1

:no_python
echo [FAILED] No usable python.exe was found.
echo.
echo You can fix this in either way:
echo   1. Set environment variable ASR_PYTHON to your python.exe
echo   2. Create file python.local.txt next to this bat and put python.exe full path on first line
echo.
pause
exit /b 1
