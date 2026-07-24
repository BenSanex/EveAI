#!/usr/bin/env bash
set -euo pipefail

repo_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
speech_root="${XDG_DATA_HOME:-$HOME/.local/share}/eva/speech"
venv_dir="$speech_root/venv"
model_root="$speech_root/models"
whisper_dir="$model_root/faster-distil-whisper-large-v3"
piper_dir="$model_root/piper"
piper_model="$piper_dir/en/en_US/amy/medium/en_US-amy-medium.onnx"
python_bin="${EVA_SPEECH_PYTHON:-$(command -v python3.13 || true)}"

if [[ -z "$python_bin" ]]; then
  echo "Python 3.13 is required. Install it with: brew install python@3.13" >&2
  exit 1
fi

mkdir -p "$model_root" "$piper_dir"
"$python_bin" -m venv "$venv_dir"
"$venv_dir/bin/python" -m pip install --upgrade pip
"$venv_dir/bin/python" -m pip install \
  "faster-whisper==1.2.1" \
  "piper-tts==1.6.0" \
  "nvidia-cublas-cu12==12.9.2.10" \
  "nvidia-cudnn-cu12==9.25.0.15"

"$venv_dir/bin/python" - "$whisper_dir" "$piper_dir" <<'PY'
from huggingface_hub import hf_hub_download, snapshot_download
from pathlib import Path
import sys

whisper_dir = Path(sys.argv[1])
piper_dir = Path(sys.argv[2])
snapshot_download(
    repo_id="Systran/faster-distil-whisper-large-v3",
    revision="c3058b475261292e64a0412df1d2681c06260fab",
    local_dir=whisper_dir,
)
for filename in ("en_US-amy-medium.onnx", "en_US-amy-medium.onnx.json", "MODEL_CARD"):
    hf_hub_download(
        repo_id="rhasspy/piper-voices",
        revision="0d907f158acc877ddeebcbf827659ee13bea8bcd",
        subfolder="en/en_US/amy/medium",
        filename=filename,
        local_dir=piper_dir,
    )
PY

"$venv_dir/bin/python" - "$speech_root" "$whisper_dir" "$piper_dir" <<'PY'
from pathlib import Path
import hashlib
import json
import sys

speech_root, whisper_dir, piper_dir = map(Path, sys.argv[1:])
files = {}
for root in (whisper_dir, piper_dir):
    for path in sorted(root.rglob("*")):
        if path.is_file() and ".cache" not in path.parts:
            digest = hashlib.sha256(path.read_bytes()).hexdigest()
            files[str(path.relative_to(speech_root))] = {
                "bytes": path.stat().st_size,
                "sha256": digest,
            }
expected = {
    "models/faster-distil-whisper-large-v3/model.bin":
        "b79368e19b6623813609431a6e5ee309a71506701ebc49fd7820e692dec7c5f5",
    "models/piper/en/en_US/amy/medium/en_US-amy-medium.onnx":
        "b3a6e47b57b8c7fbe6a0ce2518161a50f59a9cdd8a50835c02cb02bdd6206c18",
    "models/piper/en/en_US/amy/medium/en_US-amy-medium.onnx.json":
        "95a23eb4d42909d38df73bb9ac7f45f597dbfcde2d1bf9526fdeaf5466977d77",
}
for path, expected_sha in expected.items():
    actual = files.get(path, {}).get("sha256")
    if actual != expected_sha:
        raise SystemExit(f"Checksum validation failed for {path}: {actual}")
manifest = {
    "speechRuntime": {
        "fasterWhisper": "1.2.1",
        "piperTts": "1.6.0",
        "nvidiaCublasCu12": "12.9.2.10",
        "nvidiaCudnnCu12": "9.25.0.15",
    },
    "models": {
        "stt": {
            "id": "Systran/faster-distil-whisper-large-v3",
            "revision": "c3058b475261292e64a0412df1d2681c06260fab",
            "license": "MIT",
        },
        "tts": {
            "id": "rhasspy/piper-voices/en/en_US/amy/medium",
            "revision": "0d907f158acc877ddeebcbf827659ee13bea8bcd",
            "license": "See bundled MODEL_CARD and source dataset terms",
        },
    },
    "files": files,
}
(speech_root / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
PY

echo "Eva speech runtime ready:"
echo "  STT: $whisper_dir"
echo "  TTS: $piper_model"
echo "  Manifest: $speech_root/manifest.json"
