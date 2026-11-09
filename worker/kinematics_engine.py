"""
Kinematics Engine for Barbell Squat Analysis
=============================================
Uses MediaPipe Pose to extract body landmarks from video and evaluate
two key metrics for a barbell back squat:

  1. Minimum Knee Angle  – the smallest interior knee angle observed,
     recorded at the deepest point of the movement.
  2. Squat Depth (Pass / Fail) – whether the hip crease drops below the
     knee joint at any point during the lift.  In image coordinates a
     *higher* Y-value means *closer to the ground*, so depth is achieved
     when hip_y > knee_y.

The primary entry point for programmatic use is `analyze_squat_video()`,
which returns a structured results dictionary.  Running the module
directly (`python kinematics_engine.py`) will process a default video
file and display a real-time annotated preview window.
"""

import cv2
import mediapipe as mp
import numpy as np
from scipy.signal import butter, filtfilt

# ---------------------------------------------------------------------------
# Geometry helpers
# ---------------------------------------------------------------------------

def calculate_angle(a, b, c):
    """
    Calculate the interior 2D angle at vertex **b** formed by points a-b-c.

    Parameters
    ----------
    a, b, c : array-like of [x, y]
        Three landmark coordinates.  *b* is the vertex (e.g. the knee).

    Returns
    -------
    float
        Angle in degrees, always in the range [0, 180].
    """
    a = np.array(a)
    b = np.array(b)
    c = np.array(c)

    radians = np.arctan2(c[1] - b[1], c[0] - b[0]) - np.arctan2(a[1] - b[1], a[0] - b[0])
    angle = np.abs(radians * 180.0 / np.pi)

    if angle > 180.0:
        angle = 360 - angle

    return angle


# ---------------------------------------------------------------------------
# Core analysis function
# ---------------------------------------------------------------------------

