"""Fails if any tracked path cannot be checked out on Windows.

Run from the repository root (CI runs it on Linux, where such paths can still be checked out):

    python tools/check-paths.py

Catches what broke two commits in October 2026: a file named with the full text of a source file (newlines,
quotes, colons). Also rejects reserved device names, trailing dots or spaces, and paths too long for a default
Windows checkout.
"""
import re
import subprocess
import sys

MAX_PATH = 240  # leaves room for the checkout folder under the 260-character Windows limit
INVALID = re.compile(r'[<>:"|?*\\\x00-\x1f]')
RESERVED = re.compile(r'^(con|prn|aux|nul|com[0-9]|lpt[0-9])(\..*)?$', re.IGNORECASE)

paths = subprocess.run(['git', 'ls-files', '-z'], capture_output=True, check=True).stdout.decode('utf-8').split('\0')
problems = []
for path in filter(None, paths):
    shown = path if len(path) <= 120 else path[:117] + '...'
    shown = shown.replace('\n', '\\n').replace('\r', '\\r')
    if len(path) > MAX_PATH:
        problems.append(f'{shown}: {len(path)} characters (limit {MAX_PATH})')
    for part in path.split('/'):
        if INVALID.search(part):
            problems.append(f'{shown}: character not allowed on Windows in "{part[:40]}"')
            break
        if RESERVED.match(part):
            problems.append(f'{shown}: "{part}" is a reserved name on Windows')
            break
        if part.endswith(('.', ' ')):
            problems.append(f'{shown}: "{part}" ends with a dot or a space')
            break

for p in problems:
    print(p)
print(f'{len(problems)} problem(s) in {len([p for p in paths if p])} tracked paths')
sys.exit(1 if problems else 0)
