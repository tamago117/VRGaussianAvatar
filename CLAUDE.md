# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A VR system for 3D Gaussian Splatting avatars, split across two projects that talk over a
WebSocket:

- `VRGaussianAvatar_GABackend/` — Python. Reconstructs an avatar from a single image with
  [LHM](https://github.com/aigc3d/LHM) (a git submodule) and renders it per frame from
  camera poses the client sends.
- `VRGaussianAvatar_VRFrontend/` — Unity 6000.1.13f1. Reads HMD/controller poses, streams
  them to the backend, and blits the returned JPEGs onto per-eye materials.

The backend must be running before the Unity project is started.

## Backend commands

Run these from `VRGaussianAvatar_GABackend/`. The environment is a uv project; there is no
`activate` step.

```bat
setup.bat                       :: full environment bootstrap (5 stages)
fetch_weights.bat               :: LHM prior models, 18.8 GB, resumable

set PYTHONUTF8=1
uv run main_server_dual.py      :: stereo streaming server on :8000
uv run smoke_test.py            :: round-trip check against a running server
```

`smoke_test.py` is the only test in the repo. It renders 10 stereo pairs, asserts the two
eyes differ (parallax) and that frames are not black, and writes the last pair to
`smoke_out/`.

### Environment gotchas

- **`PYTHONUTF8=1` is required on a non-UTF-8 console.** The server logs emoji; on a
  Japanese Windows install (cp932) startup dies with `UnicodeEncodeError` before any model
  loads.
- **Python is pinned to 3.10** (`.python-version`). LHM is only validated there and pins
  like `numpy==1.23.4` have no 3.11+ wheels. Do not relax this casually.
- **`uv sync` runs in two passes** (see `setup.bat`). `chumpy`, `basicsr`, `sam-2` and
  `diff-gaussian-rasterization` import torch at build time and are listed in
  `no-build-isolation-package`, so torch has to be installed before they build. Their
  metadata is declared in `[[tool.uv.dependency-metadata]]` so `uv lock` does not need
  torch just to read it — if you add another package like this, declare its metadata too,
  and make the declared version match what the build actually produces or `uv sync` will
  reinstall it every run.
- **Stage 5 of `setup.bat` needs CUDA Toolkit 12.1 and MSVC v14.38.**
  `CUDA/v12.1/include/crt/host_config.h` errors unless `1910 <= _MSC_VER < 1940`, so
  current default toolsets (14.4x/14.5x) are rejected. Everything except Gaussian
  rasterization works without this.
- LHM `pip install`s `modelscope`, `GPUtil` and `imagehash` at import time if they are
  missing (`LHM/utils/model_download_utils.py`, `gpu_utils.py`, `video_utils.py`). They
  are declared in `pyproject.toml` so `uv sync` does not fight those runtime installs.

## Backend architecture

### Request flow

`main_server_dual.py` serves one endpoint, `/ws`. Per frame the client sends JSON
(`VRAvatarBatch`: a list of `CameraData` plus one `SmplxData` pose) and the server replies
with **one binary message per camera**: a 1-byte eye header (`0x00` left, `0x01` right)
followed by a JPEG.

Camera poses arrive in Unity's left-handed Y-up convention; `create_c2w_matrix()` converts
them and applies the per-eye IPD offset.

Note that the Unity client (`VRStreamIKManager.cs`) has a `useDualPack` toggle for a
different wire format — a `"VR"` magic header with length-prefixed frames, from a
`server_dualpack.py` that is **not in this repository**. Against `main_server_dual.py` that
toggle must be off.

### Startup pipeline

`load_models_and_avatar()` **`os.chdir()`s into `./LHM`** and only restores the cwd at the
end. Every weight path in the config is relative to the submodule, which is why
`fetch_weights.bat` extracts into `LHM/pretrained_models/` and not the backend root. This
is the single easiest thing to get wrong — a misplaced extraction surfaces as
`ValueError: Unknown model type human` from `smplx.create`, not as a missing-file error.

The startup sequence is: segment the target image (SAM2, falling back to rembg), estimate
SMPL-X pose, detect the face, reconstruct the avatar with `infer_single_view()`, then bind
uvicorn. It takes several minutes and holds the GPU the whole time. `LHM-500M-HF` (3.7 GB)
is downloaded on first run into `LHM/pretrained_models/huggingface/`.

`CONFIG` at the top of the server file holds the input image, model name, render size, IPD
and FOV. `main_server_single.py` still points at `p_female_13_source.jpg`, which does not
exist at the pinned LHM revision — fix that path before using it.

### Why the dependency pins look the way they do

- **torch 2.3.0+cu121** from the `pytorch-cu121` index. `pytorch3d` and `gsplat` are
  prebuilt wheels matched to exactly that build, so neither needs a local CUDA compile.
- **`diff-gaussian-rasterization` must be ashawkey's fork.** LHM's `gs_renderer.py` unpacks
  a 4-tuple `(color, radii, depth, alpha)`; upstream graphdeco returns 3 values and takes an
  `antialiasing` setting. The prebuilt wheels on public indexes are built from upstream and
  are **not** compatible. This is the one package that has to compile locally.
- `megfile` is bumped past LHM's pin because 4.1 imports `fcntl` and cannot load on Windows;
  `onnxruntime` is added because rembg hides it behind an extra that LHM does not use; `pip`
  is added because chumpy's `setup.py` imports `pip._internal`.

## Hardware

Resident weights are **8.2 GB in fp32** — Sapiens 1B (4.46 GB, loaded separately as LHM's
fine encoder per `LHM/configs/inference/human-lrm-500M.yaml`) plus LHM-500M-HF (3.75 GB).
Startup adds roughly 2.3 GB more for SAM2, BiRefNet and the head detector, which are never
released with `torch.cuda.empty_cache()`.

An 8 GB card does not fit this. Measured on an RTX 4060 Laptop (8 GB): VRAM saturated at
7.8 GB, the process working set spilled to 13.6 GB of system memory, and throughput was
2.7 stereo pairs/s. 16 GB is the practical minimum; more is better.

## Assets that are not in git

| Path | Size | Source |
| --- | --- | --- |
| `VRGaussianAvatar_VRFrontend/Assets/VRGA/Scene/C1_Self_Client.unity` | 1.08 GB | Google Drive link in the root README |
| `VRGaussianAvatar_VRFrontend/Assets/VRGA/Scene/SMPLX-Unity/` | 333 MB | https://smpl-x.is.tue.mpg.de/ |
| `VRGaussianAvatar_GABackend/LHM/pretrained_models/` | 18 GB | `fetch_weights.bat` |

The scene is over GitHub's 100 MB file limit: 1.076 GB of its 1.08 GB is a single Mesh
serialized inline. Extracting that Mesh into its own `.asset` would make the scene
committable.

`Packages/com.unity.inputsystem/` and `Packages/com.unity.timeline/` are also ignored —
they are embedded copies of registry packages at the versions `manifest.json` already pins,
so Unity resolves them on a fresh clone.

## Repository notes

`VRGaussianAvatar_GABackend/LHM` is a submodule pinned to `b31e76a`; clone with
`--recurse-submodules`. Treat it as upstream code — prefer working around its quirks in the
server rather than patching it.

The root `README.md` still describes the old pip/venv flow and references `install.bat`,
which no longer exists. `VRGaussianAvatar_GABackend/README.md` is the current one.
