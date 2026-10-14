"""Builds docs/style-guide.html: every color, text size and corner radius in the app, from the real files.

Run from the repo root:   python tools/Build-StyleGuide.py

Sections: palette (Resources/Colors.xaml), every hard-coded color in XAML and in code with where it is used and a
status against the palette, text sizes and corner radii in use with a proposed standard. Dev-only
code (Diagnostics/) and tests are left out of the code section and counted instead.
"""
import re, glob, collections, html, os, math

SKIP_DIRS = ('bin', 'obj', 'publish', 'TrayTrigger.Tests', '.git')
def files(pattern):
    return [f for f in glob.glob(pattern, recursive=True) if not f.startswith(SKIP_DIRS)]

xaml_files = [norm_f for norm_f in (f.replace(os.sep, '/') for f in files('**/*.xaml'))]
cs_files = [f for f in files('**/*.cs')
            if not f.startswith(('Diagnostics', 'tools')) and f.replace(os.sep, '/') != 'Services/AppPalette.cs']
diag_files = glob.glob('Diagnostics/*.cs')
norm = lambda f: f.replace(os.sep, '/')

app = open('Resources/Colors.xaml', encoding='utf-8').read() + open('App.xaml', encoding='utf-8').read()

# ---- palette -------------------------------------------------------------------------------------
colors = dict(re.findall(r'<Color x:Key="(\w+)">(#[0-9A-Fa-f]{6,8})</Color>', app))
palette = []   # (brush name, color key or '', hex)
for name, ref, lit in re.findall(r'<SolidColorBrush x:Key="(\w+)" Color="(?:\{StaticResource (\w+)\}|(#[0-9A-Fa-f]{6,8}))"', app):
    palette.append((name, ref, colors.get(ref, lit) if ref else lit))
palette_by_hex = collections.defaultdict(list)
for n, _, h in palette:
    palette_by_hex[h.upper()].append(n)

def rgb(h):
    h = h[1:][-6:]
    return tuple(int(h[i:i + 2], 16) for i in (0, 2, 4))
def alpha(h):
    return int(h[1:3], 16) if len(h) == 9 else 255
def lum(c):
    f = lambda v: v / 255 / 12.92 if v / 255 <= 0.03928 else ((v / 255 + 0.055) / 1.055) ** 2.4
    return 0.2126 * f(c[0]) + 0.7152 * f(c[1]) + 0.0722 * f(c[2])
def contrast(a, b):
    la, lb = sorted([lum(rgb(a)), lum(rgb(b))], reverse=True)
    return (la + 0.05) / (lb + 0.05)

bg, card = colors['ColorBackground'], colors['ColorSurface']

# ---- every color literal, with where ---------------------------------------------------------------
class Use:
    def __init__(self, file, line, prop): self.file, self.line, self.prop = file, line, prop

uses = collections.defaultdict(list)   # hex (upper, #RRGGBB or #AARRGGBB) -> [Use]
def add(h, file, line, prop):
    uses[h.upper()].append(Use(file, line, prop))

for f in xaml_files:
    if f == 'Resources/Colors.xaml':
        continue   # the definitions themselves
    in_palette_defs = (f == 'App.xaml')
    for ln, text in enumerate(open(f, encoding='utf-8').read().split('\n'), 1):
        if in_palette_defs and re.search(r'<(Color|SolidColorBrush) x:Key="\w+"', text):
            continue
        for m in re.finditer(r'(\w+(?:\.\w+)?)="(#[0-9A-Fa-f]{6,8})"', text):
            add(m.group(2), norm(f), ln, m.group(1))
        for m in re.finditer(r'>(#[0-9A-Fa-f]{6,8})</Color>', text):
            add(m.group(1), norm(f), ln, 'Color')

code_uses = []   # (hex, file, line, text)
def to_hex(a, r, g, b):
    return '#%02X%02X%02X' % (r, g, b) if a == 255 else '#%02X%02X%02X%02X' % (a, r, g, b)
for f in cs_files:
    for ln, text in enumerate(open(f, encoding='utf-8', errors='ignore').read().split('\n'), 1):
        for m in re.finditer(r'Color\.FromRgb\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\)', text):
            code_uses.append((to_hex(255, *map(int, m.groups())), norm(f), ln, text.strip()))
        for m in re.finditer(r'Color\.FromArgb\(\s*(0x[0-9A-Fa-f]+|\d+)\s*,\s*(0x[0-9A-Fa-f]+|\d+)\s*,\s*(0x[0-9A-Fa-f]+|\d+)\s*\)', text):
            code_uses.append((to_hex(255, *[int(x, 0) for x in m.groups()]), norm(f), ln, text.strip()))
        for m in re.finditer(r'Color\.FromArgb\(\s*(0x[0-9A-Fa-f]+|\d+)\s*,\s*(0x[0-9A-Fa-f]+|\d+)\s*,\s*(0x[0-9A-Fa-f]+|\d+)\s*,\s*(0x[0-9A-Fa-f]+|\d+)\s*\)', text):
            code_uses.append((to_hex(*[int(x, 0) for x in m.groups()]), norm(f), ln, text.strip()))
        for m in re.finditer(r'"(#[0-9A-Fa-f]{6,8})"', text):
            code_uses.append((m.group(1).upper(), norm(f), ln, text.strip()))
        for m in re.finditer(r'\bBrushes\.(?!Transparent\b)(\w+)', text):
            code_uses.append(('Brushes.' + m.group(1), norm(f), ln, text.strip()))
