"""GAR Revival settings editor: edits the mod's .cfg files and per-part stat overrides.

Double-click to run (needs Python 3 from python.org; uses only its standard library).
Changes apply the next time the game starts (server.cfg: the next time you host).
"""
import csv
import os
import re
import subprocess
import sys
import tkinter as tk
from tkinter import messagebox, ttk

ROOT = os.path.dirname(os.path.abspath(__file__))
MAPS = ['random', 'Western', 'WesternNight', 'Laundry', 'Pirates', 'Medieval', 'DragonTemple', 'Pompei', 'Dungeon']
MODES = ['TeamDeadmach', 'CaptureTheFlag', 'DestoryBase']


def key(name, kind, default, help_text, choices=None, lo=None, hi=None):
    """kind: bool (true/false), onoff, choice, int, float, text."""
    return dict(name=name, kind=kind, default=default, help=help_text, choices=choices, lo=lo, hi=hi)


RULES = [
    key('name', 'text', 'My server', 'Server name shown to players.'),
    key('profiles', 'choice', 'own', 'own: players bring their own profile and unlocks. server: everyone gets a separate profile for this server.', ['own', 'server']),
    key('startingCredits', 'float', '20000', 'Credits a new server profile starts with.', lo=0),
    key('startingSparks', 'float', '0', 'Sparks a new server profile starts with.', lo=0),
    key('creditRewards', 'float', '1', 'Multiplier for credits battles pay.', lo=0, hi=100),
    key('experienceRewards', 'float', '1', 'Multiplier for experience battles pay.', lo=0, hi=100),
    key('ammo', 'onoff', 'on', 'on: ammo used in battles is paid for. off: every battle starts fully loaded.'),
    key('guildBuffs', 'onoff', 'on', "Players' guild benefits count in battles here."),
    key('maxPlayers', 'int', '10', 'Players let in at once (a dedicated server does not count itself).', lo=1, hi=64),
]
FILES = [
    ('General', 'settings.cfg', [
        key('unlimitedCredits', 'bool', 'false', 'Refill credits and sparks to a huge amount on every launch. Parts still have to be bought.'),
        key('skipTutorial', 'bool', 'true', 'Mark the original new-player tutorial as completed.'),
        key('ammo', 'onoff', 'on', 'on: ammo used in battles is paid for. off: every battle starts fully loaded. In a hosted game the host decides.'),
        key('master', 'text', '', 'Master servers (server browser, FIND MATCH, ladder), comma-separated http:// URLs. Empty: the master on this PC. off: none.'),
        key('masterAutoStart', 'onoff', 'on', 'Start the bundled master on this PC when master= points here.'),
        key('skinTextureSize', 'choice', '1024', 'Size of generated skin textures (smaller uses less memory).', ['128', '256', '512', '1024', '2048']),
        key('skinCompress', 'bool', 'true', 'Compress generated skin textures.'),
        key('skinCacheParts', 'int', '48', 'How many skinned parts stay cached.', lo=4, hi=512),
    ]),
    ('Hosting', 'server.cfg', RULES + [
        key('public', 'choice', '0', '1 lists your hosted game on the master servers (shows your public IP address to everyone).', ['0', '1']),
    ]),
    ('Dedicated server', 'dedicated.cfg', RULES + [
        key('host', 'choice', 'tcp', 'tcp: LAN and the internet (forward the port). steam: a Steam lobby.', ['tcp', 'steam']),
        key('port', 'int', '27015', 'TCP port.', lo=1, hi=65535),
        key('minPlayers', 'int', '1', 'Players needed before a match is announced.', lo=1, hi=9),
        key('matchDelay', 'float', '30', 'Seconds from the announcement to the start.', lo=5, hi=3600),
        key('master', 'text', '', 'Master servers to list this server on, comma-separated. Empty: the master on this PC.'),
    ]),
]
MATCH = [
    key('map', 'choice', 'random', 'Arena.', MAPS),
    key('mode', 'choice', 'TeamDeadmach', 'Game mode.', MODES),
    key('enemies', 'int', '5', 'Enemy bots (at most 5).', lo=0, hi=5),
    key('allies', 'int', '4', 'Allied bots (at most 4).', lo=0, hi=4),
    key('minutes', 'int', '10', 'Match length in minutes.', lo=1, hi=60),
    key('humans', 'choice', 'together', 'In a session: together = all players against bots, split = players on both teams.', ['together', 'split']),
    key('weakEnemies', 'bool', 'true', 'Make enemy bots weaker.'),
]
FILES += [
    ('Match', 'battle.cfg', MATCH),
    ('Dedicated match', 'dedicated-battle.cfg', MATCH),
    ('Master server', os.path.join('master', 'master.cfg'), [
        key('port', 'int', '27090', 'TCP port to listen on.', lo=1, hi=65535),
        key('publicAddress', 'text', '', 'Address given to players for servers registering from this PC or LAN.'),
        key('serverTimeout', 'int', '60', 'Seconds without a heartbeat before a server leaves the list.', lo=15, hi=3600),
        key('maxServers', 'int', '1000', 'Most servers listed at once.', lo=1, hi=100000),
        key('maxServersPerIp', 'int', '16', 'Most servers from one IP address.', lo=1, hi=1000),
        key('requestsPerMinute', 'int', '600', 'Requests per minute from one IP address.', lo=10, hi=100000),
        key('registersPerMinute', 'int', '60', 'Server registrations per minute from one IP.', lo=1, hi=10000),
        key('resultsPerMinute', 'int', '60', 'Match results per minute from one IP.', lo=1, hi=10000),
        key('maxConnections', 'int', '64', 'Requests handled at the same time.', lo=1, hi=1000),
        key('ladderFile', 'text', 'master-ladder.txt', 'Where the ladder is saved.'),
    ]),
]
PART_STATS = ['durability', 'weight', 'speed', 'weightLimit', 'damage', 'splash', 'rateOfFire', 'capacity', 'regen', 'price']
STAT_HELP = {
    'durability': 'hit points at tier 0 (higher tiers grow from it)', 'weight': 'weight units',
    'speed': 'km/h (chassis) or km/h bonus (engine)', 'weightLimit': 'chassis limit or engine bonus',
    'damage': 'per projectile, tier 0', 'splash': 'splash damage, tier 0', 'rateOfFire': 'shots per second (max 5)',
    'capacity': 'battery energy, tier 0', 'regen': 'battery energy per second, tier 0', 'price': 'credits at tier 0',
}


