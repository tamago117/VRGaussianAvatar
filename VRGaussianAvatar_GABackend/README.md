# VRGaussianAvatar - GA Backend

LHM-based Gaussian Avatar inference with a WebSocket streaming server.
The environment is managed with [uv](https://docs.astral.sh/uv/) (replacing the
previous manual `pip` + `venv` setup).

## Requirements

| Item | Requirement | Notes |
| --- | --- | --- |
| uv | 0.9+ | `powershell -ExecutionPolicy ByPass -c "irm https://astral.sh/uv/install.ps1 \| iex"` |
| Python | 3.10 | Fetched automatically by uv - no manual install needed |
| GPU | NVIDIA (torch is the CUDA 12.1 build) | |
| CUDA Toolkit | 12.1 | Only needed to build `diff-gaussian-rasterization` |
| MSVC | v14.38 toolset (VS 2022 17.8) | CUDA 12.1 rejects `_MSC_VER >= 1940` |

> The Python version is pinned to 3.10 in `.python-version`. LHM is only
> validated on 3.10, and pins such as `numpy==1.23.4` and `matplotlib==3.5.3`
> have no wheels for 3.11+.

## Setup

Open an **x64 Native Tools Command Prompt for VS** (see "Preparing stage 5"),
then:

```bat
cd path\to\VRGaussianAvatar\VRGaussianAvatar_GABackend
setup.bat
```

`setup.bat` runs five stages:

1. `uv python install 3.10`
2. `uv lock` - resolve dependencies
3. `uv sync --no-install-package ...` - install everything available as a wheel
   (torch cu121, pytorch3d, gsplat, ...)
4. `uv sync --no-install-package diff-gaussian-rasterization` - build
   `chumpy`, `basicsr` and `sam-2` against the torch installed in stage 3
5. `uv sync` - build `diff-gaussian-rasterization`

Running the stages by hand works the same way. Stage 5 is kept separate because
it is the only one that needs the CUDA Toolkit and MSVC.

### Preparing stage 5

Stage 5 compiles a CUDA extension, so two things have to be in place first.

1. **CUDA Toolkit 12.1** - install it and make sure `nvcc --version` works.
   Without it the build fails with
   `OSError: CUDA_HOME environment variable is not set`.
2. **An MSVC toolset CUDA 12.1 accepts.** `CUDA/v12.1/include/crt/host_config.h`
   errors out unless `1910 <= _MSC_VER < 1940`, so the toolset shipped with
   current Visual Studio is too new (v14.4x/v14.5x are `_MSC_VER` 194x/195x).
   v14.38 reports 19.38 and works. In the Visual Studio Installer, add the
   individual component
   *"MSVC v143 - VS 2022 C++ x64/x86 build tools (v14.38-17.8)"*, then open the
   developer prompt pinned to it:

   ```bat
   "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat" -vcvars_ver=14.38
   ```

Stages 1-4 need neither. Until stage 5 succeeds, every dependency except the
rasterizer is installed and importable, so only Gaussian rendering is blocked.

## Model weights

LHM's prior models (SMPL-X, the pose estimator, the VGG head detector and the
SAM2 checkpoint) are not in the repository and are not downloaded on demand.
Fetch them once:

```bat
fetch_weights.bat
```