diag_count = 0
for f in diag_files:
    t = open(f, encoding='utf-8', errors='ignore').read()
    diag_count += len(re.findall(r'Color\.From(?:Rgb|Argb)\(|Brushes\.\w+', t))

named = []   # (name, file, line, prop) for White / Black etc. written as words in XAML
for f in xaml_files:
    for ln, text in enumerate(open(f, encoding='utf-8').read().split('\n'), 1):
        for m in re.finditer(r'(\w+)="(White|Black|Gray|Red|Green|Blue|Orange|Yellow|Silver|DarkGray|LightGray)"', text):
            named.append((m.group(2), norm(f), ln, m.group(1)))
transparent = sum(len(re.findall(r'="Transparent"', open(f, encoding='utf-8').read())) for f in xaml_files)

# ---- status against the palette ---------------------------------------------------------------------
palette_vals = [(n, h.upper()) for n, _, h in palette]
def status(h):
    if h in palette_by_hex:
        return 'in palette', ', '.join(palette_by_hex[h])
    best = None
    for n, ph in palette_vals:
        if alpha(h) != alpha(ph): continue
        d = math.dist(rgb(h), rgb(ph))
        if best is None or d < best[0]: best = (d, n)
    if best and best[0] <= 3: return 'merges silently', best[1]
    if best and best[0] <= 8: return 'close', best[1]
    return ('one-off' if len(uses.get(h, [])) <= 1 else 'needs a role'), (best[1] if best else '')

e = html.escape
# Text sizes: the names in Resources/Typography.xaml, and how often each is referenced.
typo = open('Resources/Typography.xaml', encoding='utf-8').read()
size_tokens = [(n, float(v)) for n, v in re.findall(r'<sys:Double x:Key="(\w+)">([\d.]+)</sys:Double>', typo)]
size_uses = collections.Counter(); size_where = collections.defaultdict(collections.Counter)
for f in xaml_files + cs_files:
    txt = open(f, encoding='utf-8', errors='ignore').read()
    for m in re.finditer(r'\{(?:Static|Dynamic)Resource (FontSize\w+)\}|AppTypography\.Size\("(FontSize\w+)"\)', txt):
        k = m.group(1) or m.group(2); size_uses[k] += 1; size_where[k][os.path.basename(f).split('.')[0]] += 1
# any number still written straight in a view
size_literals = collections.Counter()
for f in xaml_files:
    for v in re.findall(r'FontSize="([\d.]+)"', open(f, encoding='utf-8').read()):
        size_literals[float(v)] += 1
sizes = size_tokens
# Corner radii: the names in Resources/Shapes.xaml, and how often each is referenced.
shapes = open('Resources/Shapes.xaml', encoding='utf-8').read()
radius_tokens = [(n, v.replace(' ', '')) for n, v in re.findall(r'<CornerRadius x:Key="(\w+)">([\d.,\s]+)</CornerRadius>', shapes)]
radius_uses = collections.Counter(); radius_where = collections.defaultdict(collections.Counter)
for f in xaml_files + cs_files:
    txt = open(f, encoding='utf-8', errors='ignore').read()
    for m in re.finditer(r'\{(?:Static|Dynamic)Resource (Radius\w+)\}|AppShapes\.Radius\("(Radius\w+)"\)', txt):
        k = m.group(1) or m.group(2); radius_uses[k] += 1; radius_where[k][os.path.basename(f).split('.')[0]] += 1
radius_literals = collections.Counter()
for f in xaml_files:
    t = open(f, encoding='utf-8').read()
    for v in re.findall(r'CornerRadius="([\d.,\s]+)"', t):
        radius_literals[v.replace(' ', '')] += 1

# ---- summary numbers ---------------------------------------------------------------------------------
xaml_outside = [h for h in uses if h not in palette_by_hex]
st = collections.Counter(status(h)[0] for h in uses)

