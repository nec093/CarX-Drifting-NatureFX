#!/usr/bin/env python3
# Generates, from the F8 window (src/NatureFX/NatureFXWindow.cs), the debug page's settings tab:
#   src/NatureFX/Tools/SettingsControls.g.cs  every slider, switch and chip row that edits a setting directly (S.Name),
#                                             with its tab, card and range (DevControls)
#   src/NatureFX/Tools/SettingsMeta.g.cs      the page's texts (/meta.json): each setting's label and description (the
#                                             window's hint and the explanation lines under it), each card's, in
#                                             English and Japanese (tools/settings_ja.json; English where it has none)
# Settings no window control edits plainly are added at run time (reflection), described in settings_ja.json if there.
# Run it after changing the window or settings_ja.json: python3 tools/gen_settings_controls.py
# --dump-en <file>: also writes the English texts as JSON (what settings_ja.json translates)
import json
import pathlib
import re
import sys

root = pathlib.Path(__file__).resolve().parent.parent
window = (root / 'src/NatureFX/NatureFXWindow.cs').read_text(encoding='utf-8')
ja_path = root / 'tools/settings_ja.json'
out_controls = root / 'src/NatureFX/Tools/SettingsControls.g.cs'
out_meta = root / 'src/NatureFX/Tools/SettingsMeta.g.cs'


# ---- a small scanner: calls with their argument text, string literals joined
def call_args(text, start):
    """text[start] is '(': the arguments' text up to the matching ')', and the index after it"""
    depth, i, n = 0, start, len(text)
    while i < n:
        c = text[i]
        if c == '"':
            verbatim = i > 0 and text[i - 1] == '@'
            i += 1
            while i < n:
                if text[i] == '\\' and not verbatim:
                    i += 2
                    continue
                if text[i] == '"':
                    if verbatim and i + 1 < n and text[i + 1] == '"':
                        i += 2
                        continue
                    break
                i += 1
        elif c == '(':
            depth += 1
        elif c == ')':
            depth -= 1
            if depth == 0:
                return text[start + 1:i], i + 1
        i += 1
    return text[start + 1:], n


def split_args(args):
    out, depth, cur, i = [], 0, '', 0
    while i < len(args):
        c = args[i]
        if c == '"':
            j = i + 1
            while j < len(args) and args[j] != '"':
                j += 2 if args[j] == '\\' else 1
            cur += args[i:j + 1]
            i = j + 1
            continue
        if c in '([{':
            depth += 1
        elif c in ')]}':
            depth -= 1
        if c == ',' and depth == 0:
            out.append(cur.strip())
            cur = ''
        else:
            cur += c
        i += 1
    if cur.strip():
        out.append(cur.strip())
    return out


def literal(expr):
    """a plain string literal, or literals joined with +; None for anything computed ($"..", variables)"""
    expr = expr.strip()
    parts = re.split(r'"\s*\+\s*"', expr)
    if not (expr.startswith('"') and expr.endswith('"')):
        return None
    joined = '"' + ''.join(p.strip('"') if k in (0, len(parts) - 1) else p for k, p in enumerate(parts)) + '"'
    # whatever is left between the quotes must be string text only
    inner = joined[1:-1]
    if re.search(r'(?<!\\)"', inner):
        return None
    return bytes(inner, 'utf-8').decode('unicode_escape').encode('latin-1').decode('utf-8')


tabs = re.search(r'string\[\] Tabs = \{(.*?)\};', window, re.S)
tab_names = re.findall(r'"([^"]*)"', tabs.group(1)) if tabs else []
tab_of = {}
for m in re.finditer(r'case (\d+): (Draw\w+)\(\); break;', window):
    i = int(m.group(1))
    tab_of[m.group(2)] = tab_names[i] if i < len(tab_names) else m.group(2)[4:]

