"""
Scan FF9 field event scripts (.eb / .eb.bytes) for how each field sets up its player.

For every script: the character entries (player_link), their SetModel / animation opcodes, which entries call
DefinePlayerCharacter (0x2C), and every InitObject (0x09) call (team slots 251-254 vs explicit entries).

The opcode tables are read from Memoria's own parser (EventEngineUtils.cs, BinaryScript) so they stay in sync.
Usage:  python scan_field_scripts.py <EventEngineUtils.cs> <folder or .eb file>... [--json out.json]
"""
import json
import os
import re
import sys

CHARACTERS = {0: "Zidane", 1: "Vivi", 2: "Garnet", 3: "Steiner", 4: "Freya", 5: "Quina", 6: "Eiko", 7: "Amarant",
              8: "Beatrix", 9: "Cinna", 10: "Marcus", 11: "Blank"}

OP = {"NEW": 0x07, "NEW2": 0x08, "NEW3": 0x09, "REQ": 0x10, "REQSW": 0x12, "REQEW": 0x14, "POS": 0x1D,
      "MAPJUMP": 0x2B, "CC": 0x2C, "MODEL": 0x2F, "AIDLE": 0x33, "AWALK": 0x34, "ARUN": 0x35,
      "ATURNL": 0x7A, "ATURNR": 0x7B, "CHRSCALE": 0x9F, "PARTYMENU": 0xB2, "FULLMEMBER": 0xB4,
      "PARTYDELETE": 0xDD, "SYNCPARTY": 0xE9}


def load_tables(utils_cs):
    src = open(utils_cs, encoding="utf-8-sig").read()
    m = re.search(r"static SByte\[\] opArgCount = new SByte\[\]\s*\{(.*?)\};", src, re.S)
    counts = [int(x) for x in re.findall(r"-?\d+", m.group(1))]
    m = re.search(r"static Byte\[\]\[\] opArgSize = new Byte\[\]\[\]\s*\{(.*?)\n\s*\};", src, re.S)
    sizes = []
    for item in re.finditer(r"null|new Byte\[\]\s*\{([^}]*)\}", m.group(1)):
        sizes.append(None if item.group(0) == "null" else [int(x) for x in re.findall(r"\d+", item.group(1))])
    return counts, sizes


class Parser:
    def __init__(self, counts, sizes):
        self.counts, self.sizes = counts, sizes

    def arg_size(self, op, i):
        if op == 0x29:
            return 4
        if op in (0x06, 0x0B, 0x0D):
            return 2
        s = self.sizes[op] if op < len(self.sizes) else None
        return s[i] if s is not None and i < len(s) else 0

    def expression(self, raw, pos):
        ops = []
        while True:
            op = raw[pos]; pos += 1
            is_var = op >= 0xC0 or op in (0x29, 0x5F, 0x78, 0x79, 0x7A)
            is_const = op in (0x7D, 0x7E)
            args = b""
            if is_var or is_const:
                n = 4 if op == 0x7E else 2 if (op >= 0xE0 or op in (0x78, 0x7D)) else 1
                args = raw[pos:pos + n]; pos += n
            ops.append((op, args))
            if op == 0x7F:
                return ("expr", ops), pos

    def code(self, raw, pos):
        op = raw[pos]; pos += 1
        if op == 0xFF:
            op = 0x100 | raw[pos]; pos += 1
        count = self.counts[op]
        flag = 0
        if op >= 0x10 and count != 0:
            flag = raw[pos]; pos += 1
        if op == 0x05:
            flag = 1
        if count < 0:
            count = raw[pos]; pos += 1
            if op == 0x0D:
                count |= raw[pos] << 8; pos += 1
            if op == 0x06:
                count = 1 + 2 * count
            elif op in (0x0B, 0x0D):
                count = 2 + count
        args = []
        for i in range(count):
            if flag & (1 << i):
                a, pos = self.expression(raw, pos)
            else:
                size = self.arg_size(op, i)
                a = int.from_bytes(raw[pos:pos + size], "little") if size else 0
                pos += size
            args.append(a)
        return op, args, pos