# ---- cfg files: only the lines of changed keys are rewritten, comments stay

def read_cfg(path):
    values = {}
    if os.path.exists(path):
        for raw in open(path, encoding='utf-8-sig', errors='replace'):
            line = raw.split('#', 1)[0].strip() if not raw.lstrip().startswith('#') else ''
            if '=' in line:
                k, v = line.split('=', 1)
                values[k.strip().lower()] = v.strip()
    return values


def write_cfg(path, changes, schema):
    lines = open(path, encoding='utf-8-sig', errors='replace').read().splitlines() if os.path.exists(path) else []
    done = set()
    for i, raw in enumerate(lines):
        m = re.match(r'\s*([^#=\s]+)\s*=(.*)$', raw)
        if m and m.group(1).lower() in changes:
            comment = re.search(r'\s+#.*$', m.group(2))
            lines[i] = '%s=%s%s' % (m.group(1), changes[m.group(1).lower()], comment.group(0) if comment else '')
            done.add(m.group(1).lower())
    for k in schema:
        if k['name'].lower() in changes and k['name'].lower() not in done:
            lines += ['# ' + k['help'], '%s=%s' % (k['name'], changes[k['name'].lower()])]
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(lines) + '\n')


def check(k, value):
    """None when valid, else the problem."""
    if k['kind'] in ('int', 'float'):
        try:
            n = int(value) if k['kind'] == 'int' else float(value)
        except ValueError:
            return 'must be a %s' % ('whole number' if k['kind'] == 'int' else 'number')
        if (k['lo'] is not None and n < k['lo']) or (k['hi'] is not None and n > k['hi']):
            return 'must be between %s and %s' % (k['lo'], k['hi'] if k['hi'] is not None else 'any')
    if k['kind'] == 'text' and ('\n' in value or '#' in value):
        return 'cannot contain # or line breaks'
    return None


