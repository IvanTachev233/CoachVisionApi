# syntax=docker/dockerfile:1
# The Vision worker image: the worker package, its locked dependencies and the pinned Full pose
# model (VS3 change 1). Base images are pinned by digest; Dependabot moves the digests.
# Build from worker/: docker build -t vision-worker worker

# Stage 1: dependencies, from the hashed lock file only, into a virtual environment.
FROM python:3.11-slim-trixie@sha256:07d4824036af339fa1b6ef769cc99d9c10a141769538b52b3b597c84f7a7b535 AS deps
ENV PIP_DISABLE_PIP_VERSION_CHECK=1 PIP_NO_CACHE_DIR=1
RUN python -m venv /opt/venv
COPY requirements.txt /tmp/requirements.txt
RUN /opt/venv/bin/pip install --require-hashes --no-deps -r /tmp/requirements.txt

# Stage 2: the Full pose model. The build stops if the file isn't the one pinned in
# vision_worker/models.py (MODEL_SHA256["full"]); change both together. Version 3 is the file
# the evaluation ran with (it was "latest" then; versions 1 and 2 are other files).
FROM scratch AS models
ADD --chmod=444 --checksum=sha256:4eaa5eb7a98365221087693fcc286334cf0858e2eb6e15b506aa4a7ecdcec4ad \
    https://storage.googleapis.com/mediapipe-models/pose_landmarker/pose_landmarker_full/float16/3/pose_landmarker_full.task \
    /models/pose_landmarker_full.task

# Stage 3: runtime.
FROM python:3.11-slim-trixie@sha256:07d4824036af339fa1b6ef769cc99d9c10a141769538b52b3b597c84f7a7b535 AS production
ENV PATH=/opt/venv/bin:$PATH \
    PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    MPLCONFIGDIR=/tmp/matplotlib
# Shared libraries the wheels link to and the slim image lacks: OpenCV's build with window
# support (the lock's opencv-contrib-python, which mediapipe requires) needs libxcb, libGL and
# GLib; MediaPipe's tasks library needs EGL and GLES, even on the CPU. Versions come from the
# pinned Debian base.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libegl1 libgl1 libgles2 libglib2.0-0t64 libxcb1 \
    && rm -rf /var/lib/apt/lists/*
# An unprivileged user to run as. The files below stay owned by root, so the process can read
# the code and the model but not change them.
RUN useradd --system --uid 10001 --user-group --home-dir /nonexistent --no-create-home --shell /usr/sbin/nologin vision
COPY --from=deps /opt/venv /opt/venv
WORKDIR /app
COPY vision_worker/ vision_worker/
# vision_worker/models.py looks in /app/models by default.
COPY --from=models /models/ models/
USER 10001:10001
EXPOSE 8080
CMD ["python", "-m", "vision_worker.service"]
