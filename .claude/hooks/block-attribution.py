"""PreToolUse hook: refuse git/gh commands that would write assistant attribution into history."""
import json
import os
import re
import sys

FORBIDDEN = re.compile(
    r"claude-session|claude\.ai|co-authored-by:\s*claude|generated with claude|anthropic|claude code",
    re.IGNORECASE,
)
WRITES_HISTORY = re.compile(
    r"\bgit\s+(?:-\S+\s+)*(?:commit|commit-tree|merge|tag|notes|rebase|filter-branch)\b|\bgh\s+pr\s+(?:create|edit|merge)\b|\bgh\s+release\s+(?:create|edit)\b",
    re.IGNORECASE,
)
MESSAGE_FILE = re.compile(r"(?:-F|--file)[=\s]+[\"']?([^\s\"']+)")


def main() -> None:
    try:
        payload = json.load(sys.stdin)
    except Exception:
        return
    command = str((payload.get("tool_input") or {}).get("command") or "")
    if not WRITES_HISTORY.search(command):
        return

    text = command
    project = os.environ.get("CLAUDE_PROJECT_DIR") or os.getcwd()
    for path in MESSAGE_FILE.findall(command):
        candidate = path if os.path.isabs(path) else os.path.join(project, path)
        try:
            with open(candidate, encoding="utf-8", errors="ignore") as handle:
                text += "\n" + handle.read()
        except OSError:
            pass

    match = FORBIDDEN.search(text)
    if not match:
        return

    print(json.dumps({
        "hookSpecificOutput": {
            "hookEventName": "PreToolUse",
            "permissionDecision": "deny",
            "permissionDecisionReason": (
                f"Blocked: '{match.group(0)}' would put assistant attribution into git history. "
                "Commit messages and PR text must describe the change only (see AGENTS.md)."
            ),
        }
    }))


if __name__ == "__main__":
    main()
