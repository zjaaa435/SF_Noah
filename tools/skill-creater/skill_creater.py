#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
skill-creater: generate reusable "skills" (prompt + workflow specs) as JSON files.

This repo doesn't ship an Agent-Skills system by default; this tool creates a
portable skill definition you can load into your own agent platform or use as
team conventions.
"""

from __future__ import annotations

import argparse
import datetime as _dt
import json
import os
from pathlib import Path
from typing import Any, Dict


REPO_ROOT = Path(__file__).resolve().parents[2]
TEMPLATES_DIR = Path(__file__).resolve().parent / "templates"


def _utc_now_iso() -> str:
    return _dt.datetime.now(tz=_dt.timezone.utc).replace(microsecond=0).isoformat()


def _read_json(path: Path) -> Dict[str, Any]:
    with path.open("r", encoding="utf-8") as f:
        return json.load(f)


def _write_json(path: Path, data: Dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
        f.write("\n")


def _apply_placeholders(obj: Any, mapping: Dict[str, str]) -> Any:
    if isinstance(obj, str):
        for k, v in mapping.items():
            obj = obj.replace(k, v)
        return obj
    if isinstance(obj, list):
        return [_apply_placeholders(x, mapping) for x in obj]
    if isinstance(obj, dict):
        return {k: _apply_placeholders(v, mapping) for k, v in obj.items()}
    return obj


def cmd_create(args: argparse.Namespace) -> int:
    template_name = args.template
    template_path = TEMPLATES_DIR / f"{template_name}.skill.json"
    if not template_path.exists():
        raise SystemExit(f"Template not found: {template_path}")

    data = _read_json(template_path)
    placeholders = {
        "{{SKILL_ID}}": args.skill_id,
        "{{SKILL_NAME}}": args.name,
        "{{CREATED_AT}}": _utc_now_iso(),
        "{{REPO_ROOT}}": str(REPO_ROOT),
    }
    data = _apply_placeholders(data, placeholders)

    out_path = Path(args.out).resolve()
    if out_path.exists() and not args.force:
        raise SystemExit(f"Output exists: {out_path} (use --force to overwrite)")

    _write_json(out_path, data)
    print(f"Created skill: {out_path}")
    return 0


def _build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(prog="skill-creater", description="Create skill JSON files from templates.")
    sub = p.add_subparsers(dest="cmd", required=True)

    c = sub.add_parser("create", help="Create a skill from a template.")
    c.add_argument("--template", required=True, help="Template name under tools/skill-creater/templates (without suffix).")
    c.add_argument("--skill-id", required=True, help="Unique id, e.g. defect-analysis.v1")
    c.add_argument("--name", required=True, help="Human friendly name.")
    c.add_argument("--out", required=True, help="Output path for the generated skill JSON.")
    c.add_argument("--force", action="store_true", help="Overwrite output if it exists.")
    c.set_defaults(func=cmd_create)

    return p


def main() -> int:
    parser = _build_parser()
    args = parser.parse_args()
    return int(args.func(args))


if __name__ == "__main__":
    raise SystemExit(main())










