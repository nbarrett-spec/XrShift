"""XrShift hand tracking helper.

Reads the glasses' own camera stream (TCP 169.254.2.1:52997; the glasses send it by themselves while they are in
Anchor mode), finds hands with Google's MediaPipe hand landmarker, and sends what it finds to XrShift as small
JSON messages over UDP on this PC only (127.0.0.1). It never sends anything to the glasses: starting the camera
from the PC gets it stuck, and stopping it hangs the glasses' control channel.

usage: python handserver.py --model hand_landmarker.task [--port 52411] [--host 169.254.2.1]
       python handserver.py --model ... --replay recording.bin     (frames saved as raw camera messages)
Messages, one per camera frame, or one a second with no camera:
  {"t": seconds, "camera": true, "w": 504, "h": 378, "hands": [{"score": 0.97, "side": "Left", "points": [[x, y, z], x21]}]}
  {"t": seconds, "camera": false}
x and y are 0..1 across the camera picture (x to the right, y down), z is MediaPipe's relative depth.
"""
import argparse
import json
import socket
import struct
import sys
import time

import numpy as np
import cv2
import mediapipe as mp
from mediapipe.tasks.python import BaseOptions, vision

FRAME_ID = 10056
HEADER = 320                 # bytes of frame header after the 6-byte message framing
STRIDE, ROWS = 512, 378      # bytes per picture row, rows


def parse_args():
    p = argparse.ArgumentParser()
    p.add_argument("--model", required=True)
    p.add_argument("--host", default="169.254.2.1")
    p.add_argument("--camera-port", type=int, default=52997)
    p.add_argument("--port", type=int, default=52411, help="UDP port on 127.0.0.1 that XrShift listens on")
    p.add_argument("--replay", help="read camera messages from a file instead of the glasses")
    p.add_argument("--hands", type=int, default=2)
    return p.parse_args()


def read_exact(sock, n):
    buf = bytearray()
    while len(buf) < n:
        chunk = sock.recv(min(1 << 16, n - len(buf)))
        if not chunk:
            raise ConnectionError("camera connection closed")
        buf += chunk
    return bytes(buf)


def frames_from_socket(host, port, on_idle):
    """Yields (picture, width) for every camera frame, reconnecting for as long as it takes."""
    while True:
        try:
            sock = socket.create_connection((host, port), timeout=3)
            sock.settimeout(1.0)
        except OSError:
            on_idle()
            time.sleep(1.0)
            continue
        try:
            while True:
                try:
                    head = read_exact(sock, 6)
                except socket.timeout:
                    on_idle()  # connected, but the glasses are not streaming (Follow mode)
                    continue
                ident, length = struct.unpack(">HI", head)
                sock.settimeout(3.0)
                body = read_exact(sock, length)
                sock.settimeout(1.0)
                if ident != FRAME_ID or length < HEADER - 6 + STRIDE * ROWS:
                    continue
                yield picture_of(head + body)
        except (OSError, ConnectionError):
            on_idle()
            time.sleep(0.5)
        finally:
            try:
                sock.close()
            except OSError:
                pass


def frames_from_file(path):
    with open(path, "rb") as f:
        data = f.read()
    at = 0
    while at + 6 <= len(data):
        ident, length = struct.unpack_from(">HI", data, at)
        packet = data[at:at + 6 + length]
        at += 6 + length
        if ident == FRAME_ID and len(packet) >= 6 + length:
            yield picture_of(packet)
            time.sleep(1 / 15)


def picture_of(packet):
    width = struct.unpack_from("<I", packet, 11)[0] or 504
    width = min(width, STRIDE)
    pixels = np.frombuffer(packet, dtype=np.uint8, count=STRIDE * ROWS, offset=len(packet) - STRIDE * ROWS)
    return pixels.reshape(ROWS, STRIDE)[:, :width], width


def main():
    args = parse_args()
    out = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    target = ("127.0.0.1", args.port)
    started = time.monotonic()
    last_idle = [0.0]

    def send(message):
        try:
            out.sendto(json.dumps(message, separators=(",", ":")).encode(), target)
        except OSError:
            pass

    def idle():
        now = time.monotonic()
        if now - last_idle[0] >= 1.0:
            last_idle[0] = now
            send({"t": round(now - started, 3), "camera": False})

    options = vision.HandLandmarkerOptions(
        base_options=BaseOptions(model_asset_path=args.model),
        running_mode=vision.RunningMode.VIDEO,
        num_hands=args.hands,
        min_hand_detection_confidence=0.45,
        min_hand_presence_confidence=0.45,
        min_tracking_confidence=0.45,
    )
    # The picture is dim and greyscale: even out its brightness before looking for hands.
    clahe = cv2.createCLAHE(clipLimit=3.0, tileGridSize=(8, 8))
    source = frames_from_file(args.replay) if args.replay else frames_from_socket(args.host, args.camera_port, idle)
    last_ms = -1
    with vision.HandLandmarker.create_from_options(options) as landmarker:
        for picture, width in source:
            now = time.monotonic()
            ms = int((now - started) * 1000)
            if ms <= last_ms:
                ms = last_ms + 1
            last_ms = ms
            grey = clahe.apply(np.ascontiguousarray(picture))
            rgb = cv2.cvtColor(grey, cv2.COLOR_GRAY2RGB)
            result = landmarker.detect_for_video(mp.Image(image_format=mp.ImageFormat.SRGB, data=rgb), ms)
            hands = []
            for i, points in enumerate(result.hand_landmarks or []):
                side = result.handedness[i][0] if result.handedness and i < len(result.handedness) and result.handedness[i] else None
                hands.append({
                    "score": round(side.score, 3) if side else 0.0,
                    "side": side.category_name if side else "",
                    "points": [[round(p.x, 4), round(p.y, 4), round(p.z, 4)] for p in points],
                })
            send({"t": round(now - started, 3), "camera": True, "w": int(width), "h": int(picture.shape[0]), "hands": hands})


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        sys.exit(0)
