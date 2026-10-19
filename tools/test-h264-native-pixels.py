#!/usr/bin/env python3
"""Host-side encoder smoke: true 1440x3120 BGRA -> H.264 1440x3120 YUV420.
This uses synthetic data, not the Windows driver or an Android device.
"""
import argparse
import json
import shutil
import subprocess
import sys


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--width', type=int, default=1440)
    p.add_argument('--height', type=int, default=3120)
    args = p.parse_args()
    w, h = args.width, args.height
    if w <= 0 or h <= 0 or w % 2 or h % 2:
        p.error('H.264 YUV420 target must have positive even dimensions')
    ffmpeg = shutil.which('ffmpeg')
    ffprobe = shutil.which('ffprobe')
    if not ffmpeg or not ffprobe:
        sys.exit('FAIL: ffmpeg and ffprobe are required on PATH')

    # BGRA with horizontal/vertical structure, no mock video content or size substitution.
    rows = []
    for y in range(h):
        b = y % 256
        row = bytearray(w * 4)
        for x in range(w):
            offset = 4 * x
            row[offset:offset+4] = bytes((b, x % 256, (x ^ y) % 256, 255))
        rows.append(row)
    bgra = b''.join(rows)
    assert len(bgra) == w * h * 4

    cmd = [ffmpeg, '-hide_banner', '-loglevel', 'error', '-f', 'rawvideo',
           '-pix_fmt', 'bgra', '-video_size', f'{w}x{h}', '-framerate', '60',
           '-i', 'pipe:0', '-an', '-sn', '-frames:v', '1',
           '-vf', 'scale=out_color_matrix=bt709:out_range=tv,format=yuv420p',
           '-c:v', 'libx264', '-preset', 'ultrafast', '-tune', 'zerolatency',
           '-bf', '0', '-g', '120', '-flush_packets', '1', '-f', 'flv', 'pipe:1']
    encode = subprocess.run(cmd, input=bgra, capture_output=True, timeout=70)
    if encode.returncode:
        sys.exit(f'FAIL ffmpeg: {encode.stderr.decode(errors="replace")}')
    probe = subprocess.run(
        [ffprobe, '-v', 'error', '-show_entries', 'stream=codec_name,width,height,pix_fmt',
         '-of', 'json', '-i', 'pipe:0'], input=encode.stdout, capture_output=True, timeout=20,
        check=True,
    )
    streams = json.loads(probe.stdout)['streams']
    assert len(streams) == 1 and streams[0]['codec_name'] == 'h264', streams
    assert (streams[0]['width'], streams[0]['height']) == (w, h), streams
    assert streams[0]['pix_fmt'] == 'yuv420p', streams
    print(f'PASS real ffmpeg H.264 encode: BGRA {w}x{h} -> YUV420 {w}x{h}, '
          f'{len(encode.stdout)} FLV bytes, no spatial resampling')


if __name__ == '__main__':
    main()
