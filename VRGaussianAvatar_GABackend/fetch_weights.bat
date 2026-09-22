@echo off
setlocal
cd /d "%~dp0"

rem ---------------------------------------------------------------------------
rem Downloads and extracts LHM's prior models into .\LHM\pretrained_models\.
rem That location matters: main_server_*.py chdir()s into .\LHM before loading,
rem so every weight path resolves relative to the submodule, not to this folder.
rem Replaces LHM/download_weights.sh, which is wget/tar shell script.
rem
rem The archive is ~18.8 GB and the OSS endpoint drops connections, so the
rem download is resumable: re-run this script and it picks up where it stopped.
rem ---------------------------------------------------------------------------

rem The upstream OSS endpoint (virutalbuy-public.oss-cn-hangzhou.aliyuncs.com)
rem serves this at ~20 KB/s from here. Hugging Face mirrors the identical
rem archive (same 18818365440 bytes) at ~9 MB/s, so that is the default.
set "URL=https://huggingface.co/3DAIGC/LHM/resolve/main/LHM_prior_model.tar"
set "ARCHIVE=LHM_prior_model.tar"
set "EXPECTED=18818365440"

if exist "LHM\pretrained_models\human_model_files" (
    echo pretrained_models already present - nothing to do.
    exit /b 0
)

:download
if exist "%ARCHIVE%" (
    for %%A in ("%ARCHIVE%") do set "HAVE=%%~zA"
) else (
    set "HAVE=0"
)
if "%HAVE%"=="%EXPECTED%" goto extract

echo Downloading %ARCHIVE% ... (resumable, Ctrl+C is safe)
curl -L -C - --retry 20 --retry-delay 10 --retry-all-errors --progress-bar -o "%ARCHIVE%" "%URL%"
goto download

:extract
echo Extracting %ARCHIVE% ...
tar -xf "%ARCHIVE%" -C LHM
if errorlevel 1 (
    echo [ERROR] Extraction failed. The archive may be corrupt - delete it and re-run.
    exit /b 1
)

echo Done. Weights are in .\LHM\pretrained_models\
echo You can now delete %ARCHIVE% to reclaim 18.8 GB.
