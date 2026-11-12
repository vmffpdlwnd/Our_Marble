#!/usr/bin/env python3
"""
번역표(Assets/Resources/Localization/en.txt)에 빠진 한국어 글을 찾는다.

쓰는 법 (저장소 맨 위 폴더에서)
    python3 tools/check_translations.py            씬과 프리팹의 글자, 땅 이름, 카드 이름을 점검
    python3 tools/check_translations.py --code     코드 안의 한국어 문자열도 점검 (로그와 주석, 개발자용 문장은 뺀다)
    python3 tools/check_translations.py --code --all   개발자용으로 보이는 문장까지 모두 보여 준다
    python3 tools/check_translations.py --table 다른경로/en.txt

빠진 글이 있으면 목록을 보여 주고 종료 코드 1로 끝난다. 필요한 것: Python 3, PyYAML (pip install pyyaml)
번역표의 키는 코드와 씬에 적힌 한국어와 한 글자도 다르면 안 된다. 그래서 이 스크립트도 글자 그대로 비교한다.
"""
import argparse, glob, json, os, re, sys

try:
    import yaml
except ImportError:
    sys.exit('PyYAML이 필요합니다: pip install pyyaml')

HAN = re.compile(r'[가-힣]')
SKIP_DIRS = ('TextMesh Pro', 'Photon', 'Library', 'Demigiant', 'Packages')
# 개발용 화면과 사용하지 않는 파일은 번역하지 않는다
SKIP_CODE = ('PhotonDebugPanel', 'UIDebugPanel', 'HostStateCheck', 'BasicBotStrategy')
# 개발자가 보는 오류 설명은 "~합니다/~습니다"로 끝나고, 화면에 보이는 문구는 "~요"로 끝난다. 영어 이름(PlayFab 등)이나 화살표가 있어도 개발자용이다.
DEV_TEXT = re.compile(r'(니다|니다\.|\))\s*$|PlayFab|Photon|→|[A-Za-z_]{3,}\(')


def read_text(path):
    data = open(path, 'rb').read()
    for enc in ('utf-8-sig', 'utf-16', 'cp949'):
        try:
            return data.decode(enc)
        except Exception:
            pass
    return data.decode('utf-8', errors='replace')


def load_table(path):
    keys = set()
    for line in read_text(path).split('\n'):
        line = line.rstrip('\r')
        if line and not line.startswith('#') and '\t' in line:
            keys.add(line.split('\t')[0].replace('\\n', '\n').replace('\\t', '\t'))
    return keys


def unity_docs(path):
    raw = read_text(path)
    raw = re.sub(r'(?m)^--- !u!\d+ &-?\d+.*$', '---', raw)
    raw = re.sub(r'(?m)^%.*$', '', raw)
    return [d for d in yaml.safe_load_all(raw) if isinstance(d, dict)]


def walk(root, exts):
    for d, dirs, files in os.walk(root):
        dirs[:] = [x for x in dirs if x not in SKIP_DIRS and not x.startswith('.')]
        for f in files:
            if f.endswith(exts):
                yield os.path.join(d, f)


def check_scenes(keys):
    """씬과 프리팹에 적힌 TMP 글자(m_text)."""
    missing = []
    for path in sorted(walk('Assets', ('.unity', '.prefab'))):
        seen = []
        for doc in unity_docs(path):
            for comp in doc.values():
                if isinstance(comp, dict) and isinstance(comp.get('m_text'), str):
                    s = comp['m_text']
                    if HAN.search(s) and s not in keys and s not in seen:
                        seen.append(s)
        missing += [(path, s) for s in seen]
    return missing


def check_data(keys):
    """땅 이름(properties.json)과 황금열쇠 카드(ScriptableObject)."""
    missing = []
    pj = 'Assets/Resources/Data/properties.json'
    if os.path.exists(pj):
        data = json.loads(read_text(pj))
        for p in (data['properties'] if isinstance(data, dict) else data):
            name = p.get('cityName')
            if name and name not in keys:
                missing.append((pj, name))
    for path in sorted(glob.glob('Assets/04.ScriptableObject/Card/*.asset')):
        for doc in unity_docs(path):
            for comp in doc.values():
                if isinstance(comp, dict):
                    for k in ('cardName', 'description'):
                        v = comp.get(k)
                        if isinstance(v, str) and v and v not in keys:
                            missing.append((path, v))
    return missing


def check_code(keys, show_all=False):
    """코드의 한국어 문자열. $"..." 보간 문자열은 따로 센다. 개발자용으로 보이는 문장은 show_all일 때만 센다."""
    missing, interpolated = [], 0
    skip_line = re.compile(r'Debug\.|Log\(|LogWarning|LogError|Tooltip|\[Header|\[SerializeField\]')
    for path in sorted(walk('Assets', ('.cs',))):
        if any(s in path for s in SKIP_CODE) or '/Editor/' in path or '_temp' in path:
            continue
        for n, line in enumerate(read_text(path).split('\n'), 1):
            code = line.split('//')[0]
            if skip_line.search(code):
                continue
            for m in re.finditer(r'(\$?)"((?:[^"\\]|\\.)*)"', code):
                s = m.group(2).replace('\\n', '\n').replace('\\t', '\t')  # 코드의 \n, \t는 번역표 키처럼 실제 줄바꿈과 탭으로 바꿔 비교한다
                if not HAN.search(s):
                    continue
                if m.group(1) == '$':
                    interpolated += 1
                elif s not in keys:
                    if show_all or not DEV_TEXT.search(s):
                        missing.append((f'{path}:{n}', s))
    return missing, interpolated


def show(title, rows):
    print(f'\n== {title}: {len(rows)}개')
    last = None
    for where, s in rows:
        if where != last:
            print(f'  {where}')
            last = where
        print(f'      {s!r}'[:150])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--table', default='Assets/Resources/Localization/en.txt')
    ap.add_argument('--code', action='store_true', help='코드 안의 한국어 문자열도 점검')
    ap.add_argument('--all', action='store_true', help='--code와 함께: 개발자용으로 보이는 문장도 모두 보여 줌')
    args = ap.parse_args()

    keys = load_table(args.table)
    print(f'번역표 키 {len(keys)}개 ({args.table})')

    bad = check_scenes(keys)
    show('씬과 프리팹에서 번역표에 없는 글', bad)
    data = check_data(keys)
    show('땅 이름과 카드에서 번역표에 없는 글', data)
    total = len(bad) + len(data)

    if args.code:
        code, interpolated = check_code(keys, args.all)
        show('코드에서 번역표에 없는 글 (Loc.T 없이 쓴 것과 로그가 아닌 메시지 후보)', code)
        print(f'\n(참고: 값이 끼는 $"..." 문자열 {interpolated}개는 Loc.T("... {{0}} ...", 값)으로 고쳤는지 눈으로 확인해야 한다)')
        total += len(code)

    print('\n' + ('빠진 글이 없습니다.' if total == 0 else f'빠진 글 {total}개'))
    sys.exit(1 if total else 0)


if __name__ == '__main__':
    main()