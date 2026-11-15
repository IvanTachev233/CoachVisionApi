# CoachVisionApi

Video analysis for strength training: a client uploads a clip of a lift and gets back bar path, velocity, rep count and form checks.

| Folder | Contents |
| --- | --- |
| `contracts/` | The public API (OpenAPI) and the worker's raw-output schema. See `contracts/README.md`. |
| `worker/` | Python video worker: `vision_worker/` turns one clip into raw per-frame detections. `api.py` and `kinematics_engine.py` are the old proof of concept, which no longer runs (it needs MediaPipe's removed legacy API). |
| `api/` | The API service: ASP.NET Core on .NET 10 (LTS). `/health` and JSON logs, tenant isolation, and tenant API key authentication with per-tenant limits. |
| `infra/` | Google Cloud for Vision, managed with OpenTofu, and the deploy script. See `infra/README.md`. |
| `.github/` | CI and deploys (`workflows/ci.yml`), Dependabot. |

Clips and model weights never go in git; `.gitignore` blocks the usual file types.

## API

.NET SDK 10 (`api/global.json` takes the newest 10.0 SDK installed). From `api/`:

```bash
dotnet restore --locked-mode
```

```bash
dotnet run --project src/CoachVision.Api
```

It answers `GET /health` with `Healthy`; nothing else yet. Logs are one JSON object per line on standard output, which Cloud Logging reads as structured entries (`severity`, `message`, `time`, `logger`, `fields`, `stack_trace`); the worker writes the same shape.

Checks, as CI runs them (`dotnet format` without `--verify-no-changes` fixes the formatting):

```bash
dotnet format --verify-no-changes
```

```bash
dotnet test
```

```bash
dotnet package list --vulnerable --include-transitive
```

Restore also checks every package, direct and transitive, against nuget.org's advisories, and fails on any finding (`Directory.Build.props`). Package versions are in `Directory.Packages.props`; after changing one, run `dotnet restore` (not locked) to update the `packages.lock.json` files, and commit them.

Tests use xUnit v3 on Microsoft Testing Platform (`global.json` sets the runner).

### Database

`CoachVision.Data` (EF Core 10 + Npgsql) holds the schema: tenants, API keys, jobs, results and
usage, with tenant isolation enforced by Postgres row-level security, not application code (ADR
0027 in the notes repo). The API only registers the context when a connection string is
configured under `ConnectionStrings:VisionDb`; with none set (for example staging before its
database secret exists) the app still starts and `/health` still answers, since neither touches
the database.

Add a migration, from `api/`:

```bash
dotnet ef migrations add <Name> --project src/CoachVision.Data --startup-project src/CoachVision.Data
```

`dotnet ef` needs `VISIONDB_DESIGN_TIME_CONNECTION_STRING` set (any reachable Postgres; `migrations
add` never opens it). The checks below also run the two database checks CI runs: every migration
applied to an empty database, and the drift check (`dotnet ef migrations
has-pending-model-changes`, exits non-zero on a model change with no matching migration). `dotnet
test` already exercises both, against a disposable Postgres container
(`CoachVision.Data.Tests`, Testcontainers; Docker must be running locally).

### Tenant API keys

There is no admin endpoint for this; `CoachVision.Cli` is the operator tool, run with the same
connection string the API uses:

```bash
ConnectionStrings__VisionDb="<the API's own connection string>" dotnet run --project src/CoachVision.Cli -- create-tenant "Example Tenant"
```

```bash
ConnectionStrings__VisionDb="<...>" dotnet run --project src/CoachVision.Cli -- issue-key <tenant-id> live
```

The key is printed once, by `issue-key`, and is not recoverable afterwards — store it immediately.
A tenant keeps at most two active keys per kind (`live`/`test`); issue the second before revoking
the first, to roll over without downtime:

```bash
ConnectionStrings__VisionDb="<...>" dotnet run --project src/CoachVision.Cli -- revoke-key <tenant-id> <api-key-id>
```

A request authenticates with `Authorization: Bearer <key>`. Which kind this deployment accepts is
`Vision:AcceptedKeyKind` (`live` in production, `test` in staging), defaulting to `live`. Each
tenant's rate, concurrent-jobs, daily-minutes and clip-size/length limits are its own
`tenant_limits` row, created with sensible defaults when the tenant is; there is no tool yet to
change them beyond a direct update through the migrator connection.

### Rep and velocity engine

`CoachVision.Analysis` (`api/src/CoachVision.Analysis`) turns one worker raw output
(`contracts/internal/raw-output/v1.json`) into the public results
(`contracts/openapi/vision-v1.yaml`: `Results`): rep count, bar velocity, bar path and the squat
depth check. It has no database or web dependency and is not yet called from `CoachVision.Api`.

Reps are counted twice, independently: from the shoulders (works even without a visible plate)
and from the bar. If the two counts disagree, the bar is not used for anything on that clip
(`velocity` and `bar_path` come back `unanalysable: bar_track_mismatch`) rather than risk a wrong
circle passing in silence. `rep_count` still comes back from the shoulders either way. If any
single rep's own concentric window does not have enough of the bar — fewer than two frames, or
present across less than half the window's own span — `velocity` as a whole comes back
`unanalysable: plates_not_found` too: there is no made-up speed for one rep inside an otherwise
analysed result.

Its tests (`api/tests/CoachVision.Analysis.Tests`) are of two kinds:

- Unit tests of each building block, and synthetic clips built in code (clean reps, a paused
  rep, a walkout dip, no plate, a bar that never moves, a non-squat lift, a known bar speed).
  These run in CI.
- Tests against recorded clips' raw outputs, skipped unless told where they are (the same
  convention as the worker's own clip tests):

  ```bash
  VISION_EVAL_OUTPUTS=/path/to/raw-outputs VISION_EVAL_LABELS=/path/to/clip-labels.csv dotnet test --project tests/CoachVision.Analysis.Tests
  ```

  `VISION_EVAL_OUTPUTS` is a folder of `<clip>.json` raw outputs; `VISION_EVAL_LABELS` is the
  clip labels CSV (`file,reps,plate_colour,plate_diameter_mm,...`). CI never sets these, so it
  runs the synthetic-track tests only.

**Where a rep ends, for velocity.** A rep's concentric phase is bottom to lockout; lockout is not
"back at the level the bar started from" (a lifter may finish noticeably lower, or a little
higher, than where the rep began) and not "no further rise for a while" either (that also means
something different at 30 fps than at 60, and would end the rep on a slow settling creep after
the real top, not just on a stall). It is two steps, over the bar's raw track lightly smoothed in
time (not frames, so the same physical window covers more samples at a higher frame rate, not
fewer frames at any rate): (a) a gate — the first point at which the bar has recovered at least
85% of this rep's own descent depth — marks where judging the climb to the top may start, low
enough that a stall before it is never mistaken for the top; (b) from there, the engine walks
forward to the actual top of the rise: the first point after which the bar gets no higher, within
0.3 s, by more than 1% of the depth. Checked against every analysed rep in both recorded runs
(102 reps): moving the gate from 0.85 to 0.95 changes no rep's mean by more than 3%, so the gate
only has to clear a stall — the climb step decides the answer, not the gate's own value. A rep's
lockout search never reaches past the shoulders' own end time for that rep plus a one-second
margin, so it can't run on into a walk-back or re-rack afterwards (where the bar often ends up
higher than where the rep itself started).