out = []
w = out.append
w(f"""<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>TrayTrigger style guide</title><style>
:root{{--bg:{bg};--card:{card};--text:#fff;--muted:#9E9EAA;--line:#2C2C38}}
*{{box-sizing:border-box}}body{{margin:0;background:var(--bg);color:var(--text);font:14px/1.5 "Segoe UI",system-ui,sans-serif;padding:24px 16px 64px}}
main{{max-width:1100px;margin:0 auto}}h1{{font-size:24px;margin:0 0 4px}}h2{{font-size:18px;margin:36px 0 8px;border-bottom:1px solid var(--line);padding-bottom:6px}}
p.note{{color:var(--muted);margin:4px 0 14px}}.grid{{display:grid;grid-template-columns:repeat(auto-fill,minmax(230px,1fr));gap:10px}}
.sw{{background:var(--card);border:1px solid var(--line);border-radius:8px;overflow:hidden}}.chip{{height:54px;border-bottom:1px solid var(--line)}}
.sw div.t{{padding:8px 10px;font-size:12px}}.sw b{{display:block;font-size:13px}}.sw code,code{{color:var(--muted)}}
table{{width:100%;border-collapse:collapse;font-size:13px}}td,th{{text-align:left;padding:6px 8px;border-bottom:1px solid var(--line);vertical-align:top}}
th{{color:var(--muted);font-weight:600}}.dot{{display:inline-block;width:14px;height:14px;border-radius:3px;border:1px solid #fff3;vertical-align:-2px;margin-right:6px}}
.s-inpal{{color:#98c379}}.s-merge{{color:#61afef}}.s-close{{color:#E5C07B}}.s-role{{color:#c678dd}}.s-one{{color:#E06C75}}small{{color:var(--muted)}}
.sum{{display:flex;flex-wrap:wrap;gap:10px;margin:10px 0}}.sum div{{background:var(--card);border:1px solid var(--line);border-radius:8px;padding:8px 14px}}.sum b{{font-size:20px;display:block}}
</style></head><body><main>
<h1>TrayTrigger style guide</h1>
<p class="note">Generated from the real files by <code>tools/Build-StyleGuide.py</code>; re-run it after any change. Contrast is against the window <code>{bg}</code> and card <code>{card}</code>.
Dev-only code in <code>Diagnostics/</code> is not listed ({diag_count} color uses there).</p>
<div class="sum">
<div><b>{len(palette)}</b>palette brushes</div>
<div><b>{len(uses)}</b>hard-coded colors in XAML ({len(xaml_outside)} not in the palette)</div>
<div><b>{len(set(c[0] for c in code_uses))}</b>colors in code ({len(code_uses)} uses)</div>
<div><b>{len(sizes)}</b>text sizes</div>
<div><b>{len(radius_tokens)}</b>corner radii</div>
</div>
<h2>Palette ({len(palette)} brushes)</h2><p class="note">What a theme would define. Contrast shown for text-on-background use.</p><div class="grid">""")
for name, ref, h in palette:
    w(f'<div class="sw"><div class="chip" style="background:{h}"></div><div class="t"><b>{e(name)}</b><code>{h}</code>'
      f'{" · " + e(ref) if ref else ""}<br><small>vs window {contrast(h, bg):.1f}:1 · vs card {contrast(h, card):.1f}:1</small></div></div>')
w('</div>')

w(f"""<h2>Hard-coded colors in XAML ({len(uses)})</h2>
<p class="note">Every literal hex value in the views and styles, most used first. Status against the palette:
<span class="s-inpal">in palette</span> (exact match: use the brush) ·
<span class="s-merge">merges silently</span> (within 3 RGB units of a palette color, not visible) ·
<span class="s-close">close</span> (within 8, slightly visible) ·
<span class="s-role">needs a role</span> (used more than once, no near palette color) ·
<span class="s-one">one-off</span> (used once, no near palette color). Counts: {', '.join(f'{k} {v}' for k, v in st.most_common())}.</p>
<table><tr><th>Color</th><th>Uses</th><th>Status</th><th>Nearest / palette name</th><th>Used by (property · file:line)</th></tr>""")
cls = {'in palette': 's-inpal', 'merges silently': 's-merge', 'close': 's-close', 'needs a role': 's-role', 'one-off': 's-one'}
for h in sorted(uses, key=lambda k: (-len(uses[k]), k)):
    s, near = status(h)
    props = collections.Counter(u.prop for u in uses[h])
    where = '; '.join(f'{p} ×{n}' for p, n in props.most_common(3))
    first = ', '.join(f'{os.path.basename(u.file)}:{u.line}' for u in uses[h][:3])
    w(f'<tr><td><span class="dot" style="background:{h}"></span><code>{h}</code></td><td>{len(uses[h])}</td>'
      f'<td class="{cls[s]}">{s}</td><td>{e(near)}</td><td><small>{e(where)} · {e(first)}</small></td></tr>')
w('</table>')

