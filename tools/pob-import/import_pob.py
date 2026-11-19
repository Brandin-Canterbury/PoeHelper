"""
Imports skill and support gem data from Path of Building Community (MIT licence) into PoeHelper.

    python -I tools/pob-import/import_pob.py <PathOfBuilding checkout> src/PoeHelper.Core/Data/skills.json

Reads src/Data/Skills/*.lua, src/Data/SkillStatMap.lua and src/Data/Misc.lua, evaluates every gem's
stats per level the way PoB's CalcTools does (effectiveness interpolation included), and maps each stat
through the gem's own statMap or the global SkillStatMap into:
  - "skill" values: base damage (FireMin/FireMax, FireDot, ...) and skill data (RFLifeMultiplier, ...);
  - "mods": modifiers the damage model understands (MORE/INC/BASE damage, speed, crit, DoT multi).
Re-run it when a new league's PoB release lands.
"""
import json
import os
import re
import subprocess
import sys

# ---------------------------------------------------------------- a small Lua reader

TOKEN = re.compile(r"""
    (?P<ws>\s+)
  | (?P<lcomment>--\[(?P<leq>=*)\[.*?\](?P=leq)\])
  | (?P<comment>--[^\n]*)
  | (?P<lstr>\[(?P<seq>=*)\[.*?\](?P=seq)\])
  | (?P<str>"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*')
  | (?P<num>0x[0-9a-fA-F]+|\d+\.?\d*(?:[eE][-+]?\d+)?|\.\d+(?:[eE][-+]?\d+)?)
  | (?P<name>[A-Za-z_][A-Za-z0-9_]*)
  | (?P<op>\.\.\.|\.\.|==|~=|<=|>=|//|::|[-+*/%^#<>=(){}\[\];:,.])
""", re.S | re.X)


def tokenize(src):
    out = []
    pos = 0
    while pos < len(src):
        m = TOKEN.match(src, pos)
        if not m:
            raise SyntaxError(f"bad char at {pos}: {src[pos:pos + 30]!r}")
        pos = m.end()
        kind = m.lastgroup
        if kind in ("ws", "comment", "lcomment"):
            continue
        if kind in ("leq", "seq"):
            kind = "lcomment" if m.group("lcomment") else "lstr"
        text = m.group(0)
        if m.group("lstr"):
            out.append(("str", text[text.index("[", 1) + 1: text.rindex("]", 0, -1)]))
        elif m.group("str"):
            out.append(("str", bytes(text[1:-1], "utf-8").decode("unicode_escape", errors="ignore")))
        elif m.group("num"):
            out.append(("num", float(int(text, 16)) if text.startswith("0x") else float(text)))
        elif m.group("name"):
            out.append(("name", text))
        else:
            out.append(("op", text))
    out.append(("eof", None))
    return out


class Call:
    def __init__(self, fn, args):
        self.fn, self.args = fn, args

    def __repr__(self):
        return f"{self.fn}({self.args})"


class Name(str):
    """A dotted identifier, e.g. SkillType.Spell or ModFlag.Attack."""


class Func:
    """A Lua function body (skipped: PoB's hand-written skill code)."""


BLOCK_OPEN = {"function", "if", "do", "repeat"}


