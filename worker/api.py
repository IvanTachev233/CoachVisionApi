"""
LiftBig Vision API – FastAPI Application
=========================================
Serves the kinematics engine over HTTP, exposing squat analysis as a
RESTful endpoint.  Designed for B2B integration: a client sends a video
file path, and the API returns structured biomechanical metrics.

Run locally:
    uvicorn api:app --reload

Endpoint reference:
    POST /v1/analyze/squat   – Analyze a barbell squat video.
"""

import os

from fastapi import FastAPI, HTTPException
from pydantic import BaseModel, Field

from kinematics_engine import analyze_squat_video

# ---------------------------------------------------------------------------
# Application instance
# ---------------------------------------------------------------------------

app = FastAPI(
    title="LiftBig Vision API",
    version="0.1.0",
    description="Biomechanical analysis of weightlifting movements from video.",
)


# ---------------------------------------------------------------------------
# Request / Response schemas
# ---------------------------------------------------------------------------

class SquatAnalysisRequest(BaseModel):
    """Payload sent by the client to request a squat analysis."""
    video_path: str = Field(
        ...,
        description="Local filesystem path to the squat video file.",
        examples=["trimmed_squat.mov"],
    )

    user_height_m: float | None = Field(
        default=None,
        description="Lifter's height in metres (e.g. 1.80). Required for VBT velocity metrics.",
        examples=[1.80],
    )


class VelocityMetrics(BaseModel):
    """Velocity-Based Training metrics for the concentric phase."""
    peak_concentric_velocity: float | None = Field(
        description="Highest instantaneous barbell velocity (m/s) during the concentric phase.",
    )
    min_concentric_velocity: float | None = Field(
        description="Lowest instantaneous barbell velocity (m/s) during the concentric phase (sticking point).",
    )
    average_concentric_velocity: float | None = Field(
        description="Average barbell velocity (m/s) across the entire concentric phase.",
    )


class SquatAnalysisResponse(BaseModel):
    """Structured results returned after analysing a squat video."""
    video_path: str = Field(description="Path of the video that was analysed.")
    lowest_knee_angle: float | None = Field(
        description="Interior knee angle (degrees) at the deepest point of the squat.",
    )
    depth_achieved: bool = Field(
        description="True if the hip crease dropped below the knee at any point.",
    )
    total_frames_processed: int = Field(
        description="Total number of frames read from the video.",
    )
    frames_with_pose: int = Field(
        description="Number of frames where MediaPipe detected a pose.",
    )
    velocity_metrics: VelocityMetrics = Field(
        description="VBT metrics. Values are null when user_height_m is not provided.",
    )


# ---------------------------------------------------------------------------
# Endpoints
# ---------------------------------------------------------------------------

@app.post(
    "/v1/analyze/squat",
    response_model=SquatAnalysisResponse,
    summary="Analyze a barbell squat video",
    description=(
        "Accepts a path to a local video file, runs pose estimation on "
        "every frame, and returns the minimum knee angle along with a "
        "pass/fail depth assessment."
    ),
)
async def analyze_squat(request: SquatAnalysisRequest):
    """
    Main analysis endpoint.  Validates that the video file exists, then
    delegates all heavy lifting to the kinematics engine.
    """

    # -- Input validation: make sure the file actually exists ---------------
    if not os.path.isfile(request.video_path):
        raise HTTPException(
            status_code=422,
            detail=f"Video file not found: {request.video_path}",
        )

    # -- Run the analysis (preview disabled – we're headless) ---------------
    results = analyze_squat_video(
        request.video_path,
        show_preview=False,
        user_height_m=request.user_height_m,
    )

    return SquatAnalysisResponse(**results)
