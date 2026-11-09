"""Offline, data-only firmware inspection. No device or vendor execution APIs."""

__version__ = "0.1.0"


class FirmwareError(ValueError):
    """An input failed validation before interpretation or publication."""
