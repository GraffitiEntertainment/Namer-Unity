#!/usr/bin/env python3
"""Equation checks for NAMER v2 AO and proposed material-value fitting.

Standard library only. This script does not execute Unity, ray trace a mesh,
classify an image, or benchmark the proposed metallic/emissive estimators.
"""
from __future__ import annotations
import argparse
import json
import math
from pathlib import Path


def clamp(x: float, lo: float = 0.0, hi: float = 1.0) -> float:
    return max(lo, min(hi, x))


def ao_remap(a: float, strength: float, contrast: float) -> float:
    return clamp((clamp(1.0 - strength + strength * a) - 0.5) * contrast + 0.5)


def shared_value(values: list[float], mask: list[int], weights: list[float]) -> float:
    if not (len(values) == len(mask) == len(weights)):
        raise ValueError('Values, mask, and weights must have equal lengths.')
    if any(w < 0 for w in weights) or any(m not in (0, 1) for m in mask):
        raise ValueError('Weights must be nonnegative and masks binary.')
    denominator = sum(w * m for w, m in zip(weights, mask))
    return (sum(w * m * v for w, m, v in zip(weights, mask, values)) / denominator
            if denominator else 0.0)


def run_checks() -> dict:
    directions = []
    for k in range(64):
        u = (k + 0.5) / 64.0
        theta = 2 * math.pi * ((k * 0.61803398875) % 1.0)
        directions.append((math.sqrt(u) * math.cos(theta),
                           math.sqrt(u) * math.sin(theta), math.sqrt(1 - u)))
    norm_error = max(abs(math.sqrt(sum(t*t for t in d)) - 1) for d in directions)
    assert norm_error < 1e-14
    assert all(d[2] > 0 for d in directions)

    recomposition_error = 0.0
    cases = 0
    for floor in (0.1, 1e-6):
        for strength in (0.0, 0.25, 0.5, 0.75, 1.0):
            for i in range(101):
                a = i / 100
                for color in (0.0, 0.1, 0.3, 1.0):
                    g = (1 - strength) + strength * max(a, floor)
                    recovered = (color / g) * g
                    recomposition_error = max(recomposition_error, abs(recovered - color))
                    cases += 1
    assert recomposition_error < 1e-14
    synthetic_below_floor = (0.3 / max(0.02, 0.1)) * 0.02
    authored_above_floor = (0.3 / max(0.02, 1e-6)) * 0.02
    assert math.isclose(synthetic_below_floor, 0.06)
    assert math.isclose(authored_above_floor, 0.3)
    assert ao_remap(0.2, 0, 1) == 1
    assert ao_remap(0.2, 0, 0.5) == 0.75
    dark_bright_mean = (0.1 + 0.8) / 2
    image_aos = [clamp(v / dark_bright_mean) for v in (0.1, 0.8)]
    assert math.isclose(image_aos[0], 2 / 9)
    assert image_aos[1] == 1

    # Proposed scalar fitting model; the mask is supplied, NOT estimated here.
    values = [0.0, 0.6, 1.0]
    mask = [0, 1, 1]
    weights = [1.0, 1.0, 1.0]
    metal = shared_value(values, mask, weights)
    assert math.isclose(metal, 0.8)
    def objective(v: float) -> float:
        return sum(w * (x - m * v)**2 for w, x, m in zip(weights, values, mask))
    assert all(objective(metal) <= objective(i / 1000) + 1e-14 for i in range(1001))
    emission_inputs = [(0, 0, 0), (4, 2, 0), (2, 0, 2)]
    emission = [shared_value([v[c] for v in emission_inputs], mask, weights) for c in range(3)]
    assert emission == [3.0, 1.0, 1.0]
    assert shared_value(values, [0, 0, 0], weights) == 0
    r0, beta, h = 0.5, 0.25, 1.0
    proposed_gate = [clamp(r0 - beta * (1-e) * h) for e in (0, 1)]
    assert proposed_gate == [0.25, 0.5]
    return {
        'scope': 'Independent binary64 algebra checks; no Unity, mesh ray tracing, image inference, or rendered evaluation.',
        'repository_commit': '9eb068520b6e5fcf3ff39e13291dedd3f88e0015',
        'implemented_equations': {
            'ao_direction_count': 64,
            'ao_direction_max_unit_length_error': norm_error,
            'ao_guard_matched_recomposition_cases': cases,
            'ao_guard_matched_recomposition_max_error': recomposition_error,
            'source_color_in_floor_examples': 0.3,
            'synthetic_a_0_02_floor_0_1_raw_recomposition': synthetic_below_floor,
            'authored_a_0_02_floor_1e_6_raw_recomposition': authored_above_floor,
            'zero_strength_unit_contrast_ao': ao_remap(0.2, 0, 1),
            'zero_strength_half_contrast_ao': ao_remap(0.2, 0, 0.5),
            'equally_exposed_dark_bright_image_ao_estimates': image_aos,
        },
        'proposed_equations_not_implemented_estimators': {
            'supplied_mask': mask,
            'metallic_inputs': values,
            'shared_metallic_value': metal,
            'metallic_squared_error': objective(metal),
            'shared_emission_rgb': emission,
            'empty_mask_shared_value': 0.0,
            'roughness_nonemitter_emitter': proposed_gate,
        },
        'all_assertions_passed': True,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, default=Path(__file__).with_name('extraction_checks_results.json'))
    args = parser.parse_args()
    results = run_checks()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(results, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(results, indent=2))


if __name__ == '__main__':
    main()
