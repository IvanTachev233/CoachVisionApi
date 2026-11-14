"""The worker's web service on Cloud Run (vision_worker.service)."""

import pytest
from fastapi.testclient import TestClient

from vision_worker import models, service


def test_health_answers_once_the_pose_model_has_loaded():
    app = service.create_app(load_pose_model=lambda: "pose-model@test")
    with TestClient(app) as client:
        response = client.get("/health")
    assert response.status_code == 200
    assert response.json() == {"status": "ok", "poseModel": "pose-model@test"}


def test_startup_fails_when_the_pose_model_does_not_load():
    def broken():
        raise models.ModelError("model file not found")

    app = service.create_app(load_pose_model=broken)
    with pytest.raises(models.ModelError), TestClient(app):
        pass


def test_no_docs_or_unknown_routes_are_served():
    app = service.create_app(load_pose_model=lambda: "pose-model@test")
    with TestClient(app) as client:
        assert client.get("/docs").status_code == 404
        assert client.get("/openapi.json").status_code == 404
        assert client.get("/").status_code == 404


def test_the_service_runs_the_full_pose_model():
    assert service.POSE_MODEL == "full"


@pytest.mark.skipif(not models.is_available("full"), reason="Full pose model file not downloaded")
def test_the_real_full_model_loads(monkeypatch):
    monkeypatch.delenv("VISION_MODEL_DIR", raising=False)
    app = service.create_app()
    with TestClient(app) as client:
        body = client.get("/health").json()
    assert body["poseModel"].startswith("mediapipe-pose-landmarker-full@")
