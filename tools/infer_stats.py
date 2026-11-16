#!/usr/bin/env python3
"""Infer tier-0 part stats and tier curves from data/stats/*.csv.

Writes data/stats/stats.csv, data/stats/validation.md and src/GarRevival/StatTable.g.cs.
Python 3 standard library only; the output depends only on the input files.

    python tools/infer_stats.py

Steps (validation.md has the numbers):
1. Normalise every observation to the 2017 balance (REF_DATE): apply each patches.csv row
   dated after the observation's era that names its part (or a class containing it) and stat.
   multiplier/additive/set change the value; direction-only patches mark it stale (lower weight).
2. Put guide durability on the garage scale: one factor fitted from parts the players also
   measured at tier 0.
3. Fit the tier curve per stat family from parts and classes seen at several tiers and from
   the garage's bar maxima (top tier of the strongest part); linear, exponential or power,
   whichever fits best. A family with fewer than MIN_PAIRS pairs shares the pooled curve.
4. Per part type and stat, fit log(value) = class + subtype factors (+ design trait) by ridge
   regression on the measured parts and predict the missing ones. A type missing a weight class
   borrows the class step of the most similar type, scaled by the ratio of their shared step.
5. Leave-one-out: hide each measured value, refit, predict. A family whose median error is worse
   than the designed rule (or above TRUST_ERROR where there is no designed rule) uses the designed
   values instead.
"""
import csv
import math
import os
import re
from collections import defaultdict

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DATA = os.path.join(ROOT, 'data', 'stats')
DESIGN_CS = os.path.join(ROOT, 'src', 'GarRevival', 'Design.cs')
OUT_CSV = os.path.join(DATA, 'stats.csv')
OUT_MD = os.path.join(DATA, 'validation.md')
OUT_CS = os.path.join(ROOT, 'src', 'GarRevival', 'StatTable.g.cs')

REF_DATE = '2017-08-09'  # the guide's tables: the last balance
MAX_TIER = 10            # client: PartTemplate.maxTier = 10, tech tree has 10 tier slots
MAX_DEVICE_TIER = 3      # tech tree: three device slots; 2017 guide: devices top out at level 3
TRUST_ERROR = 0.20       # median leave-one-out relative error above this is not trusted
MIN_PAIRS = 3            # tier pairs a family needs for its own curve
CLASSES = ('Light', 'Medium', 'Heavy')
RELIABILITY_WEIGHT = {'official': 1.0, 'guide': 1.0, 'player': 0.4}
STALE_WEIGHT = 0.25      # a direction-only patch came after the observation
APPROX_WEIGHT = 0.5      # notes say approximate, or a class-level number

# Half the garage's display step: an error inside it is no error.
TOLERANCE = {'durability': 0.5, 'speed': 0.05, 'weightLimit': 0.05, 'weight': 0.05,
             'damage': 0.005, 'splash': 0.005, 'rateOfFire': 0.05}
# Precision inferred values are rounded to (matches the measured values of the stat).
PRECISION = {'durability': 0, 'speed': 0, 'weightLimit': 1, 'weight': 1,
             'damage': 2, 'splash': 2, 'rateOfFire': 1}

# Sources that name a module only in words (blank partId in observations.csv).
TARGET_SELECTORS = {
    "player's weapons (Blaster modules)": 'class:WeaponModule/Medium',  # Blaster is medium; one barrel assumed
}

# The designed rule the mod used before this table: a named sibling's row, else a plain part
# of the class; barrels scale it by both barrels' Design.Barrels traits.
DESIGNED_SIBLINGS = {
    'HC_ChristmasSleigh': 'HC_Warhawk', 'MC_Maximus_Pride': 'MC_Bugsy',
    'LB_Cuckoo': 'LB_Jackhammer', 'MB_Maximus_Lagacy': 'MB_Chromax',
    'HW_B_Barry_Light': 'LW_B_Bertha', 'HW_B_ChristmasRocketLauncher': 'LW_B_Bertha',
    'MW_B_Maximus_Justice': 'MW_B_Laser', 'MW_B_Maximus_Revenge': 'MW_B_Laser',
    'MW_B_WitchBow': 'MW_B_Katyusha',
}
PLAIN = {
    'Chassis': {'Light': 'LC_Buzzer', 'Medium': 'MC_Bugsy', 'Heavy': 'HC_Daggerjaw'},
    'Body': {'Light': 'LB_Jackhammer', 'Medium': 'MB_Chromax', 'Heavy': 'HB_Chopper'},
    'Barrel': {'Light': 'LW_B_Flintlock', 'Medium': 'MW_B_Laser', 'Heavy': 'HW_B_Fragunov'},
}

# Stats a patch on X also changes.
PATCH_STATS = {
    'damage': {'damage', 'dps', 'dpsQuad', 'burstDamage'},
    'rateOfFire': {'rateOfFire', 'dps', 'dpsQuad', 'burstDamage'},
    'durability': {'durability'},
    'weight': {'weight'},
    'accuracy': {'accuracy'},
    'speedModifier': {'speedModifier'},
}
# Stats that do not change with tier, so an observation with no tier still counts as tier 0.
TIERLESS = {'speed', 'weightLimit', 'weight', 'rateOfFire', 'projectilesPerShot',
            'weightLimitModifier', 'speedModifier', 'terrainRating'}

# (type, stat): subtype keys used as factors, and the Design.Barrels trait used as a prior.
MODELS = {
    ('Chassis', 'durability'): (['kind'], None),
    ('Chassis', 'speed'): (['kind'], None),
    ('Chassis', 'weightLimit'): (['kind'], None),
    ('Body', 'durability'): (['arms'], None),
    ('Body', 'weight'): (['arms'], None),
    ('Barrel', 'damage'): (['role', 'feed', 'projectiles'], 'damage'),
    ('Barrel', 'rateOfFire'): (['role', 'feed'], 'rate'),
    ('Barrel', 'splash'): (['role'], 'splash'),
}
UNITS = {'durability': 'hp', 'speed': 'km/h', 'weightLimit': 'weight units', 'weight': 'weight units',
         'terrain': 'rating +1/0/-1', 'damage': 'hp per projectile', 'splash': 'hp per hit (Oxidian per tick)',
         'rateOfFire': 'shots/s', 'projectiles': 'count', 'damageModifier': 'x',
         'weightLimitModifier': 'weight units', 'speedModifier': 'km/h'}
LAMBDAS = (0.03, 0.1, 0.3, 1.0, 3.0)


# ---------------------------------------------------------------- loading

def read_csv(name):
    with open(os.path.join(DATA, name), encoding='utf-8', newline='') as f:
        return list(csv.DictReader(f))


