#!/usr/bin/env python3
"""Deterministic checks for the NAMER paper (Python 3, standard library only).

This is an independent scalar transcription of the equations described in the
paper, not an execution of Unity, C#, HLSL, or the repository's test suite.
It does not simulate UNorm render targets, half-precision intermediates, shader
compilation, texture filtering, GPU performance, or rendered-image quality.

Usage: python3 checks.py --output checks_results.json
"""
from __future__ import annotations
import argparse
import json
import math
from pathlib import Path

COMMIT = "9eb068520b6e5fcf3ff39e13291dedd3f88e0015"


def clamp(x: float, lo: float, hi: float) -> float:
    return min(max(x, lo), hi)


def pack_alpha(m: float, e: float, r: float) -> int:
    q = int(clamp(math.floor(63.0 * r), 0, 63))
    return (128 if m > 0.5 else 0) | (64 if e > 0.1 else 0) | q


def decode_normal(o: tuple[float, float]) -> tuple[float, float, float]:
    p = (2.0 * o[0] - 1.0, 2.0 * o[1] - 1.0,
         3.0 - 2.0 * o[0] - 2.0 * o[1])
    d = sum(x * x for x in p)
    scale = (1.0 + math.sqrt(max(1.0 - 2.0 * d, 0.0))) / max(d, 1e-6)
    n = (p[0] * scale - 1.0, 1.0 - p[1] * scale, p[2] * scale - 1.0)
    length = math.sqrt(sum(x * x for x in n))
    return tuple(x / length for x in n)


def encode_normal(n: tuple[float, float, float]) -> tuple[float, float]:
    v = ((n[0] + 1.0) / 2.0, (1.0 - n[1]) / 2.0, (n[2] + 1.0) / 2.0)
    scale = max(sum(v), 1e-6)
    return 0.5 + 0.5 * v[0] / scale, 0.5 + 0.5 * v[1] / scale


def det3(a: list[list[float]]) -> float:
    return (a[0][0] * (a[1][1] * a[2][2] - a[1][2] * a[2][1])
            - a[0][1] * (a[1][0] * a[2][2] - a[1][2] * a[2][0])
            + a[0][2] * (a[1][0] * a[2][1] - a[1][1] * a[2][0]))


def solve3(a: list[list[float]], b: list[float]) -> list[float]:
    determinant = det3(a)
    if abs(determinant) < 1e-12:
        raise ArithmeticError("Synthetic system unexpectedly singular")
    result = []
    for col in range(3):
        replaced = [row[:] for row in a]
        for row in range(3):
            replaced[row][col] = b[row]
        result.append(det3(replaced) / determinant)
    return result


