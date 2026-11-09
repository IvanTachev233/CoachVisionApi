import pytest

from kinematics_engine import calculate_angle


@pytest.mark.parametrize(
    ("a", "b", "c", "expected"),
    [
        pytest.param([1, 0], [0, 0], [0, 1], 90.0, id="right-angle"),
        pytest.param([1, 0], [0, 0], [-1, 0], 180.0, id="straight-line"),
        pytest.param([1, 0], [0, 0], [1, 0], 0.0, id="same-direction"),
        pytest.param([-1, 1], [0, 0], [-1, -1], 90.0, id="wraps-past-180"),
        pytest.param([3, 2], [2, 2], [2, 3], 90.0, id="vertex-away-from-origin"),
    ],
)
def test_calculate_angle(a, b, c, expected):
    assert calculate_angle(a, b, c) == pytest.approx(expected)
