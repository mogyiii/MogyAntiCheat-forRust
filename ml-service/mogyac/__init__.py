"""
mogyac — shared training/serving code for the MogyAntiCheat ML service.

Stdlib only (no numpy/pandas/sklearn), so both `train.py` and `server.py` run on a
bare Python 3.8+ install. The only third-party dependency in the whole service is
Flask, and that is needed by `server.py` alone.

Modules:
    logparse   — streaming reader for MogyAntiCheat_Events_*.log (both formats)
    statsutil  — percentile / median / MAD helpers
    replay     — replica of the plugin's WeaponData state machine, plus feature extraction
    calibrate  — per-weapon config threshold calibration
    scoring    — robust anomaly scorer (fit offline, apply online)
"""

MODEL_FORMAT_VERSION = "1.0.0"
