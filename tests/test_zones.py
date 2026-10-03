from __future__ import annotations

import pytest
from pydantic import ValidationError

from app.core.config import ZoneConfig
from app.vision.zones import find_zone, point_in_polygon


SQUARE = [(0, 0), (10, 0), (10, 10), (0, 10)]


def test_point_inside_polygon() -> None:
    assert point_in_polygon((5, 5), SQUARE)


def test_point_outside_polygon() -> None:
    assert not point_in_polygon((15, 5), SQUARE)


def test_point_on_edge_is_inside() -> None:
    assert point_in_polygon((10, 5), SQUARE)


def test_concave_polygon() -> None:
    polygon = [(0, 0), (10, 0), (10, 10), (5, 5), (0, 10)]
    assert point_in_polygon((2, 5), polygon)
    assert not point_in_polygon((5, 8), polygon)


def test_find_zone_returns_first_matching_name() -> None:
    zones = [ZoneConfig(name="front", points=SQUARE)]
    assert find_zone((5, 5), zones) == "front"


def test_find_zone_without_zones_returns_none() -> None:
    assert find_zone((5, 5), []) is None


def test_zone_with_less_than_three_points_fails_during_config_validation() -> None:
    with pytest.raises(ValidationError, match="at least 3 items"):
        ZoneConfig(name="invalid", points=[(0, 0), (1, 1)])

