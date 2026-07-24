# Local speech models

Models are not committed. `manifest.json` records expected artifacts, licenses,
revisions, and SHA-256 values. Never install an unpinned model.

Run `./scripts/setup-speech.sh` to create an isolated Python 3.13 environment
under Eva's local data directory and download the pinned artifacts. The setup
script also writes a complete per-file manifest beside the installed models.
