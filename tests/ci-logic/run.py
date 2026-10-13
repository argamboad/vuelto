#!/usr/bin/env python3
"""CI shell-logic harness (v4 audit T8, R136).

Much of CI's pass/fail logic is shell, awk, jq and Python living inside the workflows and scripts, and the C#
tests only copy its regexes. This runs the real thing: each target in `targets` is either a block of a workflow
step, cut out between `# ci-logic begin: <name>` and `# ci-logic end: <name>` anchors, or a whole script. Every
case under cases/<name>/<case>/ runs it in a scratch directory with the real tools, fake `git`/`dotnet`/`curl`
from stubs/ first on PATH, and checks the exit code and output against `expect`. A block runs as GitHub runs a
`run:` step (`bash -e`, no pipefail; the workflow pins that), once per workflow file that holds it.

Case layout:
  env      KEY=VALUE lines exported for the run (${{ expr }} in a block reads CI_<expr, non-alnum -> _>)
  args     arguments for a script target, one per line
  files/   copied into the scratch directory (fixtures: diffs, QA plans, .trx files, API pages, ...)
  expect   `exit N`, then `+text` (must appear in the output) / `-text` (must not). The output includes the
           step's $GITHUB_OUTPUT after a `## GITHUB_OUTPUT` line.
  requires repo paths the case needs, one per line; the case is skipped where one is absent (an app has no course)
  without  repo paths left out of the case's scratch copy (only docs/tutorial/gen_coverage.py is ever copied in)

Downstream (an app: its platform-stamp.json names a commit), a target whose files are all absent — the platform's
producer-only release machinery (Pkg 2.5) — is skipped and said so; in the platform a missing file fails.

Every `::error::` a target can print must be produced by at least one of its cases, or be listed as `exempt`
in `targets` with the reason. Entry point: tests/ci-logic/run.sh; EnforcementGateTests.CiShellLogic_* runs it.
"""
import os, re, shutil, stat, subprocess, sys, tempfile, textwrap

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, '..', '..'))
EXPR = re.compile(r'\$\{\{\s*(.+?)\s*\}\}')
ERROR = re.compile(r'::error[^:]*::(.*?)(?:(?<!\\)"|$)', re.M)


def read(path):
    with open(path, encoding='utf-8') as f:
        return f.read().replace('\r\n', '\n')


def expr_var(expr):
    return 'CI_' + re.sub(r'[^A-Za-z0-9]+', '_', expr).strip('_')


def parse_targets():
    targets, exempt = [], {}
    for raw in read(os.path.join(HERE, 'targets')).split('\n'):
        line = raw.split('#', 1)[0].strip() if not raw.lstrip().startswith('exempt') else raw.strip()
        if not line:
            continue
        kind, name, rest = (line.split(None, 2) + [''])[:3]
        if kind == 'exempt':
            m = re.match(r'"([^"]+)"\s+(.+)', rest)
            assert m, f'targets: exempt needs "<message start>" <reason>: {raw}'
            exempt.setdefault(name, []).append(m.group(1))
        else:
            targets.append((kind, name, rest.split()))
    return targets, exempt


def block(source, name):
    lines = read(os.path.join(ROOT, source)).split('\n')
    begin = [i for i, l in enumerate(lines) if l.strip() == f'# ci-logic begin: {name}']
    end = [i for i, l in enumerate(lines) if l.strip() == f'# ci-logic end: {name}']
    if len(begin) != 1 or len(end) != 1 or begin[0] > end[0]:
        raise SystemExit(f'{source}: needs exactly one "# ci-logic begin: {name}" before one "# ci-logic end: {name}"')
    body = textwrap.dedent('\n'.join(lines[begin[0] + 1:end[0]]))
    return EXPR.sub(lambda m: '${' + expr_var(m.group(1)) + '}', body) + '\n'


def copy_text_tree(src, dst):
    for base, _, files in os.walk(src):
        for f in files:
            s = os.path.join(base, f)
            d = os.path.join(dst, os.path.relpath(s, src))
            os.makedirs(os.path.dirname(d), exist_ok=True)
            data = open(s, 'rb').read()
            open(d, 'wb').write(data.replace(b'\r\n', b'\n'))


def listed(case_dir, name):
    """The repo paths a case lists in its `requires` / `without` file, one per line (none when the file is absent)."""
    path = os.path.join(case_dir, name)
    return [l.strip() for l in read(path).split('\n') if l.strip() and not l.startswith('#')] if os.path.exists(path) else []


def is_the_platform():
    """True in the platform itself (its stamp's commit is null): producer-only targets and course cases apply only here."""
    stamp = os.path.join(ROOT, 'platform-stamp.json')
    return os.path.exists(stamp) and re.search(r'"commit"\s*:\s*null', read(stamp)) is not None


