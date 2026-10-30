#!/usr/bin/env python3
"""Aggregate LexiLens metadata JSONL; never copy messages, text or credentials."""

import argparse
import json
import math
import re
import sys
from collections import Counter, defaultdict
from pathlib import Path


def number(value):
    if isinstance(value, (int, float)) and not isinstance(value, bool):
        if math.isfinite(value) and value >= 0:
            return float(value)
    return None


def distribution(values):
    values = sorted(values)
    if not values:
        return {"samples": 0, "p50": None, "p95": None, "min": None, "max": None}

    def percentile(p):
        position = (len(values) - 1) * p
        low = math.floor(position)
        high = math.ceil(position)
        return round(values[low] + (values[high] - values[low]) * (position - low), 3)

    return {"samples": len(values), "p50": percentile(.5), "p95": percentile(.95),
            "min": round(values[0], 3), "max": round(values[-1], 3)}


def summarize(paths, evidence_kind="unspecified"):
    counts = Counter()
    durations = defaultdict(list)
    duration_fields = defaultdict(Counter)
    request_durations = defaultdict(list)
    request_counts = Counter()
    request_ordinals = {}
    frame_ages = defaultdict(list)
    verification_ages = defaultdict(list)
    ocr = defaultdict(list)
    overlay = defaultdict(list)
    queue_maxima = {}
    drop_maxima = {}
    fallback_counts = Counter()
    parsed = malformed = legacy = 0

    for path in paths:
        with Path(path).open(encoding="utf-8-sig") as stream:
            for line in stream:
                if not line.strip():
                    continue
                try:
                    raw = json.loads(line.lstrip("\ufeff"))
                    if not isinstance(raw, dict):
                        raise ValueError("not an event")
                    event = {key.lower(): value for key, value in raw.items()}
                    code = event.get("code", "")
                    if not isinstance(code, str) or not re.fullmatch(r"[A-Z0-9_]{1,80}", code):
                        raise ValueError("invalid event code")
                except (ValueError, TypeError, AttributeError):
                    malformed += 1
                    continue
                parsed += 1
                counts[code] += 1
                cold = event.get("coldstart")
                phase = "cold" if cold is True else "warm" if cold is False else "unknown"
                key = (code, phase)
                request_cold = event.get("requestcoldstart")
                request_phase = "cold" if request_cold is True else "warm" if request_cold is False else "unknown"
                is_request_event = code.startswith("HTTP_") or code in ("API_REQUEST_SENT", "API_REQUEST_COMPLETE", "API_REQUEST_FAILED")
                request_key = (code, request_phase)
                if is_request_event:
                    request_counts[request_key] += 1
                    ordinal = number(event.get("requestordinal"))
                    if ordinal is not None:
                        request_ordinals[request_key] = max(request_ordinals.get(request_key, 0), int(ordinal))
                state = event.get("state")
                is_error_or_skipped = state in ("Error", "Skipped", 5, 3)
                duration = number(event.get("durationmilliseconds"))
                field = "DurationMilliseconds"
                if duration is None:
                    duration = number(event.get("milliseconds"))
                    field = "Milliseconds"
                    # Legacy zero means either unmeasured or zero; do not invent a sample.
                    if duration == 0:
                        duration = None
                if event.get("monotonictimestamp") is None:
                    legacy += 1
                if duration is not None and not is_error_or_skipped and "CACHE" not in code:
                    durations[key].append(duration)
                    duration_fields[key][field] += 1
                    if is_request_event:
                        request_durations[request_key].append(duration)
                    if code in ("OCR_OK", "OCR_EMPTY") and state in ("Success", "Warning", 1, 4):
                        engine = str(event.get("ocrengine") or "")
                        engine = "PaddleOCR" if "PaddleOCR" in engine else "Windows OCR" if "Windows OCR" in engine else "unknown"
                        ocr[(engine, phase)].append(duration)
                age = number(event.get("frameagemilliseconds"))
                if age is not None:
                    frame_ages[key].append(age)
                    if code == "OVERLAY_VISIBLE" and state in ("Success", 1):
                        overlay[phase].append(age)
                verification_age = number(event.get("frameverificationagemilliseconds"))
                if verification_age is not None:
                    verification_ages[key].append(verification_age)
                area = event.get("area")
                area = area if isinstance(area, int) and not isinstance(area, bool) else 0
                queue = number(event.get("queuelength"))
                dropped = number(event.get("droppedcount"))
                counter_key = (area, code)
                if queue is not None:
                    queue_maxima[counter_key] = max(queue_maxima.get(counter_key, 0), int(queue))
                if dropped is not None:
                    drop_maxima[counter_key] = max(drop_maxima.get(counter_key, 0), int(dropped))
                reason = event.get("fallbackreason")
                if reason:
                    # Export reason codes only; arbitrary diagnostic prose is not copied.
                    reason = reason if isinstance(reason, str) and re.fullmatch(r"[a-zA-Z0-9_:-]{1,100}", reason) else "unclassified"
                    fallback_counts[reason] += 1

    return {
        "schemaVersion": 1,
        "evidenceKindDeclared": evidence_kind,
        "serviceVerification": "not_determined_from_metadata",
        "inputFiles": len(paths), "parsedEvents": parsed, "malformedLines": malformed,
        "eventsWithoutMonotonicTimestamp": legacy,
        "percentileMethod": "linear interpolation at (n-1)*p; milliseconds",
        "durationByCode": [{"code": code, "phase": phase, **distribution(values),
                            "sourceFields": dict(duration_fields[(code, phase)])}
                           for (code, phase), values in sorted(durations.items())],
        "requestDurationByCode": [{"code": code, "requestPhase": phase, **distribution(values)}
                                  for (code, phase), values in sorted(request_durations.items())],
        "requestEventCounts": [{"code": code, "requestPhase": phase, "events": count,
                                "maximumRequestOrdinalObserved": request_ordinals.get((code, phase))}
                               for (code, phase), count in sorted(request_counts.items())],
        "ocrInferenceByEngine": [{"engine": engine, "phase": phase, **distribution(values)}
                                 for (engine, phase), values in sorted(ocr.items())],
        "frameAgeByCode": [{"code": code, "phase": phase, **distribution(values)}
                           for (code, phase), values in sorted(frame_ages.items())],
        "frameVerificationAgeByCode": [{"code": code, "phase": phase, **distribution(values)}
                                       for (code, phase), values in sorted(verification_ages.items())],
        "validOverlayAge": [{"phase": phase, **distribution(values)} for phase, values in sorted(overlay.items())],
        "queueMaxima": [{"area": area, "code": code, "maximumObserved": value}
                        for (area, code), value in sorted(queue_maxima.items())],
        "droppedCounterMaxima": [{"area": area, "code": code, "maximumObserved": value}
                                for (area, code), value in sorted(drop_maxima.items())],
        "fallbackEventCounts": dict(sorted(fallback_counts.items())),
        "eventCounts": dict(sorted(counts.items())),
        "unmeasured": {"characterErrorRate": None, "sentenceRecognitionAccuracy": None,
                       "translationAccuracy": None, "gameFrameTimeMilliseconds": None,
                       "missedSentences": None, "expiredTranslations": None},
        "limitations": [
            "Evidence kind is supplied by the operator; the script cannot verify game or service provenance.",
            "No transcript, screenshot, message, URL, key or input path is copied into this summary.",
            "OVERLAY_VISIBLE age is application render observation, not physical screen presentation latency.",
            "Counter maxima are not totals; independent areas, restarts and rotated logs must not be summed.",
            "Cache/skipped/error events are excluded from durations; OCR inference uses OCR_OK/OCR_EMPTY only.",
            "Missing ground truth and game presentation telemetry remain null; stale-drop counts are not missed sentences.",
            "Use separate logs for each backend, service, scene and cold/warm run; do not mix baselines.",
            "ColdStart describes source-frame/OCR startup; HTTP request groups use RequestColdStart independently.",
        ],
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("logs", nargs="+", type=Path, help="pipeline JSONL files, oldest first")
    parser.add_argument("--output", type=Path)
    parser.add_argument("--evidence-kind", choices=("unspecified", "engineering-smoke", "game-replay", "real-game"), default="unspecified")
    args = parser.parse_args()
    try:
        report = summarize(args.logs, args.evidence_kind)
        result = json.dumps(report, ensure_ascii=False, indent=2, allow_nan=False) + "\n"
        if args.output:
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(result, encoding="utf-8")
        else:
            sys.stdout.write(result)
    except (OSError, UnicodeError):
        parser.exit(2, "Unable to read logs or write the summary. Check file paths and permissions.\n")


if __name__ == "__main__":
    main()
