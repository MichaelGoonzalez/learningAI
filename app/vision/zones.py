"""Operaciones geometricas puras para zonas poligonales."""

from __future__ import annotations

from collections.abc import Sequence

from app.core.config import ZoneConfig


Point = tuple[float, float]
Polygon = Sequence[tuple[int, int] | Point]


def _point_on_segment(point: Point, start: Point, end: Point) -> bool:
    px, py = point
    ax, ay = start
    bx, by = end
    cross_product = (px - ax) * (by - ay) - (py - ay) * (bx - ax)
    if cross_product != 0:
        return False
    return (
        min(ax, bx) <= px <= max(ax, bx)
        and min(ay, by) <= py <= max(ay, by)
    )


def point_in_polygon(point: Point, polygon: Polygon) -> bool:
    """Indica pertenencia por ray casting; los bordes cuentan como interior."""
    if len(polygon) < 3:
        raise ValueError("a polygon requires at least three points")

    px, py = point
    inside = False
    previous = (float(polygon[-1][0]), float(polygon[-1][1]))
    for raw_current in polygon:
        current = (float(raw_current[0]), float(raw_current[1]))
        if _point_on_segment(point, previous, current):
            return True

        ax, ay = previous
        bx, by = current
        crosses_scanline = (ay > py) != (by > py)
        if crosses_scanline:
            intersection_x = ax + (py - ay) * (bx - ax) / (by - ay)
            if px < intersection_x:
                inside = not inside
        previous = current
    return inside


def find_zone(point: Point, zones: Sequence[ZoneConfig]) -> str | None:
    """Retorna la primera zona configurada que contiene el punto."""
    for zone in zones:
        if point_in_polygon(point, zone.points):
            return zone.name
    return None