def run_case(kind, name, source, case_dir):
    work = tempfile.mkdtemp(prefix=f'ci-logic-{name}-')
    try:
        for scripts in ('.github/scripts',):  # what the blocks call
            copy_text_tree(os.path.join(ROOT, scripts), os.path.join(work, scripts))
        # The classifier asks the course generator for its quoted files (Pkg 1.4); a case brings the lessons in files/.
        # A downstream app has no course, and its classifier never calls the generator.
        # A case's `without` file names repo paths left out of its scratch copy (one runs as a repo with no course).
        generator = os.path.join(ROOT, 'docs', 'tutorial', 'gen_coverage.py')
        if os.path.exists(generator) and 'docs/tutorial/gen_coverage.py' not in listed(case_dir, 'without'):
            os.makedirs(os.path.join(work, 'docs', 'tutorial'), exist_ok=True)
            open(os.path.join(work, 'docs', 'tutorial', 'gen_coverage.py'), 'wb').write(open(generator, 'rb').read())
        if os.path.isdir(os.path.join(case_dir, 'files')):
            copy_text_tree(os.path.join(case_dir, 'files'), work)
        stubs = os.path.join(work, '.stubs')
        copy_text_tree(os.path.join(HERE, 'stubs'), stubs)
        for f in os.listdir(stubs):
            os.chmod(os.path.join(stubs, f), 0o755)

        env = {k: v for k, v in os.environ.items() if not k.startswith(('CI_', 'FAKE_', 'GITHUB_'))}
        env.update(PATH=stubs + os.pathsep + os.environ.get('PATH', ''), HOME=work, LC_ALL='C',
                   GITHUB_OUTPUT=os.path.join(work, '.github_output'))
        if os.path.exists(os.path.join(case_dir, 'env')):
            for line in read(os.path.join(case_dir, 'env')).split('\n'):
                if line.strip() and not line.startswith('#'):
                    k, _, v = line.partition('=')
                    env[k.strip()] = v
        args = [a for a in read(os.path.join(case_dir, 'args')).split('\n') if a] if os.path.exists(os.path.join(case_dir, 'args')) else []

        if kind == 'block':
            script = os.path.join(work, '.block.sh')
            open(script, 'w', encoding='utf-8').write(block(source, name))
            cmd = ['bash', '-e', script]
        else:
            cmd = ['bash', os.path.join(work, source)] + args
        p = subprocess.run(cmd, cwd=work, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=120)
        out = p.stdout.decode('utf-8', 'replace')
        if os.path.exists(env['GITHUB_OUTPUT']):
            out += '\n## GITHUB_OUTPUT\n' + read(env['GITHUB_OUTPUT'])

        failures = []
        for line in read(os.path.join(case_dir, 'expect')).split('\n'):
            if line.startswith('exit '):
                if p.returncode != int(line[5:]):
                    failures.append(f'exit {p.returncode}, expected {line[5:]}')
            elif line.startswith('+') and line[1:] not in out:
                failures.append(f'missing: {line[1:]}')
            elif line.startswith('-') and line[1:] in out:
                failures.append(f'unexpected: {line[1:]}')
        return out, failures
    finally:
        shutil.rmtree(work, ignore_errors=True)


def error_patterns(text):
    """Each ::error:: message as a regex, its $variables, $(...) and Python f-string {fields} as wildcards."""
    pats = []
    for m in ERROR.finditer(text):
        parts = re.split(r'\$\{[^}]*\}|\$\([^)]*\)|\$\w+|\{[^{}]*\}', m.group(1))
        pats.append((m.group(1), re.compile('.*?'.join(re.escape(p) for p in parts))))
    return pats


def main():
    targets, exempt = parse_targets()
    total = failed = 0
    platform = is_the_platform()
    for kind, name, sources in targets:
        # Downstream, a target over producer-only files (the platform's release machinery, Pkg 2.5) has neither its file
        # nor its cases: skipped, and said so. In the platform a missing file still fails (block() raises).
        if not platform and not any(os.path.exists(os.path.join(ROOT, s)) for s in sources):
            print(f'skip {name}: {", ".join(sources)} is the platform\'s alone (producer-only)'); continue
        case_root = os.path.join(HERE, 'cases', name)
        cases = sorted(d for d in os.listdir(case_root) if os.path.isdir(os.path.join(case_root, d))) if os.path.isdir(case_root) else []
        if not cases:
            print(f'FAIL {name}: no cases under tests/ci-logic/cases/{name}/'); failed += 1; continue
        for source in sources:
            text = block(source, name) if kind == 'block' else read(os.path.join(ROOT, source))
            outputs = []
            for case in cases:
                # A case's `requires` file names repo paths it needs (the course cases need the course's generator):
                # where one is absent — an app — the case is skipped, and said so.
                missing = [p for p in listed(os.path.join(case_root, case), 'requires') if not os.path.exists(os.path.join(ROOT, p))]
                if missing:
                    print(f'skip {name}/{case}: this repo has no {", ".join(missing)}'); continue
                total += 1
                out, failures = run_case(kind, name, source, os.path.join(case_root, case))
                outputs.append(out)
                label = f'{name}/{case} [{source}]'
                if failures:
                    failed += 1
                    print(f'FAIL {label}: ' + '; '.join(failures))
                    print(textwrap.indent(out.rstrip()[-3000:], '     | '))
                else:
                    print(f'ok   {label}')
            for message, pattern in error_patterns(text):
                if any(message.startswith(e) for e in exempt.get(name, [])):
                    continue
                if not any(pattern.search(o) for o in outputs):
                    failed += 1
                    print(f'FAIL {name} [{source}]: no case produces ::error::{message[:90]}')
    print(f'\n{total - failed if failed <= total else 0} of {total} cases passed' if not failed else f'\n{failed} failure(s) across {total} cases')
    return 1 if failed else 0


if __name__ == '__main__':
    sys.exit(main())
