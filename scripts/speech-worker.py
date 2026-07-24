#!/usr/bin/env python3
"""Persistent JSONL speech-to-text worker for Eva."""

import argparse
import json
import sys
import time
import wave
from pathlib import Path


def send(payload: dict) -> None:
    print(json.dumps(payload, ensure_ascii=False), flush=True)


class Transcriber:
    def __init__(self, model_path: str) -> None:
        self.model_path = model_path
        self.model = None
        self.provider = None

    def _load(self, device: str, compute_type: str) -> None:
        from faster_whisper import WhisperModel

        self.model = WhisperModel(
            self.model_path,
            device=device,
            compute_type=compute_type,
            local_files_only=True,
        )
        self.provider = device

    def transcribe(self, audio_path: str) -> tuple[str, str, int]:
        if not Path(audio_path).is_file():
            raise FileNotFoundError(f"Recording was not found: {audio_path}")

        started = time.perf_counter()
        if self.model is None:
            try:
                self._load("cuda", "float16")
            except Exception as error:
                print(f"CUDA initialization failed; using CPU: {error}", file=sys.stderr, flush=True)
                self._load("cpu", "int8")

        try:
            segments, _ = self.model.transcribe(
                audio_path,
                language="en",
                beam_size=1,
                best_of=1,
                temperature=0,
                condition_on_previous_text=False,
                hotwords="EVE Online ESI ISK capsuleer Jita Amarr Dodixie Arnon",
                vad_filter=True,
                vad_parameters={"min_silence_duration_ms": 350},
            )
            text = " ".join(segment.text.strip() for segment in segments if segment.text.strip()).strip()
        except Exception as error:
            if self.provider != "cuda":
                raise
            print(f"CUDA transcription failed; retrying on CPU: {error}", file=sys.stderr, flush=True)
            self.model = None
            self._load("cpu", "int8")
            segments, _ = self.model.transcribe(
                audio_path,
                language="en",
                beam_size=1,
                best_of=1,
                temperature=0,
                condition_on_previous_text=False,
                hotwords="EVE Online ESI ISK capsuleer Jita Amarr Dodixie Arnon",
                vad_filter=True,
            )
            text = " ".join(segment.text.strip() for segment in segments if segment.text.strip()).strip()

        elapsed_ms = round((time.perf_counter() - started) * 1000)
        return text, self.provider or "unknown", elapsed_ms


class Synthesizer:
    def __init__(self, model_path: str) -> None:
        self.model_path = model_path
        self.voice = None

    def synthesize(self, text: str, output_path: str) -> int:
        from piper import PiperVoice

        started = time.perf_counter()
        if self.voice is None:
            self.voice = PiperVoice.load(self.model_path)
        with wave.open(output_path, "wb") as wav_file:
            self.voice.synthesize_wav(text, wav_file)
        return round((time.perf_counter() - started) * 1000)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--model")
    parser.add_argument("--piper-model")
    arguments = parser.parse_args()
    if not arguments.model and not arguments.piper_model:
        parser.error("--model or --piper-model is required")
    transcriber = Transcriber(arguments.model) if arguments.model else None
    synthesizer = Synthesizer(arguments.piper_model) if arguments.piper_model else None

    for line in sys.stdin:
        request_id = None
        try:
            request = json.loads(line)
            request_id = request.get("id")
            if request.get("operation") == "ping":
                send({"id": request_id, "ok": True})
                continue
            if request.get("operation") == "transcribe" and transcriber is not None:
                text, provider, elapsed_ms = transcriber.transcribe(request["path"])
                send(
                    {
                        "id": request_id,
                        "ok": True,
                        "text": text,
                        "provider": provider,
                        "elapsedMs": elapsed_ms,
                    }
                )
            elif request.get("operation") == "synthesize" and synthesizer is not None:
                elapsed_ms = synthesizer.synthesize(request["text"], request["path"])
                send({"id": request_id, "ok": True, "elapsedMs": elapsed_ms})
            else:
                raise ValueError("Unsupported speech-worker operation.")
        except Exception as error:
            send({"id": request_id, "ok": False, "error": str(error)})
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
