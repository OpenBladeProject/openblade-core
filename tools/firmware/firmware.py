"""Run the offline firmware tools with Python 3.11 or newer."""

import sys

if sys.version_info < (3, 11):
    raise SystemExit("The offline firmware tool requires Python 3.11 or newer")

from blade_firmware.cli import main


if __name__ == "__main__":
    raise SystemExit(main())
