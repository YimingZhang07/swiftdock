These are the reviewer's `frame-old-code-after-dialog.png` and
`frame-fixed-code-after-dialog.png`, both 1240 × 640 device pixels at 192 DPI.
Only the unused central rectangle `(24,24)`–`(1216,616)` is replaced with black
to reduce fixture size. Every sampled edge/reference pixel is unchanged.

The old image has a light band at insets 2–11; insets 0–1 are normal in both
images. `FrameDetector` samples insets 2–7 and compares them with 18–23,
excluding 32-pixel corner segments. A pixel counts when all RGB channels are
at least 210 and its luminance is at least 50 above the corresponding inner
pixel. Two sides with at least 20% coverage constitute the frame.

Calibration (top/right/bottom/left): old = 25.7%/100%/100%/100%; fixed = 0% on
all sides. The reference band avoids treating an ordinary light surface as a
white frame. E2E also checks coverage increases against its pre-dialog sample.
