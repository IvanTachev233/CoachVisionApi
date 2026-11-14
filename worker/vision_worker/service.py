"""The worker as a web service, the way Cloud Run runs it.

    python -m vision_worker.service

For now it only shows that the image works: at start it loads the pinned Full pose model (VS3's
choice), and /health answers once that has worked. Cloud Run's startup probe calls /health, so a
revision whose model is missing, changed or can't load never gets traffic. The Pub/Sub push
endpoint that runs analyses comes with V3c. Listens on PORT (Cloud Run sets it; default 8080).
"""

import logging
import os
import time
from contextlib import asynccontextmanager

import uvicorn
from fastapi import FastAPI

from vision_worker import logs, models
from vision_worker.pose import PoseLandmarkerEstimator, pose_model_name

POSE_MODEL = "full"

logger = logging.getLogger(__name__)


def load_pose_model():
    """Checks the Full model file against its pin and loads it once; returns the model's name."""
    estimator = PoseLandmarkerEstimator(models.model_path(POSE_MODEL))
    estimator.close()
    return pose_model_name(POSE_MODEL)


def create_app(load_pose_model=load_pose_model):
    @asynccontextmanager
    async def lifespan(app):
        started = time.perf_counter()
        app.state.pose_model = load_pose_model()
        logger.info(
            "pose model loaded",
            extra={"fields": {"poseModel": app.state.pose_model, "seconds": round(time.perf_counter() - started, 2)}},
        )
        yield

    # A private service: no interactive docs or schema.
    app = FastAPI(lifespan=lifespan, docs_url=None, redoc_url=None, openapi_url=None)

    @app.get("/health")
    def health():
        return {"status": "ok", "poseModel": app.state.pose_model}

    return app


def main():
    logs.configure()
    # uvicorn's own loggers go through the JSON handler; Cloud Run already logs each request.
    uvicorn.run(
        create_app(), host="0.0.0.0", port=int(os.environ.get("PORT", "8080")), log_config=None, access_log=False
    )


if __name__ == "__main__":
    main()