Its tests include: a plateau that never returns to 100% of the start level (`squat_10`'s shape);
a dip followed by a slow creep past the real top (`squat_07`'s shape); a rep that tops out below
the gate (ends there, not at a fallback); a stall at 60% of depth for 0.6 s (does not end the
rep); a rep still visibly rising when it crosses the gate, finishing its climb 0.15 s later (ends
at the top, not the crossing); a re-rack after the last rep, an unrack before the first, a pause
at the bottom, 30 vs. 60 fps, and twice the pixel scale (all give the same result).

**What is validated against real clips.** Rep count from the shoulders, checked against the
labelled rep count on every clip a worker run provides; the two lockout constants above, checked
for stability (not tuned to a target) across every analysed rep in two recorded runs (see
`RecordedOutputsTests` for the per-rep table this is based on). **What is not validated.** There
is no ground-truth bar speed for any recorded clip, so the absolute accuracy of `meanConcentricMps`
and `peakConcentricMps` — only their internal consistency — is unverified. The hip and knee
positions the squat depth check reads have never been checked against labelled body points, so
`squat_depth`'s `passed` and `minKneeAngleDeg` are implemented to the contract but not yet
trusted; nor is the choice of one plate-radius scale per rep for velocity (chosen because the
plate's measured radius can vary within a clip, and scaling frame by frame would turn that
jitter into fake speed changes) checked against a ground-truth speed. Every threshold the engine
invents (the lockout gate and climb tolerance above, the bar-presence rule a rep's own concentric
window must clear before it can be measured at all, the peak-speed bound above which `velocity`
is withheld rather than reported capped, the definition of confidence itself, the scheme behind
`modelVersion`) is marked as an assumption in its own doc comments, for confirmation before this
engine is relied on. `confidence` is computed and returned on every analysed result, but nothing
is withheld on it: the one case that withholds a result on plausibility is a rep's peak speed
above that bound, which comes back `unanalysable: low_confidence` ("a result was produced but
isn't trusted, so it is withheld") instead of a silently capped number; there is no other
confidence cutoff yet.

## Images

Each service has a multi-stage `Dockerfile` with base images pinned by digest, running as an unprivileged user on port 8080. From the repository root:

```bash
infra/ci/build-image.sh api
```

```bash
infra/ci/build-image.sh worker
```

Each builds the image and checks that it starts and answers `/health`, as CI does. The worker image holds the Full pose model, downloaded at build time and checked against the SHA-256 pinned in `vision_worker/models.py`; it runs `python -m vision_worker.service`, which loads that model before `/health` answers. It has no plate proposer yet (V3c), so analyses in the image need `--finder shoulders`, `--mark` or a proposer from outside (as the timing run below).

## CI and deploys

`.github/workflows/ci.yml`, in the shape `test → build → deploy-staging → deploy-production`, as LiftBig's API (ADR 0023 there):

- Pull requests: the API's and the worker's checks for the folders changed, then their images are built and started. The `ci-ok` job is the one check to require.
- A merge to `main` builds both images once (or reuses the image of an unchanged folder, by its git tree) into the production registry and deploys both, by digest, to staging (environment `staging`).
- A merge to `production` deploys the digests staging ran for the same `api/` and `worker/` content to production, after the `production` environment's required reviewer approves. Nothing is built for production.

GitHub variables: `BUILD_WIF_PROVIDER` and `BUILD_SA` in the repository (the `github-builder` identity in production), `WIF_PROVIDER`, `DEPLOY_SA` and `GCP_PROJECT_ID` in each environment (its `github-deployer`). `infra/deploy.sh` is what both deploy jobs run.

## Analysis time on 2 vCPU

`vision_worker.bench` runs the worker on every clip in a folder, with the Full model and the default plate finder, and prints a table of analysis time against the bar of twice the clip's length (VS3). Clips first go through `vision_eval.strip`, on the machine that holds them: a copy with only the picture, no sound, location or other tags. From `worker/`:

```bash
.venv/bin/python -m vision_eval.strip /path/to/clips /path/to/stripped
```

```bash
.venv/bin/python -m vision_worker.bench /path/to/stripped
```

On a laptop this shows only that the run works; the figure that counts is from the worker image on Cloud Run with 2 vCPU (`--proposer-dir` names the proposer's folder there).

## Worker setup

Python 3.11 (`worker/.python-version`). From `worker/`:

```bash
python3 -m venv .venv
```

```bash
.venv/bin/pip install --require-hashes -r requirements.txt -r requirements-dev.txt
```

### Model files

The worker needs the Pose Landmarker model file of the chosen `--model` in `worker/models/` (or the folder in `VISION_MODEL_DIR`). They are the "Latest" links on Google's Pose Landmarker page; from `worker/`:

```bash
curl -fL --create-dirs -o models/pose_landmarker_full.task https://storage.googleapis.com/mediapipe-models/pose_landmarker/pose_landmarker_full/float16/latest/pose_landmarker_full.task
```

```bash
curl -fL --create-dirs -o models/pose_landmarker_heavy.task https://storage.googleapis.com/mediapipe-models/pose_landmarker/pose_landmarker_heavy/float16/latest/pose_landmarker_heavy.task
```

The worker refuses a file whose SHA-256 differs from the one pinned in `vision_worker/models.py`. To move to a newer model, change the pin there on purpose.

### Plate proposer

To find the plate by itself (below) the worker needs one more file in `worker/models/`: `dfine_s_obj365.onnx`, 42 MB. Only `--finder shoulders` and `--mark` run without it. It is the detector D-FINE-S trained on Objects365 (code `github.com/Peterande/D-FINE`, Apache 2.0; weights `dfine_s_obj365.pth` from the `dfinev1.0` release of `github.com/Peterande/storage`), exported to ONNX with its box post-processing, for one 640 by 640 picture, opset 16. The worker runs it on the CPU with ONNX Runtime and refuses a file whose SHA-256 differs from the one pinned in `vision_worker/models.py`.

The file is not downloaded: it was made once, with PyTorch, and an export made again may not be the same bytes. Keep the file; if it has to be made again, change the pin on purpose.

## Running the worker

From `worker/`, analyse one local video and write its raw output (`contracts/internal/raw-output/v1.json`):

```bash
.venv/bin/python -m vision_worker clip.mp4 --out clip.json --model full
```

`--model` is `full` (default) or `heavy`. Without `--out` the JSON goes to standard output. A line with frame counts and the time taken goes to standard error.

By default the worker looks for the plate by itself: the plate proposer (above) is shown three frames and answers with boxes; each box is checked for a rim (in the grey picture, then in a picture of the box's own colour, for a plate about as bright as what is behind it), the best are followed through the clip, and a track is kept only if it goes up and down by at least its own radius, stays near the lifter, and keeps its size. While following, the picture of the plate's middle (the hub and what is printed around it) from the first frame holds the circle on the hub where the rim is partly hidden or the plate's own side shows next to it. Once the circle is on the hub it stays there while the middle still looks half like its picture and is found where the plate is expected, so the circle doesn't step between hub and rim as the light on the plate changes over a lift. If a larger circle is about the best track's centre, the track is on a smaller plate or ring in front: it keeps its positions and reports the larger size. The chosen track is then mended once. A run of up to five frames that stands alone, far from the way between the frames before and after it, is dropped. Where a gap of three frames or more is left, the plate is followed again from a frame near the gap, and what that finds fills the gap if the two tracks are otherwise the same circle. Last, the track is smoothed: frames that jumped aside are told from the rest (up to four in eleven, so also a track hopping between two places), and each position is put on a parabola fitted to the seven frames around it that did not jump. Frames without a position stay without one. A track followed from `--mark` is smoothed the same way.

The body points are smoothed over time as well (`vision_worker/smooth.py`), after the bar has been found from them as measured. A joint is not held in place and a bone is not held to one length: a thigh pointing at the camera really is shorter in the picture. Each point is put on the way it goes over the seven frames around it. Points the model is unsure of (confidence under 0.5: a wrist behind a plate) don't hold that curve up; between sure frames they are put on it, and a long unsure stretch is smoothed by itself. Where a point is unsure for up to 30 frames between sure ones, the model's guesses are dropped for a bridge from the one sure place to the other: an elbow or wrist goes with the joint above it, a knee or ankle straight across the picture. With `--lift squat` an ankle is held at one place for as long as its sure positions stay there (a walk-out gives each place it stands its own); other lifts, and no `--lift`, hold nothing, since the feet move in a snatch or a clean. Confidences are written as the model gave them. The overlay draws unsure points, and bones that end in one, smaller and fainter. How the lifter moves is not asked about, so the lift need not be a squat; the pose gives only the lifter's size and place. A clip where nothing passes gets no bar positions. The output's detector is `dfine-s-obj365@1+opencv-follow@1`. Without the proposer's file the run stops with an error.

`--finder shoulders` is the older way, which needs no proposer: it looks for circles that keep their place by the lifter's shoulders, and names the detector `opencv-hough-plates@1`. On the evaluation clips it finds far fewer plates:

```bash
.venv/bin/python -m vision_worker clip.mp4 --out clip.json --finder shoulders
```

The older way needs the shoulders and their movement, so it suits only the squat. With `--mark` it follows the plate a person pointed at instead: the time of a frame in seconds, then the plate's centre and a point on its rim in the video's pixels. The plate is followed forwards and backwards from that frame, without the pose, and the output names the detector `opencv-marked-plate@1`:

```bash
.venv/bin/python -m vision_worker clip.mp4 --out clip.json --mark 1.6 250 422 250 220
```

A mark with no plate under it gives an output without bar positions. A mark at a time the video doesn't have is an error.

A marked plate is followed in two more ways besides the whole rim, both on unless turned off. The output's detector name says which were on (`opencv-marked-plate@1+colour+partial-rim`):

- By colour: the plate's colour is taken from the mark, and the rim is looked for first in a picture of that colour, then in the grey picture. When the colour doesn't set the plate apart from what is around it, the run goes on in grey only. `--no-follow-colour` turns it off.
- By a part of the rim: in the frames after the mark, where the whole rim isn't found, a part of it is accepted and the plate's size kept as last measured. `--no-follow-partial-rim` turns it off.

```bash
.venv/bin/python -m vision_worker clip.mp4 --out clip.json --mark 1.6 250 422 250 220 --no-follow-colour
```

## Plate labels

`worker/vision_eval/` holds tools for judging the worker on the private evaluation clips; it is not part of the worker. The labelling tool shows 5 frames of each clip in a window and saves where you click the plate, as `plate-labels.csv` beside the clips. From `worker/`:

```bash
.venv/bin/python -m vision_eval.label_tool /path/to/clips
```

On each frame, click the centre of the plate nearest the camera, then its top or bottom edge, then press Space. If a smaller plate sits in front of the big one, label the big one. `h` then Space marks a frame where that plate can't be seen, `r` starts the frame again, and `q` saves the finished clips and stops; the next run carries on with the clips that have no labels. `--only squat_03.mp4` labels one clip again.

Frames are only shown on screen: the tool writes numbers, never pictures. Each row has the clip, the frame's number and time as in the raw output, and the two clicks and radius in the video's pixels.

## Bar scores

`vision_eval.score` compares stored raw outputs with the plate labels and prints one table per model: a row per clip and a totals row. It reads the outputs as `<model>/<clip>.json`, the layout `VISION_EVAL_OUT` keeps (see Checks), and opens no clip. From `worker/`:

```bash
.venv/bin/python -m vision_eval.score /path/to/outputs /path/to/clips/plate-labels.csv --save /path/to/outputs/bar-scores.md
```

"Frames with a bar" counts every frame of the clip. The other columns count only the labelled frames where the plate can be seen: a bar is right when its centre is within 0.25 of the labelled radius of the labelled centre and its radius is within 10% of the labelled radius. `--centre-tolerance` and `--radius-tolerance` change the two figures. Labelled frames where the plate is hidden are counted under each table. Without `--save` the tables are only printed.

The command stops with an error when a label's frame number, time or video size doesn't match the output, since the two then come from different videos.

`vision_eval.marked` measures how well a marked plate is followed. It reads each labelled clip once, without the pose model, and uses every label in turn as the mark; each run is scored on the clip's other labels, never on its own. The runs' raw outputs go to `marked/<clip>.mark-<frame>.json` in the output folder, and the table is printed and saved there as `bar-scores.md`:

```bash
.venv/bin/python -m vision_eval.marked /path/to/clips /path/to/marked-outputs
```

It follows as the worker does, by colour and by a part of the rim. `--no-colour` and `--no-partial-rim` turn each off, so each can be compared with and without. Give every combination its own output folder: the runs go to a folder named after what was on (`marked+colour`), but `bar-scores.md` has one name.

```bash
.venv/bin/python -m vision_eval.marked /path/to/clips /path/to/marked-outputs-no-colour --no-colour
```

## Checks

The worker's, as CI runs them. From `worker/` (`ruff format` without `--check` fixes the formatting):

```bash
.venv/bin/ruff check
```

```bash
.venv/bin/ruff format --check
```

```bash
.venv/bin/pytest
```

```bash
.venv/bin/pip-audit --require-hashes --disable-pip -r requirements.txt
```

`pip-audit` checks the image's locked dependencies against known vulnerabilities without installing anything, and fails on any finding.

Tests that need a real model file are skipped when it is missing. The run over the private evaluation clips only happens when it is told where they are; it checks every clip's output against the contract and can keep the outputs:

```bash
VISION_EVAL_CLIPS=/path/to/clips VISION_EVAL_MODELS=full,heavy VISION_EVAL_OUT=/path/to/outputs .venv/bin/pytest tests/test_eval_clips.py
```

`VISION_EVAL_FINDER=shoulders` runs it with `--finder shoulders`.

From the repository root:

```bash
gitleaks git --no-banner --redact .
```

```bash
gitleaks dir --no-banner --redact .
```

## Changing dependencies

Edit `requirements.in` or `requirements-dev.in`, then regenerate both lock files from `worker/`, in this order:

```bash
.venv/bin/pip-compile --generate-hashes --allow-unsafe --strip-extras -o requirements.txt requirements.in
```

```bash
.venv/bin/pip-compile --generate-hashes --allow-unsafe --strip-extras -o requirements-dev.txt requirements-dev.in
```