class Parser:
    def __init__(self, tokens):
        self.t, self.i = tokens, 0

    def peek(self, k=0):
        return self.t[self.i + k]

    def take(self, kind=None, val=None):
        tok = self.t[self.i]
        if (kind and tok[0] != kind) or (val is not None and tok[1] != val):
            raise SyntaxError(f"expected {kind} {val}, got {tok} near token {self.i}")
        self.i += 1
        return tok

    def is_op(self, v):
        return self.peek() == ("op", v)

    def skip_function(self):
        # 'function' already taken: skip parameters and body up to the matching 'end'.
        depth = 1
        while depth:
            kind, v = self.take()
            if kind == "name":
                if v in ("function", "if", "do"):
                    depth += 1
                elif v == "repeat":
                    depth += 1
                elif v in ("end", "until"):
                    depth -= 1
                elif v in ("while", "for"):
                    pass  # their 'do' opens the block
            if kind == "eof":
                raise SyntaxError("unterminated function")
        return Func()

    def value(self):
        v = self.unary()
        # Binary arithmetic: evaluate numbers, keep anything else as an opaque string.
        while self.peek()[0] == "op" and self.peek()[1] in ("+", "-", "*", "/", "^", "..", "%", "//"):
            op = self.take()[1]
            r = self.unary()
            if isinstance(v, float) and isinstance(r, float):
                v = {"+": v + r, "-": v - r, "*": v * r, "/": v / r if r else 0.0, "^": v ** r, "%": v % r if r else 0.0,
                     "//": v // r if r else 0.0}.get(op, v)
            else:
                v = f"{v}{op}{r}"
        return v

    def unary(self):
        if self.is_op("-"):
            self.take()
            v = self.unary()
            return -v if isinstance(v, float) else f"-{v}"
        if self.peek() == ("name", "not"):
            self.take()
            self.unary()
            return None
        return self.primary()

    def primary(self):
        kind, v = self.peek()
        if kind == "num":
            self.take()
            return v
        if kind == "str":
            self.take()
            return v
        if kind == "op" and v == "{":
            return self.table()
        if kind == "op" and v == "(":
            self.take()
            inner = self.value()
            self.take("op", ")")
            return inner
        if kind == "name":
            self.take()
            if v == "true":
                return True
            if v == "false":
                return False
            if v == "nil":
                return None
            if v == "function":
                return self.skip_function()
            name = v
            while True:
                if self.is_op(".") or self.is_op(":"):
                    self.take()
                    name += "." + self.take("name")[1]
                elif self.is_op("["):
                    self.take()
                    idx = self.value()
                    self.take("op", "]")
                    name += f"[{idx}]"
                elif self.is_op("("):
                    self.take()
                    args = []
                    while not self.is_op(")"):
                        args.append(self.value())
                        if self.is_op(","):
                            self.take()
                    self.take("op", ")")
                    return self.call_tail(Call(name, args))
                elif self.peek()[0] == "str" or self.is_op("{"):
                    return Call(name, [self.value()])
                else:
                    return Name(name)
        raise SyntaxError(f"unexpected {self.peek()} at {self.i}")

    def call_tail(self, c):
        while self.is_op("."):
            self.take()
            c = Call(f"{c}.{self.take('name')[1]}", [])
        return c

    def table(self):
        self.take("op", "{")
        arr, d = [], {}
        while not self.is_op("}"):
            if self.is_op("["):
                self.take()
                k = self.value()
                self.take("op", "]")
                self.take("op", "=")
                d[k if not isinstance(k, float) else int(k)] = self.value()
            elif self.peek()[0] == "name" and self.peek(1) == ("op", "="):
                k = self.take()[1]
                self.take()
                d[k] = self.value()
            else:
                arr.append(self.value())
            if self.is_op(",") or self.is_op(";"):
                self.take()
        self.take("op", "}")
        if d and arr:
            for n, a in enumerate(arr, 1):
                d.setdefault(n, a)
            return d
        return d if d else arr


def parse_assignments(src, target):
    """Every `target["Key"] = { ... }` in a file."""
    p = Parser(tokenize(src))
    out = {}
    while p.peek()[0] != "eof":
        if p.peek() == ("name", target) and p.peek(1) == ("op", "["):
            p.take()
            p.take("op", "[")
            key = p.value()
            p.take("op", "]")
            p.take("op", "=")
            out[key] = p.value()
        else:
            p.take()
    return out


def parse_return_table(src):
    """The table a file returns, possibly wrapped in `return function(...) return { ... } end`."""
    p = Parser(tokenize(src))
    while p.peek()[0] != "eof":
        if p.is_op("{"):
            return p.table()
        p.take()
    return {}


# ---------------------------------------------------------------- stat evaluation

KEEP_MODS = {
    "Damage", "PhysicalDamage", "FireDamage", "ColdDamage", "LightningDamage", "ChaosDamage", "ElementalDamage",
    "Speed", "CritChance", "CritMultiplier", "DotMultiplier", "FireDotMultiplier", "ColdDotMultiplier",
    "PhysicalDotMultiplier", "ChaosDotMultiplier", "Duration",
    "PhysicalMin", "PhysicalMax", "FireMin", "FireMax", "ColdMin", "ColdMax", "LightningMin", "LightningMax", "ChaosMin", "ChaosMax",
    "DamageEffectiveness",
}
# Conditions the helper can evaluate or treats as always true for the skill in use.
OK_CONDITIONS = {"GlobalEffect", "SkillType", "SkillName", "SkillPart"}


def flag_list(v):
    if v in (None, 0, 0.0):
        return []
    if isinstance(v, Name):
        return [v.split(".")[-1]]
    if isinstance(v, Call) and v.fn in ("bit.bor", "bor"):
        return [x for a in v.args for x in flag_list(a)]
    return [str(v)]


def condition_ok(c):
    if not isinstance(c, dict):
        return True
    t = c.get("type")
    if t in OK_CONDITIONS:
        return True
    if t == "StatThreshold" and c.get("stat") in ("LifeUnreserved",):
        return True
    return False


def clean_condition(c):
    out = {}
    for k, v in c.items():
        if isinstance(v, Name):
            v = v.split(".")[-1]
        if isinstance(v, (str, float, int, bool)):
            out[str(k)] = v
        elif isinstance(v, list):
            out[str(k)] = [x.split(".")[-1] if isinstance(x, Name) else x for x in v if isinstance(x, (str, float, int))]
    return out


def apply_stat(entry, value, skill_out, mods_out):
    """One stat's value through its stat-map entry."""
    if not isinstance(entry, (list, dict)):
        return
    items = entry if isinstance(entry, list) else [entry[k] for k in sorted(k for k in entry if isinstance(k, int))]
    opts = entry if isinstance(entry, dict) else {}
    if isinstance(value, bool):
        value = 1.0 if value else 0.0
    if not isinstance(value, (int, float)):
        return
    v = float(value)
    if isinstance(opts.get("mult"), float):
        v *= opts["mult"]
    if isinstance(opts.get("div"), float) and opts["div"]:
        v /= opts["div"]
    if isinstance(opts.get("base"), float):
        v += opts["base"]
    for it in items:
        if not isinstance(it, Call):
            continue
        a = it.args
        if it.fn == "skill" and a:
            conds = [c for c in a[2:] if isinstance(c, dict)]
            if all(condition_ok(c) for c in conds):
                val = a[1] if len(a) > 1 and isinstance(a[1], (float, bool)) else v
                skill_out[a[0]] = skill_out.get(a[0], 0) + (val if isinstance(val, float) else (1.0 if val else 0.0))
        elif it.fn == "mod" and len(a) >= 2 and a[0] in KEEP_MODS:
            conds = [c for c in a[5:] if isinstance(c, dict)]
            if not all(condition_ok(c) for c in conds):
                continue
            val = a[2] if len(a) > 2 and isinstance(a[2], float) else v
            if isinstance(opts.get("value"), float):
                val = opts["value"]
            m = {"n": a[0], "t": a[1], "v": round(val, 4)}
            f = flag_list(a[3] if len(a) > 3 else None)
            k = flag_list(a[4] if len(a) > 4 else None)
            if f: m["f"] = f
            if k: m["k"] = k
            c = [clean_condition(x) for x in conds if x.get("type") in ("SkillType", "SkillName")]
            if c: m["c"] = c
            mods_out.append(m)


def effectiveness(consts, gem, actor_level):
    return ((consts["SkillDamageBaseEffectiveness"] + consts["SkillDamageIncrementalEffectiveness"] * (actor_level - 1))
            * float(gem.get("baseEffectiveness") or 1)
            * (1 + float(gem.get("incrementalEffectiveness") or 0)) ** (actor_level - 1))


def lua_round(x):
    return float(int(x + 0.5)) if x >= 0 else -float(int(-x + 0.5))


def convert(key, gem, statmap, consts):
    own_map = gem.get("statMap") if isinstance(gem.get("statMap"), dict) else {}

    def entry(stat):
        return own_map.get(stat) if stat in own_map else statmap.get(stat)

    stats = [s for s in gem.get("stats") or [] if isinstance(s, str)]
    types = sorted(str(k).split(".")[-1] for k, v in (gem.get("skillTypes") or {}).items() if v is True) \
        if isinstance(gem.get("skillTypes"), dict) else []
    out = {
        "id": key,
        "types": types,
    }
    if gem.get("support"):
        out["support"] = True
    for f in ("castTime",):
        if isinstance(gem.get(f), float):
            out[f] = gem[f]
    flags = gem.get("baseFlags")
    if isinstance(flags, dict):
        out["flags"] = sorted(k for k, v in flags.items() if v is True)

    # Constant stats and base mods: the same at every level.
    const_skill, const_mods = {}, []
    for cs in gem.get("constantStats") or []:
        if isinstance(cs, list) and len(cs) >= 2 and isinstance(cs[0], str):
            apply_stat(entry(cs[0]), cs[1], const_skill, const_mods)
    for bm in gem.get("baseMods") or []:
        if isinstance(bm, Call):
            apply_stat([bm], 1.0, const_skill, const_mods)
    if const_skill: out["skill"] = {k: round(v, 4) for k, v in const_skill.items()}
    if const_mods: out["mods"] = const_mods

    quality = []
    for q in gem.get("qualityStats") or []:
        if isinstance(q, list) and len(q) >= 2 and isinstance(q[0], str):
            apply_stat(entry(q[0]), q[1], {}, quality)
    if quality: out["quality"] = quality  # values per 1% quality

    levels = {}
    for lvl, row in (gem.get("levels") or {}).items():
        if not isinstance(row, dict) or not isinstance(lvl, int):
            continue
        interp = row.get("statInterpolation") or []
        actor = float(row.get("levelRequirement") or 1)
        sk, mods = {}, []
        for idx, stat in enumerate(stats, 1):
            raw = row.get(idx)
            if raw is None:
                continue
            mode = interp[idx - 1] if isinstance(interp, list) and len(interp) >= idx else 1
            val = lua_round(effectiveness(consts, gem, actor) * raw) if mode == 3.0 and isinstance(raw, float) else raw
            apply_stat(entry(stat), val, sk, mods)
        L = {"req": int(actor)}
        for src_key, dst in (("critChance", "crit"), ("damageEffectiveness", "eff"), ("baseMultiplier", "mult"),
                             ("attackSpeedMultiplier", "aspd"), ("attackTime", "atime"), ("cooldown", "cd")):
            if isinstance(row.get(src_key), float):
                L[dst] = round(row[src_key], 4)
        if sk: L["skill"] = {k: round(v, 4) for k, v in sk.items()}
        if mods: L["mods"] = mods
        levels[str(lvl)] = L
    if levels:
        out["levels"] = levels
    return out


def main():
    pob, dest = sys.argv[1], sys.argv[2]
    data = os.path.join(pob, "src", "Data")
    misc = open(os.path.join(data, "Misc.lua"), encoding="utf-8").read()
    consts = {k: float(v) for k, v in re.findall(r'\["(SkillDamage\w+Effectiveness)"\]\s*=\s*([\d.]+)', misc)}
    statmap = parse_return_table(open(os.path.join(data, "SkillStatMap.lua"), encoding="utf-8").read())

    skills = {}
    for fn in sorted(os.listdir(os.path.join(data, "Skills"))):
        if not fn.endswith(".lua"):
            continue
        src = open(os.path.join(data, "Skills", fn), encoding="utf-8").read()
        for key, gem in parse_assignments(src, "skills").items():
            if not isinstance(gem, dict) or not isinstance(gem.get("name"), str):
                continue
            conv = convert(key, gem, statmap, consts)
            if "levels" not in conv:
                continue
            name = gem["name"]
            if gem.get("hidden") and name in skills:
                continue
            # Supports are "X Support" on the character API.
            if gem.get("support") and not name.endswith(" Support"):
                name += " Support"
            if name in skills and not skills[name].get("_hidden"):
                continue
            if gem.get("hidden"):
                conv["_hidden"] = True
            skills[name] = conv

    for s in skills.values():
        s.pop("_hidden", None)
    manifest = open(os.path.join(pob, "manifest.xml"), encoding="utf-8").read()
    version = re.search(r'<Version number="([^"]+)"', manifest).group(1)
    try:
        commit = subprocess.run(["git", "-C", pob, "rev-parse", "--short", "HEAD"], capture_output=True, text=True).stdout.strip()
    except OSError:
        commit = ""
    out = {
        "source": f"Path of Building Community {version}" + (f" ({commit})" if commit else ""),
        "licence": "MIT, (c) 2016 David Gowor and Path of Building Community contributors; stat data (c) Grinding Gear Games",
        "skills": dict(sorted(skills.items())),
    }
    with open(dest, "w", encoding="utf-8", newline="\n") as f:
        json.dump(out, f, separators=(",", ":"), ensure_ascii=False)
    actives = sum(1 for s in skills.values() if not s.get("support"))
    print(f"{len(skills)} gems ({actives} active, {len(skills) - actives} support) -> {dest} ({os.path.getsize(dest) // 1024} KB)")


if __name__ == "__main__":
    main()
