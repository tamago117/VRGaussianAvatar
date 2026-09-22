"""Round-trip smoke test for the streaming server.

Start the server first, then run:

    uv run smoke_test.py

Connects to /ws, renders a stereo pair per frame, checks the replies really are
two different views, and writes the last pair to ./smoke_out/.
"""

import argparse
import asyncio
import io
import json
import pathlib
import time

import numpy as np
import websockets
from PIL import Image

OUT_DIR = pathlib.Path(__file__).with_name("smoke_out")


def build_frame(pos=(0.0, 0.0, -2.2), rot=(0.0, 0.0, 1.0, 0.0)):
    """One request. `rot` is a Unity-convention quaternion (x, y, z, w).

    The default rolls 180 degrees about Z, which cancels the Y flip in
    create_c2w_matrix() and gives an upright view for this test. A real client
    sends the headset pose instead.
    """

    def camera(eye):
        return {
            "pos_x": pos[0], "pos_y": pos[1], "pos_z": pos[2],
            "rot_x": rot[0], "rot_y": rot[1], "rot_z": rot[2], "rot_w": rot[3],
            "eye": eye,
        }

    return {
        "cameras": [camera("left"), camera("right")],
        "smplx": {
            "root_pose": [0.0, 0.0, 0.0],
            "trans": [0.0, 0.0, 0.0],
            "body_pose": [[0.0, 0.0, 0.0] for _ in range(21)],
            "lhand_pose": [[0.0, 0.0, 0.0] for _ in range(15)],
            "rhand_pose": [[0.0, 0.0, 0.0] for _ in range(15)],
        },
    }


CONNECT_HELP = """\
Could not connect to {url}: {exc}

This script is only the client - start the server first, in another terminal:

    set PYTHONUTF8=1
    uv run main_server_dual.py

Wait for 'Application startup complete.' (a few minutes on a cold start: it
loads LHM, SAM2 and BiRefNet and reconstructs the avatar), then run this again.\
"""


async def run(url, frames):
    OUT_DIR.mkdir(exist_ok=True)
    latencies = []
    last = {}
    try:
        async with websockets.connect(url, max_size=None) as ws:
            print(f"connected to {url}")
            for _ in range(frames):
                start = time.perf_counter()
                await ws.send(json.dumps(build_frame()))
                replies = [await ws.recv(), await ws.recv()]
                latencies.append(time.perf_counter() - start)
                for reply in replies:
                    eye = "left" if reply[0] == 0 else "right"
                    last[eye] = Image.open(io.BytesIO(reply[1:]))
                    last[eye].load()
    except OSError as exc:
        raise SystemExit(CONNECT_HELP.format(url=url, exc=exc)) from None

    for eye, img in sorted(last.items()):
        path = OUT_DIR / f"frame_{eye}.jpg"
        img.save(path)
        print(f"  {eye:5s} {img.size[0]}x{img.size[1]} {img.mode} -> {path}")

    # Drop the first frame: it carries CUDA warm-up cost.
    steady = latencies[1:] or latencies
    avg = sum(steady) / len(steady)
    print(f"{frames} stereo pairs | {avg * 1000:.1f} ms round-trip "
          f"-> {1 / avg:.1f} pairs/s ({2 / avg:.1f} eye-frames/s)")

    left = np.asarray(last["left"].convert("L"), dtype=np.int16)
    right = np.asarray(last["right"].convert("L"), dtype=np.int16)
    if np.array_equal(left, right):
        raise SystemExit("FAIL: both eyes returned an identical image - no parallax")
    shift = min(range(-20, 21),
                key=lambda s: np.abs(np.roll(left, s, axis=1)[:, 24:-24] - right[:, 24:-24]).mean())
    print(f"stereo parallax: {shift} px horizontal shift between eyes")
    if not left.any():
        raise SystemExit("FAIL: rendered frame is entirely black")
    print("OK")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", default="ws://127.0.0.1:8000/ws")
    parser.add_argument("--frames", type=int, default=10)
    args = parser.parse_args()
    asyncio.run(run(args.url, args.frames))
