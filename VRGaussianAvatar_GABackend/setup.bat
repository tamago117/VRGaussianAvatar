@echo off
setlocal
cd /d "%~dp0"

rem ---------------------------------------------------------------------------
rem VRGaussianAvatar GA Backend - environment setup (uv)
rem
rem Steps [1]-[4] run anywhere. Step [5] compiles a CUDA extension and needs the
rem CUDA Toolkit 12.1 plus an MSVC toolset CUDA 12.1 accepts (v14.38); start
rem this script from the matching developer prompt if you want it to succeed.
rem See README.md -> "Preparing stage 5".
rem ---------------------------------------------------------------------------

where uv >nul 2>&1
if errorlevel 1 (
    echo [ERROR] uv is not installed.
    echo         powershell -ExecutionPolicy ByPass -c "irm https://astral.sh/uv/install.ps1 ^| iex"
    exit /b 1
)

echo [1/5] Installing the Python 3.10 toolchain...
uv python install 3.10
if errorlevel 1 exit /b 1

echo [2/5] Resolving dependencies...
uv lock
if errorlevel 1 exit /b 1

echo [3/5] Installing wheel-based dependencies (torch cu121, pytorch3d, gsplat, ...)
rem The four deferred packages build against the *installed* torch, so they
rem cannot be part of this pass.
uv sync ^
    --no-install-package diff-gaussian-rasterization ^
    --no-install-package chumpy ^
    --no-install-package basicsr ^
    --no-install-package sam-2
if errorlevel 1 exit /b 1

echo [4/5] Building chumpy / basicsr / sam-2 from source...
uv sync --no-install-package diff-gaussian-rasterization
if errorlevel 1 exit /b 1

echo [5/5] Building diff-gaussian-rasterization (CUDA)...
set DISTUTILS_USE_SDK=1
uv sync
if errorlevel 1 (
    echo.
    echo [ERROR] Step 5 failed. It compiles a CUDA extension and needs:
    echo         - CUDA Toolkit 12.1  ^(nvcc on PATH / CUDA_HOME set^)
    echo         - MSVC v14.38 toolset ^(CUDA 12.1 rejects newer ones^)
    echo         - this script started from the matching developer prompt
    echo.
    echo         Everything else is installed; only Gaussian rasterization
    echo         is unavailable until this step succeeds.
    exit /b 1
)

echo.
echo Done.  Start the server with:   uv run main_server_dual.py
