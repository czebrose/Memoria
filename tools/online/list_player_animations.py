"""
List every animation the field scripts play on the player character's body: entries that call
DefinePlayerCharacter (0x2C) and are created with the given model, plus RunAnimationEx (0xBD) calls from other
entries that target them. Prints id, name, total uses, number of fields, and how it's used.
Used to build the list of actions every playable character needs (docs/online/step2-m3-own-leader.md).

Usage:  python list_player_animations.py <repo> <mod folder> [player model id, default 202 = Clove]
"""
import collections
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import scan_field_scripts as s

KIND = {0x33: "stand", 0x34: "walk", 0x35: "run", 0x7A: "turnL", 0x7B: "turnR", 0x94: "jump", 0x52: "idle-long",
        0x40: "play", 0xBD: "play(by other)"}


def load_names(repo, mod):
    names = {}
    for line in open(os.path.join(repo, "Assembly-CSharp", "Memoria", "Characters", "AnimationList.csv"), encoding="utf-8-sig"):
        p = line.strip().split(";")
        if len(p) >= 2 and p[0].isdigit():
            names[int(p[0])] = p[1]
    patch = os.path.join(mod, "DictionaryPatch.txt")
    if os.path.exists(patch):
        for line in open(patch, encoding="utf-8", errors="replace"):
            m = re.match(r"3DModelAnimation\s+(\d+)\s+(\S+)", line)
            if m:
                names[int(m.group(1))] = m.group(2)
    return names


def main():
    repo, mod = sys.argv[1], sys.argv[2]
    player_model = int(sys.argv[3]) if len(sys.argv) > 3 else 202
    fields = os.path.join(mod, "StreamingAssets", "assets", "resources", "commonasset", "eventengine", "eventbinary", "field", "us")
    names = load_names(repo, mod)
    parser = s.Parser(*s.load_tables(os.path.join(repo, "Assembly-CSharp", "Global", "Event", "Engine", "EventEngineUtils.cs")))
    uses = collections.defaultdict(collections.Counter)
    fields_of = collections.defaultdict(set)
    for f in sorted(os.listdir(fields)):
        script = s.parse_file(os.path.join(fields, f), parser)
        if not script:
            continue
        entries = script["entries"]
        player, model_of = set(), {}
        for e in entries:
            for fn in e["functions"]:
                for op, args in fn["codes"]:
                    if op == 0x2C:
                        player.add(e["sid"])
                    elif op == 0x2F and isinstance(args[0], int):
                        model_of[e["sid"]] = args[0]
        body = {sid for sid in player if model_of.get(sid) == player_model}
        uids = {250}  # 250 = "the player"; plus the UIDs the body entries are created with
        for e in entries:
            for fn in e["functions"]:
                for op, args in fn["codes"]:
                    if op == 0x09 and args[0] in body and isinstance(args[1], int):
                        uids.add(args[1])
        for e in entries:
            for fn in e["functions"]:
                for op, args in fn["codes"]:
                    if op not in KIND:
                        continue
                    if op == 0xBD:
                        if e["sid"] in body or args[0] not in uids:
                            continue
                        anim = args[1]
                    elif e["sid"] in body:
                        anim = args[0]
                    else:
                        continue
                    key = anim if isinstance(anim, int) else "expr"
                    uses[key][KIND[op]] += 1
                    fields_of[key].add(f.split(".")[0])
    for anim, kinds in sorted(uses.items(), key=lambda kv: -sum(kv[1].values())):
        print(f"{anim}\t{names.get(anim, '?')}\t{sum(kinds.values())} uses\t{len(fields_of[anim])} fields\t{dict(kinds)}")


if __name__ == "__main__":
    main()