class CfgTab(ttk.Frame):
    def __init__(self, master, title, rel_path, schema):
        super().__init__(master, padding=12)
        self.path, self.schema, self.title = os.path.join(ROOT, rel_path), schema, title
        self.vars = {}
        ttk.Label(self, text=rel_path, style='Path.TLabel').grid(row=0, column=0, columnspan=3, sticky='w', pady=(0, 8))
        for row, k in enumerate(schema, start=1):
            ttk.Label(self, text=k['name']).grid(row=row, column=0, sticky='nw', padx=(0, 12), pady=3)
            var = tk.StringVar()
            if k['kind'] in ('bool', 'onoff'):
                on, off = ('true', 'false') if k['kind'] == 'bool' else ('on', 'off')
                widget = ttk.Checkbutton(self, variable=var, onvalue=on, offvalue=off)
            elif k['kind'] == 'choice':
                widget = ttk.Combobox(self, textvariable=var, values=k['choices'], state='readonly', width=22)
            else:
                widget = ttk.Entry(self, textvariable=var, width=40 if k['kind'] == 'text' else 14)
            widget.grid(row=row, column=1, sticky='w', pady=3)
            ttk.Label(self, text=k['help'], style='Help.TLabel', wraplength=520).grid(row=row, column=2, sticky='w', padx=(12, 0))
            self.vars[k['name'].lower()] = (k, var)
        self.columnconfigure(2, weight=1)
        self.reload()

    def reload(self):
        values = read_cfg(self.path)
        for name, (k, var) in self.vars.items():
            var.set(values.get(name, k['default']))
        self.saved = {name: var.get() for name, (k, var) in self.vars.items()}

    def save(self):
        changes = {}
        for name, (k, var) in self.vars.items():
            value = var.get().strip()
            problem = check(k, value)
            if problem:
                messagebox.showerror('GAR Revival settings', '%s / %s %s.' % (self.title, k['name'], problem))
                return False
            if value != self.saved[name]:
                changes[name] = value
        if changes:
            write_cfg(self.path, changes, self.schema)
        self.reload()
        return True


class CodesTab(ttk.Frame):
    """codes.cfg: CODE = credits N, sparks N."""

    def __init__(self, master):
        super().__init__(master, padding=12)
        self.path = os.path.join(ROOT, 'codes.cfg')
        ttk.Label(self, text='codes.cfg: redeem codes (Menu > Redeem Code), each usable once per profile', style='Path.TLabel').pack(anchor='w')
        self.tree = ttk.Treeview(self, columns=('code', 'credits', 'sparks'), show='headings', height=12)
        for c, w in (('code', 220), ('credits', 120), ('sparks', 120)):
            self.tree.heading(c, text=c.title())
            self.tree.column(c, width=w)
        self.tree.pack(fill='both', expand=True, pady=8)
        self.tree.bind('<<TreeviewSelect>>', self.pick)
        form = ttk.Frame(self)
        form.pack(fill='x')
        self.code, self.credits, self.sparks = tk.StringVar(), tk.StringVar(), tk.StringVar()
        for label, var, w in (('Code', self.code, 20), ('Credits', self.credits, 10), ('Sparks', self.sparks, 10)):
            ttk.Label(form, text=label).pack(side='left', padx=(0, 4))
            ttk.Entry(form, textvariable=var, width=w).pack(side='left', padx=(0, 12))
        ttk.Button(form, text='Add / update', command=self.put).pack(side='left')
        ttk.Button(form, text='Remove', command=self.remove).pack(side='left', padx=6)
        self.reload()

    def reload(self):
        self.tree.delete(*self.tree.get_children())
        self.header = ['# GAR Revival redeem codes (Menu > Redeem Code). One per line:', '#   CODE = credits 2000, sparks 10',
                       '# Codes are not case-sensitive and work once per profile.']
        if os.path.exists(self.path):
            self.header = []
            for raw in open(self.path, encoding='utf-8-sig', errors='replace').read().splitlines():
                if raw.lstrip().startswith('#') or '=' not in raw:
                    self.header.append(raw)
                    continue
                code, rewards = raw.split('=', 1)
                got = dict(re.findall(r'(credits|sparks)\s+([0-9.]+)', rewards, re.I))
                got = {k.lower(): v for k, v in got.items()}
                self.tree.insert('', 'end', values=(code.strip().upper(), got.get('credits', ''), got.get('sparks', '')))

    def pick(self, _):
        sel = self.tree.selection()
        if sel:
            code, credits, sparks = self.tree.item(sel[0], 'values')
            self.code.set(code), self.credits.set(credits), self.sparks.set(sparks)

    def put(self):
        code = self.code.get().strip().upper()
        if not re.fullmatch(r'[A-Z0-9_-]{1,40}', code):
            messagebox.showerror('GAR Revival settings', 'A code is 1-40 letters, digits, - or _.')
            return
        for v in (self.credits.get(), self.sparks.get()):
            if v.strip() and not re.fullmatch(r'\d+(\.\d+)?', v.strip()):
                messagebox.showerror('GAR Revival settings', 'Credits and sparks must be numbers.')
                return
        for item in self.tree.get_children():
            if self.tree.item(item, 'values')[0] == code:
                self.tree.delete(item)
        self.tree.insert('', 'end', values=(code, self.credits.get().strip(), self.sparks.get().strip()))

    def remove(self):
        for item in self.tree.selection():
            self.tree.delete(item)

    def save(self):
        lines = list(self.header)
        for item in self.tree.get_children():
            code, credits, sparks = self.tree.item(item, 'values')
            rewards = ', '.join('%s %s' % (k, v) for k, v in (('credits', credits), ('sparks', sparks)) if str(v).strip())
            if rewards:
                lines.append('%s = %s' % (code, rewards))
        with open(self.path, 'w', encoding='utf-8', newline='\n') as f:
            f.write('\n'.join(lines) + '\n')
        return True


