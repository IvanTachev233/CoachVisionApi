# CoachVisionApi

Video analysis for strength training: a client uploads a clip of a lift and gets back bar path, velocity, rep count and form checks.

| Folder | Contents |
| --- | --- |
| `contracts/` | The public API (OpenAPI) and the worker's raw-output schema. See `contracts/README.md`. |
| `worker/` | Python video worker: `vision_worker/` turns one clip into raw per-frame detections. `api.py` and `kinematics_engine.py` are the old proof of concept, which no longer runs (it needs MediaPipe's removed legacy API). |
| `api/` | The API service. Empty until it is scaffolded. |

Clips and model weights never go in git; `.gitignore` blocks the usual file types.

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

From `worker/`:

```bash
.venv/bin/ruff check
```

```bash
.venv/bin/pytest
```

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