def num(text):
    text = (text or '').strip()
    if not text:
        return None
    try:
        return float(text)
    except ValueError:
        return None


def era_date(era):
    m = re.match(r'(\d{4}-\d{2}-\d{2})', era or '')
    if m:
        return m.group(1)
    return REF_DATE if 'client' in (era or '') else (era or '')[:4] + '-12-31'


def load_parts():
    parts = {}
    for row in read_csv('parts.csv'):
        sub = {}
        for item in (row['subtype'] or '').split(';'):
            if '=' in item:
                k, v = item.split('=', 1)
                sub[k.strip()] = v.strip()
        parts[row['id']] = {'id': row['id'], 'name': row['name'], 'type': row['type'],
                            'weight': row['weightClass'], 'sub': sub}
    return parts


def load_design_traits():
    text = open(DESIGN_CS, encoding='utf-8').read()
    traits = {}
    for pid, args in re.findall(r'\{\s*"([^"]+)",\s*B\(([^)]*)\)\s*\}', text):
        vals = [float(a.strip().rstrip('f')) for a in args.split(',')]
        vals += [0.0] * (6 - len(vals))
        traits[pid] = {'damage': vals[0], 'rate': vals[1], 'accuracy': vals[2], 'range': vals[3],
                       'splash': vals[4], 'splashRange': vals[5]}
    return traits


# ---------------------------------------------------------------- selectors and patches

def selector_of(pid, parts):
    """(type, filters) for a part id or a class selector such as class:Body/Heavy/4arm."""
    if pid.startswith('class:'):
        bits = pid[6:].split('/')
        filters = {}
        for b in bits[1:]:
            if b in CLASSES:
                filters['weight'] = b
            elif b.endswith('arm') and b[:-3].isdigit():
                filters['arms'] = b[:-3]
            elif b == 'energy':
                filters['feed'] = 'energy'
            else:
                filters['tag'] = b
        return bits[0], filters
    p = parts.get(pid)
    if p is None:
        return None, {}
    filters = {'id': pid, 'weight': p['weight']}
    filters.update(p['sub'])
    return p['type'], filters


def patch_covers(patch_sel, obs_sel):
    ptype, pf = patch_sel
    otype, of = obs_sel
    if ptype is None or otype is None:
        return False
    if ptype not in ('Part', otype):
        return False
    for k, v in pf.items():
        if of.get(k) != v:
            return False
    return True


def normalise(obs, patches, parts):
    """Value moved to the 2017 balance, plus what was done to it."""
    date = era_date(obs['era'])
    sel = selector_of(obs['partId'], parts)
    value, value_max = num(obs['value']), num(obs['valueMax'])
    applied, stale, unquantified = [], [], []
    for p in sorted(patches, key=lambda r: r['date']):
        if not (date < p['date'] <= REF_DATE):
            continue
        if not patch_covers(selector_of(p['partId'], parts), sel):
            continue
        if p['kind'] == 'unspecified':
            unquantified.append(p['date'])
            continue
        if obs['stat'] not in PATCH_STATS.get(p['stat'], {p['stat']}):
            continue
        change = num(p['change'])
        if p['kind'] == 'multiplier' and change is not None:
            value = value * change if value is not None else None
            value_max = value_max * change if value_max is not None else None
            applied.append('%s x%g' % (p['date'], change))
        elif p['kind'] == 'additive' and change is not None:
            value = value + change if value is not None else None
            value_max = value_max + change if value_max is not None else None
            applied.append('%s %+g' % (p['date'], change))
        elif p['kind'] == 'set' and change is not None:
            value = value_max = change
            applied.append('%s =%g' % (p['date'], change))
        elif p['kind'] == 'direction':
            stale.append('%s %s' % (p['date'], p['change']))
    weight = RELIABILITY_WEIGHT.get(obs['reliability'], 0.4)
    if stale:
        weight *= STALE_WEIGHT
    if obs['partId'].startswith('class:') or 'approximate' in obs['notes']:
        weight *= APPROX_WEIGHT
    return {'id': obs['partId'], 'stat': obs['stat'], 'tier': num(obs['tier']), 'value': value,
            'valueMax': value_max, 'unit': obs['unit'], 'mechanism': obs['mechanism'], 'date': date,
            'url': obs['url'], 'reliability': obs['reliability'], 'notes': obs['notes'],
            'weight': weight, 'applied': applied, 'stale': stale, 'unquantified': sorted(set(unquantified))}


# ---------------------------------------------------------------- small numerics

def wmedian(values, weights):
    pairs = sorted(zip(values, weights))
    total = sum(weights)
    acc = 0.0
    for v, w in pairs:
        acc += w
        if acc >= total / 2:
            return v
    return pairs[-1][0]


