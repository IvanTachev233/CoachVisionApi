"""A detector model that proposes where the plates might be.

D-FINE-S trained on Objects365 has no class for a weight plate, and its class "Barbell" doesn't
fire on gym clips. But its boxes, whatever name they carry, sit on the plates (VS2f). So only the
boxes and their scores are used, as proposals: `BarTracker.find` decides which of them is a plate.

The model runs on the CPU through ONNX Runtime. Its file is the export described in README.md.
"""

import cv2
import numpy as np

INPUT_SIDE = 640  # the model's fixed input, a square; its answer comes back in the picture's own pixels


class DFineProposer:
    """`propose(image)` gives boxes as (left, top, right, bottom, score), best first."""

    name = "dfine-s-obj365@1"

    def __init__(self, model_file=None, *, session=None):
        if session is None:
            import onnxruntime  # here, so that nothing else in the worker needs it loaded

            session = onnxruntime.InferenceSession(str(model_file), providers=["CPUExecutionProvider"])
        self._session = session

    def propose(self, image):
        """Boxes for a BGR picture, in its pixels. A box the model names more than once comes out once."""
        height, width = image.shape[:2]
        square = cv2.resize(image, (INPUT_SIDE, INPUT_SIDE), interpolation=cv2.INTER_AREA)
        pixels = cv2.cvtColor(square, cv2.COLOR_BGR2RGB).transpose(2, 0, 1)[None].astype(np.float32) / 255
        _, boxes, scores = self._session.run(
            ["labels", "boxes", "scores"],
            {"images": pixels, "orig_target_sizes": np.array([[width, height]], np.int64)},
        )
        best = {}
        for box, score in zip(boxes[0].tolist(), scores[0].tolist(), strict=True):
            key = tuple(round(value, 1) for value in box)
            if score > best.get(key, (0.0,))[-1]:
                best[key] = (*box, score)
        return sorted(best.values(), key=lambda entry: -entry[4])

    def close(self):
        self._session = None