w(f"""<h2>Colors in code ({len(code_uses)} uses)</h2>
<p class="note">Colors built in C# (<code>Color.FromRgb</code>, <code>Brushes.X</code>, hex strings) in the app's own code. What is left here is artwork (the tray icon tile, the poster overlay, the fallback game icon) and has to stay the same in any theme. Not listed: the dev diagnostics, the build-time icon generator, the helper that reads the palette, and <code>Transparent</code>.</p>
<table><tr><th>Color</th><th>Status</th><th>File:line</th><th>Code</th></tr>""")
for h, f, ln, text in sorted(code_uses, key=lambda c: (c[0], c[1], c[2])):
    if h.startswith('#'):
        s, near = status(h)
        chip = f'<span class="dot" style="background:{h}"></span><code>{h}</code>'
        st_cell = f'<td class="{cls[s]}">{s}{" · " + e(near) if near else ""}</td>'
    else:
        chip, st_cell = f'<code>{h}</code>', '<td>named color</td>'
    w(f'<tr><td>{chip}</td>{st_cell}<td><small>{e(os.path.basename(f))}:{ln}</small></td><td><small>{e(text[:110])}</small></td></tr>')
w('</table>')

w(f"""<h2>Named colors in XAML</h2><p class="note">Colors written as a word, such as <code>White</code>. <code>Transparent</code> ({transparent} uses) is the same in every theme and is not listed.</p>
<table><tr><th>Name</th><th>Property</th><th>Where</th></tr>""")
for n, f, ln, p in named:
    w(f'<tr><td>{e(n)}</td><td>{e(p)}</td><td><small>{e(os.path.basename(f))}:{ln}</small></td></tr>')
if not named: w('<tr><td colspan="3">None.</td></tr>')
w('</table>')

w(f"""<h2>Text sizes ({len(sizes)} named)</h2>
<p class="note">The sizes in <code>Resources/Typography.xaml</code>, with how often each is used. Views reference these names; a number written straight into a view is caught by the TypographyGuardTests.
{("<b>Still written as a number: " + ", ".join(f"{v:g} ({c})" for v, c in sorted(size_literals.items())) + ".</b>") if size_literals else "No view writes a size as a number."}</p>
<table><tr><th>Name</th><th>Size</th><th>Uses</th><th>Sample</th><th>Mostly in</th></tr>""")
for name, v in sizes:
    w(f'<tr><td><code>{name}</code></td><td>{v:g}</td><td>{size_uses[name]}</td><td style="font-size:{v:g}px">The quick brown fox jumps over the lazy dog</td>'
      f'<td><small>{e(", ".join(f"{k} ×{c}" for k, c in size_where[name].most_common(4)))}</small></td></tr>')
w('</table>')

w(f"""<h2>Corner radii ({len(radius_tokens)} named)</h2>
<p class="note">The radii in <code>Resources/Shapes.xaml</code>, with how often each is used. Views reference these names with DynamicResource, so a theme can change them; a number written straight into a view, or a StaticResource, is caught by the ShapeGuardTests. The Theme column says whether a theme file's <code>shape</code> block can set the radius (and its largest value); the poster and the inner edges follow the theme's card, less 1 pixel, unless the theme sets the poster.
{("<b>Still written as a number: " + ", ".join(f"{v} ({c})" for v, c in sorted(radius_literals.items())) + ".</b>") if radius_literals else "No view writes a radius as a number."}</p>
<table><tr><th>Name</th><th>Radius</th><th>Theme</th><th>Uses</th><th>Shape</th><th>Mostly in</th></tr>""")
theme_radius = {'RadiusSmall': 'up to 16', 'RadiusControl': 'up to 48', 'RadiusCard': 'up to 64', 'RadiusPoster': 'up to 96 (else card less 1)',
                'RadiusInnerTop': 'follows the card', 'RadiusInnerBottom': 'follows the card'}
for name, v in radius_tokens:
    rr = ' '.join(x + 'px' for x in v.split(','))
    w(f'<tr><td><code>{name}</code></td><td>{e(v)}</td><td><small>{theme_radius.get(name, "no")}</small></td><td>{radius_uses[name]}</td><td><div style="width:64px;height:28px;background:var(--card);border:1px solid #fff4;border-radius:{rr}"></div></td>'
      f'<td><small>{e(", ".join(f"{k} ×{c}" for k, c in radius_where[name].most_common(4)))}</small></td></tr>')
w('</table></main></body></html>')

os.makedirs('docs', exist_ok=True)
open('docs/style-guide.html', 'w', encoding='utf-8').write('\n'.join(out))
print(f'palette {len(palette)} | xaml colors {len(uses)} ({len(xaml_outside)} outside) | code {len(code_uses)} uses | '
      f'named {len(named)} | sizes {len(sizes)} | radii {len(radius_tokens)} | status {dict(st)}')