This is the Windows equivalent of `LHM/download_weights.sh`. The archive is
**18.8 GB** and the OSS endpoint drops connections, so the download resumes:
if it is interrupted, just run the script again. It extracts into
`.\LHM\pretrained_models\` and is a no-op once that directory exists.
That path matters: `main_server_*.py` `chdir()`s into `.\LHM` before loading,
so the weight paths resolve relative to the submodule, not to this folder.

The avatar model itself (`LHM-500M-HF`, set in `CONFIG.MODEL_NAME`) is fetched
automatically on first start through Hugging Face or ModelScope.

## Running

```bat
uv run main_server_dual.py     :: stereo (two-eye) streaming
uv run main_server_single.py   :: single-view streaming
```

`uv run` picks up `.venv` automatically, so there is no `activate` step.

On a non-UTF-8 console (e.g. a Japanese Windows install, where the code page is
cp932) startup dies with `UnicodeEncodeError: 'cp932' codec can't encode
character '\U0001f680'` - the server logs emoji. Set the interpreter to UTF-8
first:

```bat
set PYTHONUTF8=1
uv run main_server_dual.py
```

First start takes a few minutes: it downloads `LHM-500M-HF` (3.7 GB) into
`LHM\pretrained_models\huggingface\`, then runs segmentation, pose estimation
and avatar reconstruction before uvicorn binds to port 8000. Watch for
`Application startup complete.`

### Protocol

`/ws` takes a JSON message per frame:

```json
{
  "cameras": [{"pos_x": 0, "pos_y": 0, "pos_z": -2.2,
               "rot_x": 0, "rot_y": 0, "rot_z": 0, "rot_w": 1, "eye": "left"}],
  "smplx": {"root_pose": [0,0,0], "trans": [0,0,0],
            "body_pose": [[0,0,0], "... 21 joints"],
            "lhand_pose": [[0,0,0], "... 15 joints"],
            "rhand_pose": [[0,0,0], "... 15 joints"]}
}
```

and replies with one binary frame per camera: a 1-byte eye header
(`0x00` left, `0x01` right) followed by a JPEG. Camera poses are Unity's
left-handed Y-up convention - `create_c2w_matrix` converts them.

## Smoke test

With the server running:

```bat
uv run smoke_test.py
```

It opens `/ws`, renders 10 stereo pairs, reports the round-trip rate, checks the
two eyes actually differ (parallax) and that the frame is not black, and writes
the last pair to `.\smoke_out\`. A reference run on an RTX 4060 Laptop:

```
10 stereo pairs | 370.0 ms round-trip -> 2.7 pairs/s (5.4 eye-frames/s)
stereo parallax: -10 px horizontal shift between eyes
```

The measured shift matches `fx * IPD / Z` = `402 * 0.055 / 2.2` = 10.05 px.
Note that 8 GB of VRAM is almost fully consumed (7.8 GB) by LHM-500M plus
Sapiens, SAM2 and BiRefNet, which caps the frame rate.

## Dependency design notes

Everything lives in `pyproject.toml`. The parts worth knowing:

- **torch 2.3.0 + cu121** - pulled from the `pytorch-cu121` entry in
  `[[tool.uv.index]]`. It is the only combination LHM is validated against, and
  the prebuilt wheels below are matched to it.
- **pytorch3d / gsplat** - CUDA extensions, but prebuilt wheels exist for
  torch 2.3.0 + cu121 + cp310, so **no local build is required**.
  - `pytorch3d==0.7.8+pt2.3.0cu121` from `https://miropsota.github.io/torch_packages_builder`
  - `gsplat==1.4.0+pt23cu121` from `https://docs.gsplat.studio/whl/pt23cu121`
- **diff-gaussian-rasterization** - **ashawkey's fork is mandatory.**
  LHM's `gs_renderer.py` expects a 4-tuple `(color, radii, depth, alpha)`,
  while upstream graphdeco returns 3 values and takes an `antialiasing` setting.
  The prebuilt wheels are built from upstream, so this one package must be
  compiled from source.
- **chumpy / basicsr / sam-2** - the PyPI releases are unusable, so they come
  from git. All of them import torch at build time, which is why they are listed
  in `no-build-isolation-package`: an isolated build env would pull in a CPU-only
  torch.
- **`[[tool.uv.dependency-metadata]]`** - those same four packages need torch
  merely to *read* their metadata, which breaks `uv lock`. Their setup.py
  contents are declared up front to avoid that.
- **fastapi / uvicorn / pydantic / websockets** - server dependencies of this
  repository; they are not part of LHM's own requirements.
- **Windows fixes on top of LHM's list**
  - `megfile` is bumped to 5.x. The pinned 4.1.0.post2 imports `fcntl`, so it
    cannot be imported on Windows at all.
  - `onnxruntime` is added. rembg keeps it behind an extra, but LHM imports
    rembg unconditionally as the SAM2 fallback.
  - `pip` is added because chumpy's setup.py imports `pip._internal`, and it is
    built without isolation.
- **`modelscope` / `gputil` / `imagehash`** - LHM `pip install`s these at import
  time when they are missing (see `LHM/utils/model_download_utils.py`,
  `gpu_utils.py`, `video_utils.py`). Declaring them keeps the environment
  reproducible; otherwise `uv sync` prunes whatever those runtime installs left
  behind and the next server start reinstalls them.
- `dna` and `spaces` were dropped - nothing in LHM references them.

## Client (Unity)

Unity Native WebSocket: https://github.com/endel/NativeWebSocket

Package Manager -> Add package from git URL:

```
https://github.com/endel/NativeWebSocket.git#upm
```