class PartsTab(ttk.Frame):
    """part-stats.csv (written by the game at startup) shown; edits go to part-overrides.csv."""

    def __init__(self, master):
        super().__init__(master, padding=12)
        self.stats_path = os.path.join(ROOT, 'part-stats.csv')
        self.over_path = os.path.join(ROOT, 'part-overrides.csv')
        ttk.Label(self, text='Part stats at tier 0. Start the game once to fill this list; edits are saved to part-overrides.csv '
                             'and apply at the next game start. Tiered stats keep growing with the tier.',
                  style='Path.TLabel', wraplength=900).pack(anchor='w')
        bar = ttk.Frame(self)
        bar.pack(fill='x', pady=6)
        self.search, self.kind = tk.StringVar(), tk.StringVar(value='All types')
        ttk.Label(bar, text='Search').pack(side='left')
        ttk.Entry(bar, textvariable=self.search, width=24).pack(side='left', padx=6)
        self.kinds = ttk.Combobox(bar, textvariable=self.kind, state='readonly', width=16)
        self.kinds.pack(side='left')
        self.only = tk.BooleanVar()
        ttk.Checkbutton(bar, text='Only changed parts', variable=self.only, command=self.fill).pack(side='left', padx=12)
        self.search.trace_add('write', lambda *_: self.fill())
        self.kind.trace_add('write', lambda *_: self.fill())
        cols = ['partId', 'type', 'class', 'origin'] + PART_STATS
        self.tree = ttk.Treeview(self, columns=cols, show='headings', height=14)
        for c in cols:
            self.tree.heading(c, text=c)
            self.tree.column(c, width=150 if c == 'partId' else 78, anchor='w' if c in ('partId', 'type', 'class', 'origin') else 'e')
        self.tree.tag_configure('changed', foreground='#b45309')
        self.tree.pack(fill='both', expand=True)
        self.tree.bind('<<TreeviewSelect>>', self.pick)
        self.form = ttk.LabelFrame(self, text='Override (blank = built-in value)', padding=8)
        self.form.pack(fill='x', pady=(8, 0))
        self.fields = {}
        for i, stat in enumerate(PART_STATS):
            ttk.Label(self.form, text=stat).grid(row=i // 5 * 2, column=i % 5, sticky='w', padx=6)
            var = tk.StringVar()
            entry = ttk.Entry(self.form, textvariable=var, width=14)
            entry.grid(row=i // 5 * 2 + 1, column=i % 5, sticky='w', padx=6, pady=(0, 6))
            self.fields[stat] = (var, entry)
        buttons = ttk.Frame(self.form)
        buttons.grid(row=4, column=0, columnspan=5, sticky='w', padx=6)
        ttk.Button(buttons, text='Apply to part', command=self.apply).pack(side='left')
        ttk.Button(buttons, text='Clear part', command=self.clear).pack(side='left', padx=6)
        self.selected = None
        self.reload()

    def reload(self):
        self.rows = list(csv.DictReader(open(self.stats_path, encoding='utf-8'))) if os.path.exists(self.stats_path) else []
        self.over = {}
        if os.path.exists(self.over_path):
            for raw in open(self.over_path, encoding='utf-8-sig'):
                cells = [c.strip() for c in raw.split(',')]
                if len(cells) >= 3 and not raw.startswith('#') and cells[0] != 'partId':
                    self.over.setdefault(cells[0], {})[cells[1]] = cells[2]
        self.kinds['values'] = ['All types'] + sorted({r['type'] for r in self.rows})
        self.fill()

    def fill(self):
        self.tree.delete(*self.tree.get_children())
        text, kind = self.search.get().lower(), self.kind.get()
        for r in self.rows:
            pid = r['partId']
            if (text and text not in pid.lower()) or (kind != 'All types' and r['type'] != kind) or (self.only.get() and pid not in self.over):
                continue
            o = self.over.get(pid, {})
            values = [pid, r['type'], r['class'], r['origin']] + ['%s*' % o[s] if s in o else r.get(s, '') for s in PART_STATS]
            self.tree.insert('', 'end', iid=pid, values=values, tags=('changed',) if o else ())
        if not self.rows:
            self.tree.insert('', 'end', values=['(start the game once: it writes part-stats.csv)'] + [''] * 13)

    def pick(self, _):
        sel = self.tree.selection()
        if not sel or sel[0] not in {r['partId'] for r in self.rows}:
            return
        self.selected = sel[0]
        self.form['text'] = 'Override %s (blank = built-in value)' % self.selected
        row = next(r for r in self.rows if r['partId'] == self.selected)
        for stat, (var, entry) in self.fields.items():
            var.set(self.over.get(self.selected, {}).get(stat, ''))
            entry.state(['!disabled'] if row.get(stat, '') != '' or stat in ('durability', 'weight', 'price') else ['disabled'])

    def apply(self):
        if not self.selected:
            return
        new = {}
        for stat, (var, _) in self.fields.items():
            v = var.get().strip()
            if not v:
                continue
            try:
                if float(v) < 0 or (stat == 'rateOfFire' and float(v) > 5):
                    raise ValueError
            except ValueError:
                messagebox.showerror('GAR Revival settings', '%s: %s must be a number from 0%s.' % (self.selected, stat, ' to 5' if stat == 'rateOfFire' else ''))
                return
            new[stat] = v
        if new:
            self.over[self.selected] = new
        else:
            self.over.pop(self.selected, None)
        self.fill()

    def clear(self):
        if self.selected:
            self.over.pop(self.selected, None)
            for var, _ in self.fields.values():
                var.set('')
            self.fill()

    def save(self):
        lines = ['# GAR Revival part overrides: partId,stat,value (tier-0 values). Edited by edit-settings.pyw.',
                 '# stats: ' + ', '.join('%s (%s)' % (s, STAT_HELP[s]) for s in PART_STATS), 'partId,stat,value']
        for pid in sorted(self.over):
            lines += ['%s,%s,%s' % (pid, s, v) for s, v in sorted(self.over[pid].items())]
        with open(self.over_path, 'w', encoding='utf-8', newline='\n') as f:
            f.write('\n'.join(lines) + '\n')
        return True


class App(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title('GAR Revival settings')
        self.geometry('1180x720')
        style = ttk.Style(self)
        style.configure('Help.TLabel', foreground='#555555')
        style.configure('Path.TLabel', foreground='#1f4e79')
        notebook = ttk.Notebook(self)
        notebook.pack(fill='both', expand=True, padx=8, pady=8)
        self.tabs = [CfgTab(notebook, title, path, schema) for title, path, schema in FILES]
        for tab, (title, _, _) in zip(self.tabs, FILES):
            notebook.add(tab, text=title)
        for tab, title in ((CodesTab(notebook), 'Redeem codes'), (PartsTab(notebook), 'Part stats')):
            notebook.add(tab, text=title)
            self.tabs.append(tab)
        bottom = ttk.Frame(self, padding=(8, 0, 8, 8))
        bottom.pack(fill='x')
        ttk.Button(bottom, text='Save all', command=self.save).pack(side='right')
        ttk.Button(bottom, text='Reload', command=self.reload).pack(side='right', padx=6)
        ttk.Button(bottom, text='Open folder', command=lambda: open_folder(ROOT)).pack(side='left')
        ttk.Label(bottom, text='Changes apply the next time the game starts (server.cfg: the next time you host).',
                  style='Help.TLabel').pack(side='left', padx=12)

    def save(self):
        for tab in self.tabs:
            if tab.save() is False:
                return
        messagebox.showinfo('GAR Revival settings', 'Saved. Restart the game to apply.')

    def reload(self):
        for tab in self.tabs:
            tab.reload()


def open_folder(path):
    if sys.platform == 'win32':
        os.startfile(path)
    else:
        subprocess.Popen(['open' if sys.platform == 'darwin' else 'xdg-open', path])


if __name__ == '__main__':
    App().mainloop()