def run_checks() -> dict:
    byte_failures = []
    for a in range(256):
        recovered = math.floor((a / 255.0) * 255.0 + 0.5)
        recomposed = (128 if a & 128 else 0) | (64 if a & 64 else 0) | (a & 63)
        if recovered != a or recomposed != a:
            byte_failures.append(a)
    assert not byte_failures
    assert pack_alpha(0.5, 0.1, 0.0) == 0
    assert pack_alpha(math.nextafter(0.5, 1.0), math.nextafter(0.1, 1.0), 0.0) == 192
    assert pack_alpha(0.0, 0.0, 1.0) == 63
    roughness_max_error = 0.0
    for i in range(100001):
        r = i / 100000.0
        decoded = (pack_alpha(0.0, 0.0, r) & 63) / 63.0
        error = r - decoded
        assert -1e-15 <= error <= 1.0 / 63.0 + 1e-15
        roughness_max_error = max(roughness_max_error, error)

    bary = [(i / 16.0, j / 16.0, 1.0 - i / 16.0 - j / 16.0)
            for i in range(1, 17) for j in range(1, 17 - i)
            if 1.0 - i / 16.0 - j / 16.0 >= 1e-4]
    assert len(bary) == 105
    gram = [[sum(w[i] * w[j] for w in bary) for j in range(3)] for i in range(3)]
    vertices = [(0.2, 0.3, 0.7), (0.8, 0.4, 0.1), (0.4, 0.9, 0.6)]
    recovered = [[0.0] * 3 for _ in range(3)]
    for channel in range(3):
        values = [sum(w[j] * vertices[j][channel] for j in range(3)) for w in bary]
        rhs = [sum(w[j] * value for w, value in zip(bary, values)) for j in range(3)]
        solution = solve3(gram, rhs)
        for j in range(3):
            recovered[j][channel] = solution[j]
    fit_error = max(abs(recovered[j][c] - vertices[j][c]) for j in range(3) for c in range(3))
    assert fit_error < 1e-12

    floor = 1e-3
    projection_identity_error = max(abs(max(i / 10000.0, floor) /
                                        max(i / 10000.0, floor) - 1.0)
                                    for i in range(10001))
    assert projection_identity_error == 0.0
    zero_vertex_reconstruction = 0.0 * (max(0.4, floor) / max(0.0, floor))
    assert zero_vertex_reconstruction == 0.0
    r0, beta = 0.5, 0.25
    transfer = [clamp(r0 - beta * clamp(d, -1, 1), 0, 1)
                for d in (-2, -1, 0, 1, 2)]
    assert transfer == [0.75, 0.75, 0.5, 0.25, 0.25]

    n = (-1.0 / math.sqrt(2.0), 1.0 / math.sqrt(2.0), 0.0)
    n_decoded = decode_normal(encode_normal(n))
    cosine = clamp(sum(a * b for a, b in zip(n, n_decoded)), -1, 1)
    normal_error = math.degrees(math.acos(cosine))
    assert abs(normal_error - 45.0) < 1e-10
    # Domain-safe, non-grazing examples; excludes UNorm8 quantization.
    safe_normals = [(0.0, 0.0, 1.0), (0.3, 0.4, math.sqrt(0.75)),
                    (-0.3, 0.4, math.sqrt(0.75)), (0.6, -0.2, math.sqrt(0.6))]
    safe_component_error = max(abs(a - b) for v in safe_normals
                               for a, b in zip(v, decode_normal(encode_normal(v))))
    assert safe_component_error < 1e-12

    pixels, vertex_count = 2048 ** 2, 50000
    mib = 1024 ** 2
    storage = {
        "five_rgba8_maps": 20 * pixels,
        "three_rgba8_maps": 12 * pixels,
        "three_bc7_maps": 3 * pixels,
        "namer_surface_and_vertex_colors": 4 * pixels + 4 * vertex_count,
        "namer_with_256_rgba16f_residual": 4 * pixels + 4 * vertex_count + 8 * 256 ** 2,
        "namer_with_512_rgba16f_residual": 4 * pixels + 4 * vertex_count + 8 * 512 ** 2,
    }
    return {
        "scope": "Independent Python binary64 equation checks; not Unity/HLSL execution or asset benchmarks",
        "repository_commit": COMMIT,
        "alpha_bytes_checked": 256,
        "alpha_byte_failures": len(byte_failures),
        "roughness_dense_samples": 100001,
        "roughness_observed_max_error": roughness_max_error,
        "roughness_theoretical_bin_width": 1.0 / 63.0,
        "barycentric_grid_samples": len(bary),
        "gram_matrix": gram,
        "affine_fit_max_component_error": fit_error,
        "projection_identity_samples": 10001,
        "projection_identity_max_error": projection_identity_error,
        "zero_vertex_source_value": 0.4,
        "zero_vertex_reconstructed_value": zero_vertex_reconstruction,
        "roughness_transfer_for_minus2_minus1_zero_plus1_plus2": transfer,
        "normal_counterexample_source": n,
        "normal_counterexample_decoded": n_decoded,
        "normal_counterexample_error_degrees": normal_error,
        "normal_safe_examples_max_component_error": safe_component_error,
        "storage_assumptions": "2048^2 surface maps, 50000 new Color32 values, no mipmaps, no extra geometry/index bytes",
        "storage_bytes": storage,
        "storage_mib": {key: value / mib for key, value in storage.items()},
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=Path(__file__).with_name("checks_results.json"))
    args = parser.parse_args()
    results = run_checks()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(results, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(results, indent=2))


if __name__ == "__main__":
    main()