def median(values):
    s = sorted(values)
    n = len(s)
    if n == 0:
        return float('nan')
    return s[n // 2] if n % 2 else (s[n // 2 - 1] + s[n // 2]) / 2


def golden(f, lo, hi, iters=200):
    g = (math.sqrt(5) - 1) / 2
    a, b = lo, hi
    c, d = b - g * (b - a), a + g * (b - a)
    for _ in range(iters):
        if f(c) < f(d):
            b = d
        else:
            a = c
        c, d = b - g * (b - a), a + g * (b - a)
    return (a + b) / 2


def solve(matrix, vector):
    n = len(vector)
    m = [row[:] + [vector[i]] for i, row in enumerate(matrix)]
    for col in range(n):
        piv = max(range(col, n), key=lambda r: abs(m[r][col]))
        m[col], m[piv] = m[piv], m[col]
        if abs(m[col][col]) < 1e-12:
            continue
        for r in range(n):
            if r != col and m[r][col] != 0.0:
                f = m[r][col] / m[col][col]
                for c in range(col, n + 1):
                    m[r][c] -= f * m[col][c]
    return [m[i][n] / m[i][i] if abs(m[i][i]) > 1e-12 else 0.0 for i in range(n)]


def ridge(rows, lam):
    """rows: (features dict, y, weight). Class intercepts are free; other factors shrink by lam."""
    names = sorted({k for f, _, _ in rows for k in f})
    idx = {k: i for i, k in enumerate(names)}
    n = len(names)
    a = [[0.0] * n for _ in range(n)]
    b = [0.0] * n
    for f, y, w in rows:
        items = [(idx[k], v) for k, v in f.items()]
        for i, vi in items:
            b[i] += w * vi * y
            for j, vj in items:
                a[i][j] += w * vi * vj
    for k, i in idx.items():
        a[i][i] += 1e-6 if k.startswith('class=') else lam
    beta = solve(a, b)
    return {k: beta[i] for k, i in idx.items()}


def rnd(value, digits):
    q = round(value, digits)
    return int(q) if digits == 0 else q


# ---------------------------------------------------------------- the model

class Inference:
    def __init__(self):
        self.parts = load_parts()
        self.traits = load_design_traits()
        self.patches = read_csv('patches.csv')
        self.obs = []
        self.skipped = defaultdict(list)
        for row in read_csv('observations.csv'):
            if not row['partId'] and row['target'] in TARGET_SELECTORS:
                row = dict(row, partId=TARGET_SELECTORS[row['target']],
                           notes=row['notes'] + '; mapped by infer_stats.py')
            if not row['partId']:
                self.skipped['no part or class named'].append(row['target'])
                continue
            o = normalise(row, self.patches, self.parts)
            if o['value'] is None:
                self.skipped['no number'].append(row['partId'] + ' ' + row['stat'])
                continue
            self.obs.append(o)
        self.notes = []

    # ---- observations by part

    def pick(self, pid, stat, tier=0):
        """The best observation of a part's stat at a tier: guide/official before player, fresh before stale."""
        cands = [o for o in self.obs if o['id'] == pid and o['stat'] == stat
                 and (o['tier'] == tier or (o['tier'] is None and stat in TIERLESS))]
        if not cands:
            return None
        rank = {'official': 0, 'guide': 0, 'player': 1}
        cands.sort(key=lambda o: (rank.get(o['reliability'], 2), bool(o['stale']), o['tier'] is None,
                                  -o['weight'], o['date']), reverse=False)
        return cands[0]

    def of_type(self, ptype):
        return sorted(p for p, v in self.parts.items() if v['type'] == ptype)

    # ---- step 2: guide durability scale

    def fit_scale(self):
        pairs = []
        for o in self.obs:
            if o['stat'] != 'durability' or o['reliability'] == 'guide' or o['tier'] != 0:
                continue
            if o['id'].startswith('class:'):
                continue
            g = self.pick(o['id'], 'durability', 0)
            if g is None or g['reliability'] != 'guide':
                continue
            pairs.append((o['id'], o['value'], g['value'], o['weight'], o['applied']))
        logs = [math.log(v / g) for _, v, g, _, _ in pairs]
        ws = [w for _, _, _, w, _ in pairs]
        fitted = math.exp(sum(l * w for l, w in zip(logs, ws)) / sum(ws))
        snapped = fitted
        for q in (1, 2, 3, 4):
            p = round(fitted * q)
            if p and abs(p / q - fitted) / fitted <= 0.005:
                snapped = p / q
                break
        self.scale = {'fitted': fitted, 'value': snapped, 'pairs': pairs}
        # Class-level numbers as a check only.
        checks = []
        for o in self.obs:
            if o['stat'] == 'durability' and o['tier'] == 0 and o['id'].startswith('class:') and o['id'].count('/') >= 2:
                members = self.members(o['id'])
                vals = [self.pick(m, 'durability', 0) for m in members]
                vals = [v['value'] for v in vals if v is not None and v['reliability'] == 'guide']
                if vals:
                    mean = math.exp(sum(math.log(v) for v in vals) / len(vals))
                    checks.append((o['id'], o['value'], mean, o['value'] / mean))
        self.scale['checks'] = checks

    def members(self, selector):
        sel = selector_of(selector, self.parts)
        return [p for p in sorted(self.parts) if patch_covers(sel, selector_of(p, self.parts))]

    def garage(self, o):
        """An observation's value in garage units."""
        v = o['value']
        if o['stat'] == 'durability' and o['reliability'] == 'guide':
            v *= self.scale['value']
        if o['stat'] == 'splash' and '2x per-tick' in o['unit']:
            v /= 2.0  # the garage shows Oxidian splash as 2x the per-tick value
        return v

    # ---- step 3: tier curves

    def tier_pairs(self):
        pairs = {'durability': [], 'damage': []}
        for o in self.obs:
            if o['stat'] != 'durability' or not o['tier']:
                continue
            if o['id'].startswith('class:'):
                base = [b for b in self.obs if b['id'] == o['id'] and b['stat'] == 'durability'
                        and b['tier'] == 0 and b['url'] == o['url']]
                if not base:
                    continue
                b = base[0]
                ratio = o['value'] / b['value']
                label = '%s T%d %g / T0 %g' % (o['id'], o['tier'], o['value'], b['value'])
                w = min(o['weight'], b['weight'])
            else:
                b = self.pick(o['id'], 'durability', 0)
                if b is None:
                    continue
                ratio = o['value'] / self.garage(b)
                label = '%s T%d %g%s / T0 %g' % (self.parts[o['id']]['name'], o['tier'], o['value'],
                                                 ' (%s)' % ', '.join(o['applied']) if o['applied'] else '',
                                                 self.garage(b))
                w = o['weight']
            pairs['durability'].append({'t': int(o['tier']), 'r': ratio, 'w': w, 'label': label})
        # Bar maxima: the strongest measured part at the top tier.
        bars = {('Chassis', 'durabilityBarMax'): ('durability', 'durability'),
                ('Body', 'durabilityBarMax'): ('durability', 'durability'),
                ('Barrel', 'damageBarMax'): ('damage', 'damage'),
                ('Barrel', 'splashBarMax'): ('splash', 'damage')}
        for o in self.obs:
            ptype = o['id'][6:] if o['id'].startswith('class:') else None
            key = (ptype, o['stat'])
            if key not in bars:
                continue
            stat, family = bars[key]
            best = None
            for pid in self.of_type(ptype):
                m = self.pick(pid, stat, 0)
                if m is not None and m['value'] > 0:
                    v = self.garage(m)
                    if best is None or v > best[1]:
                        best = (pid, v)
            pairs[family].append({'t': MAX_TIER, 'r': o['value'] / best[1], 'w': 1.0,
                                  'label': '%s %s %g / %s T0 %g' % (ptype, o['stat'], o['value'],
                                                                    self.parts[best[0]]['name'], best[1])})
        return pairs

    @staticmethod
    def curve(form, k, t):
        if form == 'linear':
            return 1 + k * t
        if form == 'exponential':
            return math.exp(k * t)
        return (1 + t) ** k

    def fit_curve(self, pairs):
        best = None
        for form, hi in (('linear', 0.5), ('exponential', 0.3), ('power', 1.0)):
            def sse(k, form=form):
                return sum(p['w'] * (math.log(p['r']) - math.log(self.curve(form, k, p['t']))) ** 2 for p in pairs)
            k = golden(sse, 0.0, hi)
            if best is None or sse(k) < best[2] - 1e-12:
                best = (form, k, sse(k))
        return best[0], best[1]

    def robust_curve(self, pairs):
        form, k = self.fit_curve(pairs)
        res = [math.log(p['r']) - math.log(self.curve(form, k, p['t'])) for p in pairs]
        sigma = max(0.02, 1.4826 * wmedian([abs(r) for r in res], [p['w'] for p in pairs]))
        kept = [p for p, r in zip(pairs, res) if abs(r) <= 3 * sigma]
        dropped = [p for p, r in zip(pairs, res) if abs(r) > 3 * sigma]
        if dropped and len(kept) >= 2:
            form, k = self.fit_curve(kept)
        else:
            dropped = []
        return form, k, dropped

    def fit_curves(self):
        pairs = self.tier_pairs()
        pooled = [dict(p, family=f) for f, ps in pairs.items() for p in ps]
        self.curves = {}
        for family, ps in pairs.items():
            own = len(ps) >= MIN_PAIRS
            src = ps if own else pooled
            form, k, dropped = self.robust_curve(src)
            snapped, label = k, None
            if form == 'linear':
                n = round(1 / k)
                if n > 0 and abs(1 / n - k) / k <= 0.02:
                    snapped, label = 1.0 / n, '1/%d' % n
            loo = []
            for i in range(len(src)):
                rest = src[:i] + src[i + 1:]
                f2, k2, _ = self.robust_curve(rest)
                pred = self.curve(f2, k2, src[i]['t'])
                loo.append(abs(pred - src[i]['r']) / src[i]['r'])
            self.curves[family] = {'form': form, 'fitted': k, 'k': snapped, 'label': label, 'own': own,
                                   'pairs': src, 'dropped': dropped, 'loo': loo}

    def grow(self, family, t):
        c = self.curves[family]
        return self.curve(c['form'], c['k'], t)

    # ---- step 4: class and subtype factors

    def features(self, pid, keys, trait):
        p = self.parts[pid]
        f = {'class=' + p['weight']: 1.0}
        for k in keys:
            v = p['sub'].get(k)
            if k == 'projectiles':
                f['log projectiles'] = math.log(float(v or 1))
            elif v:
                f['%s=%s' % (k, v)] = 1.0
        if trait:
            t = self.traits.get(pid, {}).get(trait, 0.0)
            if t > 0:
                f['log design %s' % trait] = math.log(t)
        return f

    def measured(self, ptype, stat):
        """Measured tier-0 values of a type's stat in garage units: {pid: (value, obs)}."""
        out = {}
        for pid in self.of_type(ptype):
            o = self.pick(pid, stat, 0)
            if o is not None:
                out[pid] = (self.garage(o), o)
        return out

    def fit_rows(self, ptype, stat, meas, exclude=None):
        keys, trait = MODELS[(ptype, stat)]
        rows = []
        for pid, (v, o) in sorted(meas.items()):
            if pid == exclude or v <= 0:
                continue
            rows.append((self.features(pid, keys, trait), math.log(v), o['weight']))
        return rows

    def predict(self, beta, pid, ptype, stat):
        keys, trait = MODELS[(ptype, stat)]
        f = self.features(pid, keys, trait)
        return math.exp(sum(beta.get(k, 0.0) * v for k, v in f.items())), f

    def err(self, stat, pred, obs):
        return max(0.0, abs(pred - obs) - TOLERANCE.get(stat, 0.0)) / obs

    def designed(self, pid, stat, meas, exclude=None):
        """The designed rule: sibling or plain part of the class, barrels scaled by traits."""
        p = self.parts[pid]
        sib = DESIGNED_SIBLINGS.get(pid) or PLAIN.get(p['type'], {}).get(p['weight'])
        if sib is None or sib == exclude or sib == pid or sib not in meas:
            return None, None
        v = meas[sib][0]
        if p['type'] == 'Barrel':
            key = {'damage': 'damage', 'rateOfFire': 'rate', 'splash': 'splash'}.get(stat)
            if key:
                mine, theirs = self.traits[pid][key], self.traits[sib][key]
                v = v * mine / theirs if theirs > 0 else 0.0
        return v, sib

    def fit_models(self):
        self.models = {}
        for (ptype, stat) in MODELS:
            meas = self.measured(ptype, stat)
            if stat == 'splash':
                meas_fit = {k: v for k, v in meas.items() if v[0] > 0}
            else:
                meas_fit = meas
            best = None
            for lam in LAMBDAS:
                errs = []
                for pid, (v, o) in sorted(meas_fit.items()):
                    if v <= 0:
                        continue
                    beta = ridge(self.fit_rows(ptype, stat, meas_fit, exclude=pid), lam)
                    pred, _ = self.predict(beta, pid, ptype, stat)
                    errs.append((pid, pred, v, self.err(stat, pred, v)))
                score = median([e[3] for e in errs])
                if best is None or score < best[1] - 1e-9:
                    best = (lam, score, errs)
            lam, score, errs = best
            beta = ridge(self.fit_rows(ptype, stat, meas_fit), lam)
            derrs = []
            for pid, (v, o) in sorted(meas_fit.items()):
                dv, sib = self.designed(pid, stat, meas, exclude=pid)
                if dv is not None and v > 0:
                    derrs.append((pid, dv, v, self.err(stat, dv, v)))
            dscore = median([e[3] for e in derrs]) if derrs else float('nan')
            # Beat the designed rule where there is one; otherwise stay within TRUST_ERROR.
            trusted = score <= dscore if derrs else score <= TRUST_ERROR
            self.models[(ptype, stat)] = {'lam': lam, 'beta': beta, 'loo': errs, 'dloo': derrs,
                                          'score': score, 'dscore': dscore, 'trusted': trusted,
                                          'meas': meas, 'n': len(meas_fit)}

    # ---- weapon modules: case + barrel durability per class

    def class_steps(self, ptype):
        beta = self.models[(ptype, 'durability')]['beta']
        c = {k: beta.get('class=' + k, 0.0) for k in CLASSES}
        return {'LM': c['Medium'] - c['Light'], 'MH': c['Heavy'] - c['Medium']}

    def fit_modules(self):
        known = {}
        for o in self.obs:
            if o['id'].startswith('class:WeaponModule/') and o['stat'] == 'durability' and o['tier'] is not None:
                cls = o['id'].split('/')[1]
                known[cls] = (o['value'] / self.grow('durability', int(o['tier'])), o)
        donors = {t: self.class_steps(t) for t in ('Chassis', 'Body')}
        bar = {o['id'][6:]: o['value'] for o in self.obs if o['stat'] == 'durabilityBarMax'}
        share = bar['WeaponCase'] / (bar['WeaponCase'] + bar['Barrel'])

        def infer(known_cls):
            """Module durability per class from the known classes, borrowing missing steps."""
            vals = {k: v for k, (v, _) in known_cls.items()}
            own = math.log(vals['Heavy'] / vals['Medium']) if 'Heavy' in vals and 'Medium' in vals else None
            if own is not None:
                donor = min(donors, key=lambda t: (abs(donors[t]['MH'] - own), t))
                mult = own / donors[donor]['MH']
            else:
                donor = 'Chassis'
                mult = 1.0
            steps = {'LM': donors[donor]['LM'] * mult, 'MH': donors[donor]['MH'] * mult if own is None else own}
            out = dict(vals)
            for _ in range(2):
                if 'Medium' in out and 'Heavy' not in out:
                    out['Heavy'] = out['Medium'] * math.exp(steps['MH'])
                if 'Heavy' in out and 'Medium' not in out:
                    out['Medium'] = out['Heavy'] / math.exp(steps['MH'])
                if 'Medium' in out and 'Light' not in out:
                    out['Light'] = out['Medium'] / math.exp(steps['LM'])
                if 'Light' in out and 'Medium' not in out:
                    out['Medium'] = out['Light'] * math.exp(steps['LM'])
            return out, donor, mult, steps

        values, donor, mult, steps = infer(known)
        loo, dloo = [], []
        top = self.grow('durability', MAX_TIER)
        for cls, (v, o) in sorted(known.items()):
            rest = {k: x for k, x in known.items() if k != cls}
            pred, _, _, _ = infer(rest)
            if cls not in pred:
                continue  # the only known class: nothing left to predict it from
            tol = 0.05 if 'approximate' in o['notes'] or o['id'].startswith('class:') else 0.0
            loo.append((cls, pred[cls], v, max(0.0, abs(pred[cls] - v) / v - tol)))
            designed = (bar['WeaponCase'] + bar['Barrel']) * {'Light': 0.6, 'Medium': 0.8, 'Heavy': 1.0}[cls] / top
            dloo.append((cls, designed, v, max(0.0, abs(designed - v) / v - tol)))
        score = median([e[3] for e in loo])
        dscore = median([e[3] for e in dloo])
        self.modules = {'values': values, 'known': known, 'donor': donor, 'mult': mult, 'steps': steps,
                        'share': share, 'loo': loo, 'dloo': dloo, 'score': score, 'dscore': dscore,
                        'trusted': score <= TRUST_ERROR and score <= dscore, 'bar': bar}

    # ---- weapon case damage modifier from DPS on two mechanisms

    def fit_mechanisms(self):
        dps = defaultdict(dict)
        for o in self.obs:
            if o['stat'] == 'dps' and o['mechanism'] and o['reliability'] == 'player':
                dps[o['id']][o['mechanism']] = o
        cannon, ballistic = [], []
        for pid, by in sorted(dps.items()):
            if 'HW_C_Cannon' in by and 'HW_C_Ballistic' in by:
                cannon.append((pid, by['HW_C_Cannon']['value'] / by['HW_C_Ballistic']['value']))
            if 'HW_C_Ballistic' in by:
                g_dmg, g_rof = self.pick(pid, 'damage', 0), self.pick(pid, 'rateOfFire', 0)
                if g_dmg and g_rof and g_dmg['reliability'] == 'guide':
                    o = by['HW_C_Ballistic']
                    ballistic.append((pid, o['value'] / (g_dmg['value'] * g_rof['value']), o['weight'], o['applied'], o['stale']))
        self.mech = {}
        if cannon:
            # The garage shows damage modifiers rounded to 0.01 (RoundTo).
            v = round(sum(r for _, r in cannon) / len(cannon), 2)
            self.mech['HW_C_Cannon'] = (v, 'player DPS on Cannon / on Ballistic: ' + ', '.join(
                '%s %.4f' % (self.parts[p]['name'], r) for p, r in cannon))
        if ballistic:
            v = round(wmedian([r for _, r, _, _, _ in ballistic], [w for _, _, w, _, _ in ballistic]), 2)
            self.mech['HW_C_Ballistic'] = (v, 'player DPS on Ballistic / guide DMG x ROF: ' + ', '.join(
                '%s %.3f%s' % (self.parts[p]['name'], r, ' (stale)' if s else '') for p, r, _, _, s in ballistic))
        hiccs = [o for o in self.obs if o['id'] == 'LW_C_Hiccs' and o['stat'] == 'rateOfFireMultiplier']
        bar = [o for o in self.obs if o['stat'] == 'rateOfFireModifierBarMax']
        if hiccs and bar and hiccs[0]['value'] > bar[0]['value']:
            self.notes.append('Hiccs rate x%g (2016 guide) is above the official rate-modifier bar maximum '
                              'x%g; the official bound wins and Hiccs keeps its designed value.'
                              % (hiccs[0]['value'], bar[0]['value']))

    # ---- step 5: the table

    def build(self):
        self.fit_scale()
        self.fit_curves()
        self.fit_models()
        self.fit_modules()
        self.fit_mechanisms()
        rows = []  # (pid, stat, value, provenance, basis)

        def basis_of(o, extra=''):
            bits = ['%s %s %s' % (o['reliability'], o['date'], o['url'])]
            if o['stat'] == 'durability' and o['reliability'] == 'guide' and self.scale['value'] != 1:
                bits.append('guide x%g garage scale' % self.scale['value'])
            if o['applied']:
                bits.append('patched ' + ', '.join(o['applied']))
            if o['stale']:
                bits.append('later change without figure: ' + ', '.join(o['stale']))
            if extra:
                bits.append(extra)
            return '; '.join(bits)

        for (ptype, stat), m in sorted(self.models.items()):
            meas = m['meas']
            for pid in self.of_type(ptype):
                if pid in meas:
                    v, o = meas[pid]
                    prov = 'adjusted-by-patch' if o['applied'] else 'measured'
                    extra = 'shown 2x per tick in the garage' if stat == 'splash' and '2x per-tick' in o['unit'] else ''
                    rows.append((pid, stat, round(v, 4), prov, basis_of(o, extra)))
                    continue
                if stat == 'splash' and self.traits.get(pid, {}).get('splash', 0) <= 0:
                    rows.append((pid, stat, 0.0, 'designed', 'no splash in its description (Design.cs)'))
                    continue
                if m['trusted']:
                    pred, f = self.predict(m['beta'], pid, ptype, stat)
                    rows.append((pid, stat, rnd(pred, PRECISION[stat]), 'inferred', self.explain(ptype, stat, pid, f, m)))
                else:
                    dv, sib = self.designed(pid, stat, meas)
                    how = 'sibling %s' % sib + (' scaled by Design.Barrels traits' if ptype == 'Barrel' else '')
                    rows.append((pid, stat, rnd(dv, PRECISION[stat]), 'designed',
                                 '%s; inference not trusted (LOO median %.0f%%)' % (how, 100 * m['score'])))
        # Guide barrels without a splash row have none.
        have = {(r[0], r[1]) for r in rows}
        for pid in self.of_type('Barrel'):
            if (pid, 'splash') not in have:
                o = self.pick(pid, 'damage', 0)
                rows.append((pid, 'splash', 0.0, 'measured', 'guide lists no splash; ' + o['url']))
        # Terrain: the most common rating among chassis of the same kind.
        by_kind = defaultdict(list)
        for pid in self.of_type('Chassis'):
            o = self.pick(pid, 'terrainRating', 0)
            if o is not None:
                by_kind[self.parts[pid]['sub'].get('kind')].append((int(o['value']), pid))
        self.terrain_loo = []
        for pid in self.of_type('Chassis'):
            o = self.pick(pid, 'terrainRating', 0)
            kind = self.parts[pid]['sub'].get('kind')
            others = [v for v, q in by_kind[kind] if q != pid]
            mode = max(sorted(set(others)), key=others.count) if others else 0
            if o is not None:
                self.terrain_loo.append((pid, mode, int(o['value'])))
                rows.append((pid, 'terrain', int(o['value']), 'measured', basis_of(o)))
            else:
                rows.append((pid, 'terrain', mode, 'inferred', 'most common rating of %s chassis (%d of %d)'
                             % (kind, others.count(mode), len(others))))
        # Projectiles per shot.
        for pid in self.of_type('Barrel'):
            o = self.pick(pid, 'projectilesPerShot', 0)
            if o is not None:
                rows.append((pid, 'projectiles', int(o['value']), 'measured', basis_of(o)))
            else:
                rows.append((pid, 'projectiles', int(self.parts[pid]['sub'].get('projectiles', '1')), 'designed',
                             'parts.csv subtype from the description'))
        # Weapon case and barrel durability from module durability.
        md = self.modules
        if md['trusted']:
            for ptype, share in (('WeaponCase', md['share']), ('Barrel', 1 - md['share'])):
                for pid in self.of_type(ptype):
                    cls = self.parts[pid]['weight']
                    v = md['values'][cls] * share
                    src = md['known'].get(cls)
                    how = ('module T%d %g / curve' % (int(src[1]['tier']), src[1]['value'])) if src else \
                        'borrowed %s class step x%.2f' % (md['donor'], md['mult'])
                    rows.append((pid, 'durability', rnd(v, 0), 'inferred',
                                 '%s weapon module %.0f (%s), split %.3f by bar maxima case %g : barrel %g'
                                 % (cls, md['values'][cls], how, share, md['bar']['WeaponCase'], md['bar']['Barrel'])))
        for pid, (v, basis) in sorted(self.mech.items()):
            rows.append((pid, 'damageModifier', v, 'measured', basis))
        # Engines: measured modifiers only; the rest stay designed.
        for pid in self.of_type('Engine'):
            for stat in ('weightLimitModifier', 'speedModifier'):
                o = self.pick(pid, stat, 0)
                if o is not None:
                    rows.append((pid, stat, o['value'], 'adjusted-by-patch' if o['applied'] else 'measured', basis_of(o)))
        self.rows = sorted(rows, key=lambda r: (self.parts[r[0]]['type'], r[0], r[1]))

    def explain(self, ptype, stat, pid, f, m):
        beta = m['beta']
        cls = self.parts[pid]['weight']
        same = [q for q in m['meas'] if self.parts[q]['weight'] == cls]
        bits = ['class %s base %.4g from %d parts' % (cls, math.exp(beta.get('class=' + cls, 0.0)), len(same))]
        for k in sorted(f):
            if k.startswith('class='):
                continue
            factor = math.exp(beta.get(k, 0.0) * f[k])
            if '=' in k:
                key, val = k.split('=', 1)
                sharing = [self.parts[q]['name'] for q in sorted(m['meas']) if self.parts[q]['sub'].get(key) == val]
                bits.append('%s x%.3f (%s)' % (k, factor, ', '.join(sharing[:5]) or 'no measured part'))
            else:
                bits.append('%s x%.3f' % (k, factor))
        return 'log-linear, ridge %g: ' % m['lam'] + '; '.join(bits)


# ---------------------------------------------------------------- writers

def fmt(v):
    if isinstance(v, int):
        return str(v)
    s = ('%.4f' % v).rstrip('0').rstrip('.')
    return s if s not in ('-0', '') else '0'


def write_csv(inf):
    with open(OUT_CSV, 'w', encoding='utf-8', newline='') as f:
        w = csv.writer(f, lineterminator='\n')
        w.writerow(['partId', 'stat', 'tier0', 'unit', 'provenance', 'basis'])
        for pid, stat, v, prov, basis in inf.rows:
            w.writerow([pid, stat, fmt(v), UNITS.get(stat, ''), prov, basis])


def pct(x):
    return '%.1f%%' % (100 * x) if x == x else '-'


def write_md(inf):
    L = ['# Stat inference: validation', '',
         'Generated by `tools/infer_stats.py`; do not edit. Rerun with `python tools/infer_stats.py`.', '']
    s = inf.scale
    L += ['## Guide durability scale', '',
          'Garage tier-0 durability / guide durability, from parts players also measured at tier 0 '
          '(patched to 2017): fitted x%.4f, used x%g.' % (s['fitted'], s['value']), '']
    L += ['| Part | Player T0 (2017 balance) | Guide | Ratio |', '|---|---|---|---|']
    for pid, v, g, w, applied in s['pairs']:
        L.append('| %s | %g (%s) | %g | %.4f |' % (inf.parts[pid]['name'], v, ', '.join(applied) or 'as seen', g, v / g))
    for sel, v, mean, r in s['checks']:
        L.append('| %s (check) | %g | class mean %.1f | %.3f |' % (sel, v, mean, r))
    L += ['', '## Tier curves', '',
          'value(t) = value(0) x curve(t), tiers 0-%d (client `PartTemplate.maxTier` = 10; devices 0-%d).'
          % (MAX_TIER, MAX_DEVICE_TIER), '']
    L += ['| Family | Form | Fitted | Used | Pairs | Own data | LOO median | LOO max |', '|---|---|---|---|---|---|---|---|']
    for fam, c in sorted(inf.curves.items()):
        L.append('| %s | %s | %.5f | %s | %d | %s | %s | %s |' % (
            fam, c['form'], c['fitted'], c['label'] or '%.5f' % c['k'], len(c['pairs']),
            'yes' if c['own'] else 'no, pooled', pct(median(c['loo'])), pct(max(c['loo']))))
    L += ['', 'Pairs (ratio = tier t / tier 0, on the 2017 balance):', '']
    seen = set()
    for fam, c in sorted(inf.curves.items()):
        for p in c['pairs']:
            if p['label'] in seen:
                continue
            seen.add(p['label'])
            dropped = ' **outlier, not used**' if p in c['dropped'] else ''
            L.append('- T%d x%.4f (curve x%.4f), weight %.2g: %s%s' % (
                p['t'], p['r'], inf.grow(fam, p['t']), p['w'], p['label'], dropped))
    L += ['', '## Stat families (leave-one-out)', '',
          'Each measured tier-0 value is hidden, the model refitted and the value predicted. Error is relative, '
          'after the garage display rounding. "Designed" is the earlier rule (sibling or plain part of the class, '
          'barrels scaled by `Design.Barrels` traits). A family is trusted when its median error is not worse '
          'than the designed rule, or at most %d%% where there is none; otherwise missing values use the designed rule.'
          % round(100 * TRUST_ERROR), '']
    L += ['| Family | Measured | Ridge | LOO median | LOO max (part) | Designed median | Trusted |', '|---|---|---|---|---|---|---|']
    for (ptype, stat), m in sorted(inf.models.items()):
        worst = max(m['loo'], key=lambda e: e[3])
        L.append('| %s %s | %d | %g | %s | %s (%s) | %s | %s |' % (
            ptype, stat, m['n'], m['lam'], pct(m['score']), pct(worst[3]), inf.parts[worst[0]]['name'],
            pct(m['dscore']), 'yes' if m['trusted'] else '**no**'))
    md = inf.modules
    worst = max(md['loo'], key=lambda e: e[3]) if md['loo'] else None
    L.append('| WeaponModule durability | %d | - | %s | %s | %s | %s |' % (
        len(md['known']), pct(md['score']), '%s (%s)' % (pct(worst[3]), worst[0]) if worst else 'n/a (one class known)',
        pct(md['dscore']), 'yes' if md['trusted'] else '**no**'))
    hits = sum(1 for _, p, o in inf.terrain_loo if p == o)
    L.append('| Chassis terrain (by kind) | %d | - | %d/%d right | - | - | yes |' % (len(inf.terrain_loo), hits, len(inf.terrain_loo)))
    L += ['', '## Fitted factors', '']
    for (ptype, stat), m in sorted(inf.models.items()):
        items = ', '.join('%s x%.3f' % (k, math.exp(v)) if not k.startswith('log ') else '%s ^%.3f' % (k, v)
                          for k, v in sorted(m['beta'].items()))
        L.append('- **%s %s**: %s' % (ptype, stat, items))
    L += ['- **Weapon module durability** (tier 0): ' + ', '.join('%s %.1f' % (k, md['values'][k]) for k in CLASSES)
          + '. Light borrows the %s Light->Medium step x%.3f (module Medium->Heavy step / %s step = %.3f). '
            'Case share %.3f, barrel share %.3f (bar maxima %g : %g).'
          % (md['donor'], md['mult'], md['donor'], md['mult'], md['share'], 1 - md['share'],
             md['bar']['WeaponCase'], md['bar']['Barrel'])]
    L += ['', '## Bar maxima check', '', 'Strongest table value at the top tier against the garage bar maximum:', '']
    for ptype, stat, bar_stat, fam in (('Chassis', 'durability', 'durabilityBarMax', 'durability'),
                                       ('Body', 'durability', 'durabilityBarMax', 'durability'),
                                       ('WeaponCase', 'durability', 'durabilityBarMax', 'durability'),
                                       ('Barrel', 'durability', 'durabilityBarMax', 'durability'),
                                       ('Barrel', 'damage', 'damageBarMax', 'damage'),
                                       ('Barrel', 'splash', 'splashBarMax', 'damage'),
                                       ('Chassis', 'speed', 'speedBarMax', None),
                                       ('Chassis', 'weightLimit', 'weightLimitBarMax', None),
                                       ('Body', 'weight', 'weightBarMax', None)):
        vals = [(r[2], r[0]) for r in inf.rows if r[1] == stat and inf.parts[r[0]]['type'] == ptype]
        bar = [o['value'] for o in inf.obs if o['id'] == 'class:' + ptype and o['stat'] == bar_stat]
        if not vals or not bar:
            continue
        v, pid = max(vals)
        top = v * (inf.grow(fam, MAX_TIER) if fam else 1.0)
        L.append('- %s %s: %s %.2f vs bar %g (%s)' % (ptype, stat, inf.parts[pid]['name'], top, bar[0],
                                                     'equal' if abs(top - bar[0]) <= 0.01 * bar[0] else
                                                     'under' if top < bar[0] else 'over'))
    counts = defaultdict(lambda: defaultdict(int))
    for pid, stat, v, prov, basis in inf.rows:
        counts[inf.parts[pid]['type']][prov] += 1
    L += ['', '## Values by provenance', '', '| Type | measured | adjusted-by-patch | inferred | designed |', '|---|---|---|---|---|']
    for t in sorted(counts):
        c = counts[t]
        L.append('| %s | %d | %d | %d | %d |' % (t, c['measured'], c['adjusted-by-patch'], c['inferred'], c['designed']))
    L += ['', '## Not used', '']
    for reason, items in sorted(inf.skipped.items()):
        L.append('- %s: %d rows (%s)' % (reason, len(items), '; '.join(sorted(set(items)))))
    unknown = sorted({'%s %s' % (o['id'], o['stat']) for o in inf.obs if o['tier'] is None and o['stat'] not in TIERLESS})
    L.append('- tier not given for a stat that grows with tier: %d rows (%s)' % (len(unknown), '; '.join(unknown)))
    for n in inf.notes:
        L.append('- ' + n)
    L.append('')
    with open(OUT_MD, 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(L))


def cs_float(v):
    s = ('%.4f' % v).rstrip('0').rstrip('.')
    return (s if s not in ('', '-0') else '0') + 'f'


def curve_expr(c):
    k = cs_float(c['k'])
    if c['form'] == 'linear':
        return '1f + %s * tier' % k
    if c['form'] == 'exponential':
        return '(float)Math.Exp(%s * tier)' % k
    return '(float)Math.Pow(1 + tier, %s)' % k


def write_cs(inf):
    code = {'measured': 'm', 'adjusted-by-patch': 'p', 'inferred': 'i', 'designed': 'd'}
    val = {(r[0], r[1]): r for r in inf.rows}

    def cell(pid, stat):
        return val[(pid, stat)][2]

    def prov(pid, stats):
        return ' '.join(code[val[(pid, s)][3]] for s in stats)

    def table(name, row_type, helper, ptype, stats, cols, pad):
        out = ['    public static readonly Dictionary<string, %s> %s = new()' % (row_type, name), '    {',
               '        // %s' % ', '.join(cols)]
        for pid in inf.of_type(ptype):
            args = ', '.join(str(cell(pid, s)) if s in ('terrain', 'projectiles') else cs_float(cell(pid, s)) for s in stats)
            key = '"%s",' % pid
            out.append('        { %s %s(%s) }, // %s' % (key.ljust(pad), helper, args, prov(pid, stats)))
        out.append('    };')
        return out

    def sparse(name, ptype, stat, comment):
        items = [(pid, val[(pid, stat)]) for pid in inf.of_type(ptype) if (pid, stat) in val]
        out = ['    // %s' % comment, '    public static readonly Dictionary<string, float> %s = new()' % name, '    {']
        for pid, r in items:
            out.append('        { "%s", %s }, // %s' % (pid, cs_float(r[2]), code[r[3]]))
        out.append('    };')
        return out

    dur, dmg = inf.curves['durability'], inf.curves['damage']
    origin = {}
    for pid, stat, v, p, basis in inf.rows:
        origin.setdefault(pid, set()).add(p)
    L = ['// <auto-generated>',
         '// Generated by tools/infer_stats.py from data/stats/*.csv. Do not edit; rerun the script.',
         '// </auto-generated>',
         'using System;',
         'using System.Collections.Generic;',
         '',
         'namespace GarRevival;',
         '',
         '// Tier-0 stats on the 2017 balance in garage units, and the tier curves (data/stats/validation.md).',
         '// Per value: m measured, p measured then patched, i inferred, d designed rule.',
         'internal static class StatTable',
         '{',
         '    // Client: PartTemplate.maxTier = 10; the tech tree has three device tiers.',
         '    public const int MaxTier = %d;' % MAX_TIER,
         '    public const int MaxDeviceTier = %d;' % MAX_DEVICE_TIER,
         '',
         '    // Durability: %s fit %.5f%s over %d pairs.' % (dur['form'], dur['fitted'],
                                                            ' = ' + dur['label'] if dur['label'] else '', len(dur['pairs'])),
         '    public static float DurabilityCurve(int tier) => %s;' % curve_expr(dur),
         '',
         '    // Damage and splash: %s%s.' % ('own fit' if dmg['own'] else 'too few pairs, pooled with durability',
                                            ' = ' + dmg['label'] if dmg['label'] else ''),
         '    public static float DamageCurve(int tier) => %s;' % curve_expr(dmg),
         '',
         '    public sealed class ChassisRow',
         '    {',
         '        public float Durability, SpeedKmh, WeightLimit;',
         '        public int Terrain; // +1 good, 0 average, -1 poor',
         '    }',
         '',
         '    public sealed class BodyRow',
         '    {',
         '        public float Durability, Weight;',
         '    }',
         '',
         '    // Damage and Splash per projectile; Oxidian splash per tick.',
         '    public sealed class BarrelRow',
         '    {',
         '        public float Damage, Splash, ShotsPerSecond;',
         '        public int Projectiles;',
         '    }',
         '',
         '    private static ChassisRow C(float durability, float speedKmh, float weightLimit, int terrain) =>',
         '        new() { Durability = durability, SpeedKmh = speedKmh, WeightLimit = weightLimit, Terrain = terrain };',
         '',
         '    private static BodyRow B(float durability, float weight) => new() { Durability = durability, Weight = weight };',
         '',
         '    private static BarrelRow W(float damage, float splash, float shotsPerSecond, int projectiles) =>',
         '        new() { Damage = damage, Splash = splash, ShotsPerSecond = shotsPerSecond, Projectiles = projectiles };',
         '']
    L += table('Chassis', 'ChassisRow', 'C', 'Chassis', ['durability', 'speed', 'weightLimit', 'terrain'],
               ['durability', 'km/h', 'weight limit', 'terrain'], 23)
    L.append('')
    L += table('Bodies', 'BodyRow', 'B', 'Body', ['durability', 'weight'], ['durability', 'weight'], 26)
    L.append('')
    L += table('Barrels', 'BarrelRow', 'W', 'Barrel', ['damage', 'splash', 'rateOfFire', 'projectiles'],
               ['damage', 'splash', 'shots/s', 'projectiles'], 31)
    L.append('')
    dur_items = ['    // Weapon case and barrel durability (weapon module per class, split by the bar maxima).',
                 '    public static readonly Dictionary<string, float> Durability = new()', '    {']
    for ptype in ('WeaponCase', 'Barrel'):
        for pid in inf.of_type(ptype):
            if (pid, 'durability') in val:
                r = val[(pid, 'durability')]
                dur_items.append('        { "%s", %s }, // %s' % (pid, cs_float(r[2]), code[r[3]]))
    dur_items.append('    };')
    L += dur_items
    L.append('')
    L += sparse('DamageModifier', 'WeaponCase', 'damageModifier', 'Weapon case damage modifiers with a measurement.')
    L.append('')
    L += sparse('EngineWeightLimit', 'Engine', 'weightLimitModifier', 'Engine weight limit modifiers with a measurement.')
    L.append('')
    L += sparse('EngineSpeedKmh', 'Engine', 'speedModifier', 'Engine speed modifiers (km/h) with a measurement.')
    L.append('')
    L += ['    // Where a part\'s table values come from, for generated-templates.txt.',
          '    public static readonly Dictionary<string, string> Origin = new()', '    {']
    order = ['measured', 'adjusted-by-patch', 'inferred', 'designed']
    for pid in sorted(origin):
        L.append('        { "%s", "%s" },' % (pid, '+'.join(p for p in order if p in origin[pid])))
    L += ['    };', '}', '']
    with open(OUT_CS, 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(L))


def main():
    inf = Inference()
    inf.build()
    write_csv(inf)
    write_md(inf)
    write_cs(inf)
    for fam, c in sorted(inf.curves.items()):
        print('curve %-10s %s %.5f -> %s (LOO median %s)' % (fam, c['form'], c['fitted'], c['label'] or c['k'], pct(median(c['loo']))))
    for (ptype, stat), m in sorted(inf.models.items()):
        print('%-8s %-11s n=%2d lambda=%-4g LOO median %6s designed %6s %s' % (
            ptype, stat, m['n'], m['lam'], pct(m['score']), pct(m['dscore']), 'trusted' if m['trusted'] else 'DESIGNED'))
    md = inf.modules
    print('modules  %s LOO %s designed %s %s' % (', '.join('%s %.0f' % (k, md['values'][k]) for k in CLASSES),
                                                pct(md['score']), pct(md['dscore']), 'trusted' if md['trusted'] else 'DESIGNED'))
    print('wrote %s, %s, %s (%d values)' % (os.path.relpath(OUT_CSV, ROOT), os.path.relpath(OUT_MD, ROOT),
                                          os.path.relpath(OUT_CS, ROOT), len(inf.rows)))


if __name__ == '__main__':
    main()