def analyze_squat_video(
    video_path: str,
    *,
    show_preview: bool = False,
    user_height_m: float | None = None,
) -> dict:
    """
    Analyze a barbell squat video and return key performance metrics.

    The function processes every frame with MediaPipe Pose, tracks the
    right-side hip / knee / ankle landmarks, and determines:
      - The knee angle at the deepest point of the squat.
      - Whether proper depth was achieved (hip crease below knee).
      - Velocity-Based Training (VBT) metrics for the concentric phase,
        when ``user_height_m`` is provided for real-world calibration.

    Parameters
    ----------
    video_path : str
        Path to the input video file.
    show_preview : bool, optional
        If ``True``, open an OpenCV window showing the annotated video
        in real time.  Press **q** to stop early.  Default is ``False``
        so the function can run headlessly (e.g. behind an API).
    user_height_m : float or None, optional
        The lifter's height in metres (e.g. 1.80).  Used to calibrate
        on-screen distances to real-world units.  When ``None``, velocity
        metrics are not calculated.

    Returns
    -------
    dict
        {
            "video_path": str,
            "lowest_knee_angle": float | None,
            "depth_achieved": bool,
            "total_frames_processed": int,
            "frames_with_pose": int,
            "velocity_metrics": {
                "peak_concentric_velocity": float | None,
                "min_concentric_velocity": float | None,
                "average_concentric_velocity": float | None,
            },
        }
    """

    # -- MediaPipe setup (scoped to this call so state doesn't leak) --------
    mp_pose = mp.solutions.pose
    pose = mp_pose.Pose(
        static_image_mode=False,
        model_complexity=1,
        smooth_landmarks=True,
        min_detection_confidence=0.5,
        min_tracking_confidence=0.5,
    )
    mp_drawing = mp.solutions.drawing_utils

    cap = cv2.VideoCapture(video_path)
    if not cap.isOpened():
        raise FileNotFoundError(f"Could not open video file: {video_path}")

    # -- Phase 1: FPS & time tracking ---------------------------------------
    fps = cap.get(cv2.CAP_PROP_FPS)
    if fps <= 0:
        fps = 30.0  # sensible fallback for malformed metadata
    delta_time = 1.0 / fps

    # -- Tracking variables -------------------------------------------------
    lowest_hip_y = 0.0          # highest numerical Y seen (= deepest physical position)
    angle_at_bottom = None      # knee angle recorded at the deepest hip position
    depth_achieved = False      # flipped to True the first time hip_y > knee_y
    total_frames = 0
    frames_with_pose = 0

    # -- Phase 2 & 3: VBT tracking variables --------------------------------
    pixel_to_meter_ratio: float | None = None   # set on first pose frame
    prev_barbell_y: float | None = None         # normalised Y of previous frame
    barbell_y_buffer: list[float] = []          # rolling window for smoothing
    SMOOTHING_WINDOW = 5                        # frames to average over
    MAX_VELOCITY = 3.0                          # m/s — physical ceiling for a squat
    MIN_VELOCITY = 0.05                         # m/s — below this is noise, not movement
    concentric_velocities: list[float] = []     # instantaneous upward velocities (m/s)
    concentric_distance_m = 0.0                 # cumulative upward distance (metres)
    concentric_time_s = 0.0                     # cumulative time spent moving up (s)
    current_velocity = 0.0                      # latest instantaneous velocity for overlay
    velocity_history: list[float] = []          # all frame velocities for graph
    GRAPH_HEIGHT = 100                          # pixels for the velocity graph
    GRAPH_MAX_POINTS = 150                      # max data points shown on graph
    playback_delay = 10                         # ms between frames (preview only)

    while cap.isOpened():
        ret, frame = cap.read()
        if not ret:
            break

        total_frames += 1

        # MediaPipe expects RGB; OpenCV captures BGR.
        image = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
        image.flags.writeable = False
        results = pose.process(image)

        # Convert back to BGR for any OpenCV rendering.
        image.flags.writeable = True
        image = cv2.cvtColor(image, cv2.COLOR_RGB2BGR)

        h, w, _ = frame.shape

        if results.pose_landmarks:
            frames_with_pose += 1
            landmarks = results.pose_landmarks.landmark

            # -- Extract right-side landmarks (normalised 0-1 coords) ------
            hip = [
                landmarks[mp_pose.PoseLandmark.RIGHT_HIP.value].x,
                landmarks[mp_pose.PoseLandmark.RIGHT_HIP.value].y,
            ]
            knee = [
                landmarks[mp_pose.PoseLandmark.RIGHT_KNEE.value].x,
                landmarks[mp_pose.PoseLandmark.RIGHT_KNEE.value].y,
            ]
            ankle = [
                landmarks[mp_pose.PoseLandmark.RIGHT_ANKLE.value].x,
                landmarks[mp_pose.PoseLandmark.RIGHT_ANKLE.value].y,
            ]

            knee_angle = calculate_angle(hip, knee, ankle)

            # -- Phase 2: Calibrate on the first pose frame ----------------
            if user_height_m is not None and pixel_to_meter_ratio is None:
                nose_y = landmarks[mp_pose.PoseLandmark.NOSE.value].y
                left_ankle_y = landmarks[mp_pose.PoseLandmark.LEFT_ANKLE.value].y
                right_ankle_y = landmarks[mp_pose.PoseLandmark.RIGHT_ANKLE.value].y
                avg_ankle_y = (left_ankle_y + right_ankle_y) / 2.0
                on_screen_height = abs(avg_ankle_y - nose_y)
                if on_screen_height > 0:
                    pixel_to_meter_ratio = user_height_m / on_screen_height

            # -- Phase 3: Plate detection via colour masking --------------------
            if user_height_m is not None and pixel_to_meter_ratio is not None:
                hsv = cv2.cvtColor(frame, cv2.COLOR_BGR2HSV)

                # Red bumper plates (red wraps around hue 0/180)
                red_mask1 = cv2.inRange(hsv, (0, 80, 80), (10, 255, 255))
                red_mask2 = cv2.inRange(hsv, (170, 80, 80), (180, 255, 255))
                # Blue bumper plates
                blue_mask = cv2.inRange(hsv, (100, 80, 80), (130, 255, 255))

                plate_mask = red_mask1 | red_mask2 | blue_mask

                # Clean up noise
                kernel = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (7, 7))
                plate_mask = cv2.morphologyEx(plate_mask, cv2.MORPH_CLOSE, kernel)
                plate_mask = cv2.morphologyEx(plate_mask, cv2.MORPH_OPEN, kernel)

                contours, _ = cv2.findContours(
                    plate_mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE,
                )

                # Filter: area + circularity (plates are round-ish)
                min_plate_area = (w * h) * 0.005
                plate_contours = []
                for c in contours:
                    area = cv2.contourArea(c)
                    if area < min_plate_area:
                        continue
                    perimeter = cv2.arcLength(c, True)
                    if perimeter == 0:
                        continue
                    circularity = 4 * np.pi * area / (perimeter * perimeter)
                    if circularity > 0.3:  # 1.0 = perfect circle
                        plate_contours.append(c)

                if plate_contours:
                    plate_ys = []
                    for c in plate_contours:
                        M = cv2.moments(c)
                        if M["m00"] > 0:
                            cy = int(M["m01"] / M["m00"])
                            cx = int(M["m10"] / M["m00"])
                            plate_ys.append(cy)

                            if show_preview:
                                cv2.drawContours(image, [c], -1, (0, 255, 0), 2)
                                cv2.circle(image, (cx, cy), 5, (0, 0, 255), -1)

                    if plate_ys:
                        # Median is more robust to outlier detections
                        raw_y = float(np.median(plate_ys)) / h

                        # Buffer raw positions for post-processing filter
                        barbell_y_buffer.append(raw_y)
                        if len(barbell_y_buffer) > SMOOTHING_WINDOW:
                            barbell_y_buffer.pop(0)
                        barbell_y = float(np.mean(barbell_y_buffer))

                        if prev_barbell_y is not None:
                            raw_displacement = prev_barbell_y - barbell_y
                            displacement_m = raw_displacement * pixel_to_meter_ratio
                            instantaneous_velocity = displacement_m / delta_time

                            # Discard physically impossible spikes
                            if abs(instantaneous_velocity) <= MAX_VELOCITY:
                                current_velocity = instantaneous_velocity

                                if instantaneous_velocity >= MIN_VELOCITY:
                                    concentric_velocities.append(instantaneous_velocity)
                                    concentric_distance_m += displacement_m
                                    concentric_time_s += delta_time

                        prev_barbell_y = barbell_y

            # Record velocity for the graph (0.0 if no plates detected this frame)
            if pixel_to_meter_ratio is not None:
                velocity_history.append(current_velocity)

            # -- Track the deepest point of the squat ----------------------
            current_hip_y = hip[1]
            if current_hip_y > lowest_hip_y:
                lowest_hip_y = current_hip_y
                angle_at_bottom = knee_angle

            # -- Depth check: hip crease below the knee --------------------
            # In image coordinates Y increases downward, so hip_y > knee_y
            # means the hip is physically lower than the knee.
            if current_hip_y > knee[1]:
                depth_achieved = True

            # -- Visual overlay (only when preview is enabled) -------------
            if show_preview:
                mp_drawing.draw_landmarks(
                    image, results.pose_landmarks, mp_pose.POSE_CONNECTIONS,
                )

                # Live knee angle near the knee joint
                cv2.putText(
                    image,
                    str(int(knee_angle)),
                    tuple(np.multiply(knee, [w, h]).astype(int)),
                    cv2.FONT_HERSHEY_SIMPLEX,
                    1,
                    (255, 255, 255),
                    2,
                    cv2.LINE_AA,
                )

                # Depth status in the top-left corner
                depth_label = "Depth: PASS" if depth_achieved else "Depth: FAIL"
                depth_color = (0, 255, 0) if depth_achieved else (0, 0, 255)  # BGR
                cv2.putText(
                    image,
                    depth_label,
                    (30, 60),
                    cv2.FONT_HERSHEY_SIMPLEX,
                    1.5,
                    depth_color,
                    3,
                    cv2.LINE_AA,
                )

                # Live velocity below the depth label
                if pixel_to_meter_ratio is not None:
                    vel_label = f"Velocity: {current_velocity:.2f} m/s"
                    vel_color = (0, 255, 255) if current_velocity > 0 else (180, 180, 180)  # BGR
                    cv2.putText(
                        image,
                        vel_label,
                        (30, 110),
                        cv2.FONT_HERSHEY_SIMPLEX,
                        1.5,
                        vel_color,
                        3,
                        cv2.LINE_AA,
                    )

                    # -- Velocity graph at the bottom of the frame -------------
                    if len(velocity_history) > 1:
                        points = velocity_history[-GRAPH_MAX_POINTS:]
                        graph_w = w
                        graph_y_top = h - GRAPH_HEIGHT - 10  # 10px margin from bottom

                        # Semi-transparent background
                        overlay = image.copy()
                        cv2.rectangle(overlay, (0, graph_y_top), (graph_w, h), (0, 0, 0), -1)
                        cv2.addWeighted(overlay, 0.5, image, 0.5, 0, image)

                        # Zero line
                        zero_y = graph_y_top + GRAPH_HEIGHT // 2
                        cv2.line(image, (0, zero_y), (graph_w, zero_y), (80, 80, 80), 1)

                        # Scale: map -MAX_VELOCITY..+MAX_VELOCITY to graph area
                        step_x = graph_w / max(len(points) - 1, 1)
                        for i in range(1, len(points)):
                            v_prev = points[i - 1]
                            v_curr = points[i]
                            # Map velocity to pixel Y (positive velocity = above zero line)
                            y_prev = int(zero_y - (v_prev / MAX_VELOCITY) * (GRAPH_HEIGHT // 2))
                            y_curr = int(zero_y - (v_curr / MAX_VELOCITY) * (GRAPH_HEIGHT // 2))
                            x_prev = int((i - 1) * step_x)
                            x_curr = int(i * step_x)

                            # Green for concentric (up), red for eccentric (down)
                            color = (0, 255, 0) if v_curr >= 0 else (0, 0, 255)
                            cv2.line(image, (x_prev, y_prev), (x_curr, y_curr), color, 2)

                        # Labels
                        cv2.putText(image, f"+{MAX_VELOCITY:.1f}", (5, graph_y_top + 15),
                                    cv2.FONT_HERSHEY_SIMPLEX, 0.4, (200, 200, 200), 1)
                        cv2.putText(image, f"-{MAX_VELOCITY:.1f}", (5, h - 5),
                                    cv2.FONT_HERSHEY_SIMPLEX, 0.4, (200, 200, 200), 1)
                        cv2.putText(image, "0", (5, zero_y - 5),
                                    cv2.FONT_HERSHEY_SIMPLEX, 0.4, (200, 200, 200), 1)

                        # Min/Max concentric markers on the graph
                        concentric_in_window = [(i, v) for i, v in enumerate(points) if v >= MIN_VELOCITY]
                        if concentric_in_window:
                            max_i, max_v = max(concentric_in_window, key=lambda x: x[1])
                            min_i, min_v = min(concentric_in_window, key=lambda x: x[1])

                            # Peak marker (cyan diamond)
                            max_px = int(max_i * step_x)
                            max_py = int(zero_y - (max_v / MAX_VELOCITY) * (GRAPH_HEIGHT // 2))
                            cv2.drawMarker(image, (max_px, max_py), (255, 255, 0), cv2.MARKER_DIAMOND, 12, 2)
                            cv2.putText(image, f"Peak: {max_v:.2f}", (max_px + 8, max_py - 5),
                                        cv2.FONT_HERSHEY_SIMPLEX, 0.4, (255, 255, 0), 1)

                            # Min marker (magenta diamond)
                            min_px = int(min_i * step_x)
                            min_py = int(zero_y - (min_v / MAX_VELOCITY) * (GRAPH_HEIGHT // 2))
                            cv2.drawMarker(image, (min_px, min_py), (255, 0, 255), cv2.MARKER_DIAMOND, 12, 2)
                            cv2.putText(image, f"Min: {min_v:.2f}", (min_px + 8, min_py + 15),
                                        cv2.FONT_HERSHEY_SIMPLEX, 0.4, (255, 0, 255), 1)

        if show_preview:
            cv2.imshow("Squat Analysis", image)
            key = cv2.waitKey(playback_delay) & 0xFF
            if key == ord("q"):
                break
            elif key == ord(" "):          # Space = pause / resume
                while True:
                    key2 = cv2.waitKey(0) & 0xFF
                    if key2 == ord(" ") or key2 == ord("q"):
                        break
                if key2 == ord("q"):
                    break
            elif key == ord(","):           # , = slow down
                playback_delay = min(playback_delay + 20, 500)
            elif key == ord("."):           # . = speed up
                playback_delay = max(playback_delay - 20, 1)

    # -- Cleanup -------------------------------------------------- ----------
    cap.release()
    if show_preview:
        cv2.destroyAllWindows()
    pose.close()

    # -- Phase 4: Compute final VBT metrics ----------------------------------
    # Apply Butterworth low-pass filter to smooth the concentric velocity signal
    peak_concentric_velocity = None
    min_concentric_velocity = None
    average_concentric_velocity = None
    if len(concentric_velocities) >= 10:
        # 2nd-order Butterworth, cutoff at 6 Hz (typical for barbell movement)
        cutoff_hz = 6.0
        nyquist = fps / 2.0
        if cutoff_hz < nyquist:
            b, a = butter(2, cutoff_hz / nyquist, btype="low")
            filtered = filtfilt(b, a, concentric_velocities)
            peak_concentric_velocity = round(float(np.max(filtered)), 3)
            min_concentric_velocity = round(float(np.min(filtered)), 3)
        else:
            peak_concentric_velocity = round(max(concentric_velocities), 3)
            min_concentric_velocity = round(min(concentric_velocities), 3)
    elif concentric_velocities:
        peak_concentric_velocity = round(max(concentric_velocities), 3)
        min_concentric_velocity = round(min(concentric_velocities), 3)
    if concentric_time_s > 0:
        average_concentric_velocity = round(concentric_distance_m / concentric_time_s, 3)

    return {
        "video_path": video_path,
        "lowest_knee_angle": round(angle_at_bottom, 1) if angle_at_bottom is not None else None,
        "depth_achieved": depth_achieved,
        "total_frames_processed": total_frames,
        "frames_with_pose": frames_with_pose,
        "velocity_metrics": {
            "peak_concentric_velocity": peak_concentric_velocity,
            "min_concentric_velocity": min_concentric_velocity,
            "average_concentric_velocity": average_concentric_velocity,
        },
    }


# ---------------------------------------------------------------------------
# CLI entry point
# ---------------------------------------------------------------------------

def main():
    """Run analysis on a default video and print results to the console."""
    video_path = "trimmed_squat.mov"

    print(f"Processing '{video_path}' … Press 'q' to stop early.\n")
    results = analyze_squat_video(video_path, show_preview=True)

    print("\n--- ANALYSIS COMPLETE ---")
    if results["lowest_knee_angle"] is not None:
        print(f"  Knee Angle at bottom : {results['lowest_knee_angle']}°")
        print(f"  Depth Achieved       : {'YES ✓' if results['depth_achieved'] else 'NO ✗'}")
        print(f"  Frames processed     : {results['total_frames_processed']}")
        print(f"  Frames with pose     : {results['frames_with_pose']}")

        vm = results["velocity_metrics"]
        if vm["peak_concentric_velocity"] is not None:
            print(f"  Peak Concentric Vel  : {vm['peak_concentric_velocity']} m/s")
            print(f"  Min Concentric Vel   : {vm['min_concentric_velocity']} m/s")
            print(f"  Avg Concentric Vel   : {vm['average_concentric_velocity']} m/s")
        else:
            print("  Velocity metrics     : N/A (no user_height_m provided)")
    else:
        print("  Could not detect pose data in the video.")


if __name__ == "__main__":
    main()
