from api import app


def test_squat_analysis_route_is_registered():
    assert "/v1/analyze/squat" in {route.path for route in app.routes}
