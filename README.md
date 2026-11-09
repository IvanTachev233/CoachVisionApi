# CoachVisionApi

Video analysis for strength training: a client uploads a clip of a lift and gets back bar path, velocity, rep count and form checks.

| Folder | Contents |
| --- | --- |
| `contracts/` | The public API (OpenAPI) and the worker's raw-output schema. See `contracts/README.md`. |
| `worker/` | Python video worker. It currently holds the proof of concept. |
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

## Checks

From `worker/`:

```bash
.venv/bin/ruff check
```

```bash
.venv/bin/pytest
```

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