def parse_file(path, parser):
    raw = open(path, "rb").read()
    if raw[:2] != b"EV":
        return None
    version = raw[2]
    if version > 2:
        mode, count, pos = raw[3], raw[4], 5
    else:
        mode, count, pos = 0, raw[3], 4
    if mode == 0:
        pos += 124
    table_start = pos
    table = []
    for i in range(count):
        if version > 2:
            mem_id, link = int.from_bytes(raw[pos:pos + 4], "little", signed=True), int.from_bytes(raw[pos + 4:pos + 8], "little", signed=True)
            pos += 9
        else:
            mem_id = i
            link = 8 if i + 1 >= count else (i + 9 - count) if i + 9 >= count else -1
        ofs, size = int.from_bytes(raw[pos:pos + 2], "little"), int.from_bytes(raw[pos + 2:pos + 4], "little")
        pos += 8
        table.append({"sid": i, "memoria_id": mem_id, "link": link, "ofs": ofs, "size": size})
    base = pos if version > 2 else table_start
    for e in table:
        e["functions"] = []
        if e["size"] == 0:
            continue
        p = base + e["ofs"]
        end = p + e["size"]
        p += 1  # type
        fcount = raw[p]; p += 1
        fbase = p
        tags = []
        for _ in range(fcount):
            tags.append((int.from_bytes(raw[p:p + 2], "little"), int.from_bytes(raw[p + 2:p + 4], "little")))
            p += 4
        for k, (tag, fpos) in enumerate(tags):
            q = fbase + fpos
            qend = fbase + tags[k + 1][1] if k + 1 < len(tags) else end
            codes = []
            try:
                while q < qend:
                    op, args, q = parser.code(raw, q)
                    codes.append((op, args))
            except (IndexError, TypeError) as err:
                codes.append(("PARSE_ERROR", str(err)))
            e["functions"].append({"tag": tag, "codes": codes})
    return {"file": os.path.basename(path), "version": version, "entries": table}


def const(a):
    return a if isinstance(a, int) else "expr"


def summarize(script):
    out = {"file": script["file"], "characters": [], "cc_entries": [], "init_object": [], "party_ops": []}
    for e in script["entries"]:
        calls_cc = []
        model = anims = None
        for f in e["functions"]:
            for op, args in f["codes"]:
                if op == OP["CC"]:
                    calls_cc.append(f["tag"])
                elif op == OP["MODEL"]:
                    model = (const(args[0]), const(args[1]))
                elif op in (OP["AIDLE"], OP["AWALK"], OP["ARUN"]):
                    anims = anims or {}
                    anims[{OP["AIDLE"]: "idle", OP["AWALK"]: "walk", OP["ARUN"]: "run"}[op]] = const(args[0])
                elif op == OP["NEW3"]:
                    out["init_object"].append({"from_sid": e["sid"], "func": f["tag"], "target": const(args[0]), "uid": const(args[1])})
                elif op in (OP["PARTYDELETE"], OP["SYNCPARTY"], OP["PARTYMENU"], OP["FULLMEMBER"]):
                    out["party_ops"].append({"sid": e["sid"], "op": hex(op)})
        if e["link"] >= 0:
            out["characters"].append({"sid": e["sid"], "char": CHARACTERS.get(e["link"], e["link"]), "size": e["size"],
                                      "model": model, "anims": anims, "cc": calls_cc})
        if calls_cc:
            out["cc_entries"].append({"sid": e["sid"], "char": CHARACTERS.get(e["link"], e["link"]) if e["link"] >= 0 else None, "funcs": calls_cc, "model": model})
    return out


def main():
    utils_cs, targets = sys.argv[1], [a for a in sys.argv[2:] if not a.startswith("--")]
    json_out = sys.argv[sys.argv.index("--json") + 1] if "--json" in sys.argv else None
    if json_out in targets:
        targets.remove(json_out)
    parser = Parser(*load_tables(utils_cs))
    files = []
    for t in targets:
        if os.path.isdir(t):
            files += sorted(os.path.join(t, f) for f in os.listdir(t) if ".eb" in f)
        else:
            files.append(t)
    results = []
    for path in files:
        script = parse_file(path, parser)
        if script:
            results.append(summarize(script))
    if json_out:
        json.dump(results, open(json_out, "w"), indent=1)
    for r in results:
        print(r["file"])
        for c in r["characters"]:
            print(f"   char {c['char']:8} sid {c['sid']:3} size {c['size']:5} model {c['model']} anims {c['anims']} CC in funcs {c['cc']}")
        others = [c for c in r["cc_entries"] if c["char"] is None]
        for c in others:
            print(f"   non-character entry sid {c['sid']} calls CC (funcs {c['funcs']}, model {c['model']})")
        team = [i for i in r["init_object"] if isinstance(i["target"], int) and 251 <= i["target"] <= 254]
        explicit = [i for i in r["init_object"] if not (isinstance(i["target"], int) and 251 <= i["target"] <= 254)]
        print(f"   InitObject: {len(team)} via team slots {sorted(set(i['target'] for i in team))}, {len(explicit)} explicit")
        if r["party_ops"]:
            print(f"   party ops: {sorted(set(p['op'] for p in r['party_ops']))}")


if __name__ == "__main__":
    main()