arrays = {}
for m in re.finditer(r'static readonly string\[\] (\w+) = \{(.*?)\};', window, re.S):
    arrays[m.group(1)] = '{ ' + ', '.join(re.findall(r'"(?:[^"\\]|\\.)*"', m.group(2))) + ' }'

token = re.compile(r'private void (Draw\w+)\(|Widgets\.(BeginCard|Slider|IntSlider|Switch|Chips|Info)\(|int (\w+) = Mathf\.Clamp\(S\.(\w+),')
entries, seen, cards = [], set(), {}
cur_method, cur_card, locals_, last = None, '', {}, None
pos = 0
while True:
    m = token.search(window, pos)
    if not m:
        break
    pos = m.end()
    if m.group(1):
        cur_method, cur_card, locals_, last = m.group(1), '', {}, None
        continue
    if cur_method not in tab_of:
        continue
    if m.group(3):
        locals_[m.group(3)] = m.group(4)
        continue
    kind = m.group(2)
    args_text, pos = call_args(window, m.end() - 1)
    args = split_args(args_text)
    group = tab_of[cur_method] + (' / ' + cur_card if cur_card else '')
    if kind == 'BeginCard':
        cur_card = literal(args[0]) or ''
        group = tab_of[cur_method] + (' / ' + cur_card if cur_card else '')
        hint = literal(args[1]) if len(args) > 1 else None
        cards.setdefault(group, hint or '')
        last = None
        continue
    if kind == 'Info':
        text = literal(args[0]) if args else None
        if text and last is not None:
            last['help'] = (last['help'] + ' ' + text).strip()
        continue
    entry = None
    if kind in ('Slider', 'IntSlider') and len(args) >= 5:
        prop = re.fullmatch(r'S\.(\w+)', args[1])
        target = re.fullmatch(r'v => S\.(\w+) = v', args[4])
        if prop and target and prop.group(1) == target.group(1):
            lo, hi = (float(a.rstrip('f')) if re.fullmatch(r'-?[0-9.]+f?', a) else None for a in args[2:4])
            if lo is not None and hi is not None:
                entry = dict(kind='Slider', prop=prop.group(1), lo=lo, hi=hi, integer=kind == 'IntSlider')
    elif kind == 'Switch' and len(args) >= 3:
        prop = re.fullmatch(r'S\.(\w+)', args[1])
        target = re.fullmatch(r'v => S\.(\w+) = v', args[2])
        if prop and target and prop.group(1) == target.group(1):
            entry = dict(kind='Switch', prop=prop.group(1), hint=literal(args[3]) if len(args) > 3 else None)
    elif kind == 'Chips' and len(args) >= 2:
        var = re.fullmatch(r'ref (\w+)', args[0])
        if var and var.group(1) in locals_:
            expr = args[1]
            if expr.startswith('new[]'):
                options = 'new[] ' + expr[len('new[]'):].strip()
            elif expr in arrays:
                options = 'new[] ' + arrays[expr]
            else:
                # a public array elsewhere (WeatherController.Names, Rendering.Dlaa.Qualities)
                options = 'NatureFX.' + expr if expr.startswith('Rendering.') else expr
            entry = dict(kind='Chips', prop=locals_[var.group(1)], options=options)
    if entry is None:
        last = None
        continue
    if entry['prop'] in seen:
        last = None
        continue
    seen.add(entry['prop'])
    entry['group'] = group
    entry['label'] = (literal(args[0]) if kind != 'Chips' else None) or (cur_card if kind == 'Chips' else entry['prop'])
    entry['help'] = entry.get('hint') or ''
    entries.append(entry)
    last = entry


def cs(s):
    return '"' + s.replace('\\', '\\\\').replace('"', '\\"').replace('\n', '\\n') + '"'


def f(x):
    return repr(float(x)) + 'f'


lines = []
for e in entries:
    g, p, label = cs(e['group']), e['prop'], cs(e['label'])
    if e['kind'] == 'Slider':
        lo, hi = e['lo'], e['hi']
        step = 1.0 if e['integer'] else max((hi - lo) / 200.0, 0.0001)
        set_ = f'v => S.{p} = Mathf.RoundToInt(v)' if e['integer'] else f'v => S.{p} = v'
        get = f'() => D.{p}'
        lines.append(f'\t\t\tSetting({g}, {cs(p)}, {label}, () => S.{p}, {set_}, {get}, {f(lo)}, {f(hi)}, {f(step)});')
    elif e['kind'] == 'Switch':
        lines.append(f'\t\t\tSettingSwitch({g}, {cs(p)}, {label}, () => S.{p}, v => S.{p} = v, () => D.{p});')
    else:
        lines.append(f'\t\t\tSettingChips({g}, {cs(p)}, {label}, {e["options"]}, () => S.{p}, v => S.{p} = v, () => D.{p});')

out_controls.write_text('''// Generated by tools/gen_settings_controls.py from NatureFXWindow.cs: do not edit by hand.
using NatureFX.Nature;
using UnityEngine;

namespace NatureFX.Tools {
	public static partial class DevControls {
		// the F8 window's controls of the settings, for the debug page's settings tab (tab / card, the setting, its
		// label, the current value, the default)
		private static void BuildWindowSettings() {
''' + '\n'.join(lines) + '''
		}
	}
}
''', encoding='utf-8', newline='\n')

# ---- the settings' own comments (Settings.cs's ISettings): the help where the window has none, and the settings it has
# no plain control for (named by their property)
settings_cs = (root / 'src/NatureFX/Settings.cs').read_text(encoding='utf-8')
interface = settings_cs[settings_cs.index('public interface ISettings'):settings_cs.index('internal class SettingsDefaults')]
comments, pending = {}, []
for line in interface.splitlines():
    t = line.strip()
    if t.startswith('//'):
        pending.append(t[2:].strip())
        continue
    m = re.match(r'public (bool|int|float|string) (\w+) \{ get; set; \}\s*(?://\s*(.*))?', t)
    if m:
        text = (m.group(3) or '').strip()
        if not text and pending:
            text = ' '.join(pending)
        comments[m.group(2)] = (m.group(1), text)
        if m.group(3):
            pass
    if not t:
        pending = []
    elif not t.startswith('//') and not m:
        pending = []
    elif m and m.group(3):
        pass

# ---- the texts
en = {'settings': {e['prop']: {'label': e['label'], 'help': e['help'] or comments.get(e['prop'], ('', ''))[1]} for e in entries},
      'groups': {g: {'name': g, 'help': h} for g, h in cards.items() if any(e['group'] == g for e in entries)}}
for name, (kind, text) in comments.items():
    if name not in en['settings'] and name != 'ConfigVersion' and kind != 'string':
        en['settings'][name] = {'label': name, 'help': text}
ja = json.loads(ja_path.read_text(encoding='utf-8')) if ja_path.exists() else {}
meta = {'en': en, 'ja': {'settings': ja.get('settings', {}), 'groups': ja.get('groups', {}), 'ui': ja.get('ui', {})}}
if '--dump-en' in sys.argv:
    dump = pathlib.Path(sys.argv[sys.argv.index('--dump-en') + 1])
    dump.write_text(json.dumps(en, ensure_ascii=False, indent=1), encoding='utf-8')
text = json.dumps(meta, ensure_ascii=False, separators=(',', ':'))
out_meta.write_text('''// Generated by tools/gen_settings_controls.py (NatureFXWindow.cs, tools/settings_ja.json): do not edit by hand.
namespace NatureFX.Tools {
	public static class SettingsMeta {
		// the debug page's settings texts, English and Japanese: /meta.json
		public const string Json = @"''' + text.replace('"', '""') + '''";
	}
}
''', encoding='utf-8', newline='\n')
missing = [p for p in en['settings'] if p not in ja.get('settings', {})]
print(f'{len(entries)} settings controls, {len(en["settings"])} settings, {len(en["groups"])} cards; Japanese missing for {len(missing)}')
