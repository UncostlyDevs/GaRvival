"""Builds the Guns and Robots protocol manifest from decompiled client code.

    python protocol_extract.py <decompiled Assembly-CSharp dir> <output dir> [GameServer.cs]

Reads the client's Photon peers (LoadbalancingPeer, NetworkingPeer, GamePeer)
and writes protocol.json plus a readable MESSAGES.md:
  - every operation the client sends, with parameter keys and types
  - every operation response and event it handles, with the keys it reads
Types marked proto/lz4/bzip2 are messages from junkwars.proto.
Decompile first with:  ilspycmd -p -o <dir> GAR_Data/Managed/Assembly-CSharp.dll
"""
import glob
import json
import os
import re
import sys

PEER_FILES = ["LoadbalancingPeer.cs", "NetworkingPeer.cs", "GamePeer.cs"]
VARS = r"(?:operationResponse|photonEvent|eventData|evData|response)"
KEY = r"(\d+|byte\.MaxValue)"
WRAP = {"": "proto", "Compressed": "lz4", "HighCompressed": "bzip2"}


def read(path):
    with open(path, encoding="utf-8", errors="replace") as f:
        return f.read()


def num(text):
    return 255 if text == "byte.MaxValue" else int(text)


def consts(path):
    names = {}
    for m in re.finditer(r"public const byte (\w+) = (\d+|byte\.MaxValue);", read(path)):
        names.setdefault(num(m.group(2)), []).append(m.group(1))
    return {k: "/".join(v) for k, v in names.items()}


def methods(text):
    """Yields (name, signature params, body) for every method in a file."""
    lines = text.split("\n")
    i = 0
    while i < len(lines):
        m = re.match(r"^(\t+)(?:public|protected|private|internal)[^=;(]*?\b(\w+)\(([^)]*)\)\s*$", lines[i])
        if m and i + 1 < len(lines) and lines[i + 1] == m.group(1) + "{":
            indent = m.group(1)
            j = i + 2
            while j < len(lines) and lines[j] != indent + "}":
                j += 1
            yield m.group(2), m.group(3), "\n".join(lines[i + 2:j])
            i = j
        i += 1


def param_types(signature):
    out = {}
    for part in re.split(r",(?![^<]*>)", signature):
        part = part.split("=")[0].strip()
        if " " in part:
            typ, name = part.rsplit(" ", 1)
            out[name] = typ.replace("this ", "").strip()
    return out


def expr_type(expr, params):
    expr = expr.strip()
    m = re.match(r"DataSerialization\.ProtoSerializeObject(\w*)\((\w+)\)$", expr)
    if m:
        return "%s<%s>" % (WRAP.get(m.group(1), "proto"), params.get(m.group(2), m.group(2)))
    m = re.match(r"\(([\w\[\]<>., ]+)\)\(?[\w.]+", expr)
    if m:
        return m.group(1)
    if expr in params:
        return params[expr]
    if re.match(r'^".*"$', expr):
        return "string"
    if expr in ("true", "false"):
        return "bool"
    return "= " + expr


def extract_requests(folder):
    requests = []
    for name in PEER_FILES:
        for method, signature, body in methods(read(os.path.join(folder, name))):
            for call in re.finditer(r"OpCustom\((?:\(byte\))?(\d+),\s*(\w+)([^;]*);", body):
                code, var, rest = int(call.group(1)), call.group(2), call.group(3)
                params = param_types(signature)
                keys = []
                for a in re.finditer(r"^(\t+)" + re.escape(var) + r"\[" + KEY + r"\] = (.+);$", body, re.M):
                    keys.append({"key": num(a.group(2)), "type": expr_type(a.group(3), params), "optional": len(a.group(1)) > 2})
                requests.append({"code": code, "method": method, "peer": name[:-3], "encrypted": "encrypt: true" in rest,
                                 "signature": signature, "params": keys})
    return requests


def case_blocks(body, switch_on):
    """Splits a handler's top-level switch into {codes: block text}."""
    lines = body.split("\n")
    start = next((i for i, l in enumerate(lines) if "switch (" + switch_on in l), None)
    if start is None:
        return []
    indent = None
    blocks = []
    labels, text, prev_label = [], [], False
    for line in lines[start + 1:]:
        m = re.match(r"^(\t+)(?:case " + KEY + r"|default):\s*$", line)
        if m and indent is None and m.group(2):
            indent = m.group(1)
        if m and m.group(1) == indent:
            if not prev_label:
                if labels:
                    blocks.append((labels, "\n".join(text)))
                labels, text = [], []
            if m.group(2):
                labels.append(num(m.group(2)))
            prev_label = True
            continue
        if indent is not None and line == indent[:-1] + "}":
            break
        prev_label = False
        text.append(line)
    if labels:
        blocks.append((labels, "\n".join(text)))
    return blocks


def read_keys(text):
    keys = {}
    for m in re.finditer(VARS + r"(?:\.Parameters)?\[" + KEY + r"\]", text):
        key = num(m.group(1))
        pre = text[max(0, m.start() - 160):m.start()].split("\n")[-1]
        typ = None
        d = re.search(r"DeserializeObject(\w*)<(.+)>\(\(byte\[\]\)$", pre)
        if d:
            typ = "%s<%s>" % (WRAP.get(d.group(1), "proto"), d.group(2))
        else:
            c = re.search(r"\(([A-Za-z_][\w.\[\]<>, ]*)\)$", pre)
            if c:
                typ = c.group(1)
        if typ or key not in keys:
            if key not in keys or keys[key] in (None, "byte[]"):
                keys[key] = typ
    for m in re.finditer(r"ContainsKey\(" + KEY + r"\)", text):
        keys.setdefault(num(m.group(1)), None)
    return keys


def listeners(folder):
    """signal name -> handler body, from AddListener("name", Handler) calls."""
    out = {}
    for path in glob.glob(os.path.join(folder, "**", "*.cs"), recursive=True):
        text = read(path)
        wanted = dict(re.findall(r'AddListener<[^>]*>\("([^"]+)",\s*(\w+)\)', text))
        if not wanted:
            continue
        bodies = {name: body for name, _, body in methods(text)}
        for signal, handler in wanted.items():
            if handler in bodies:
                out.setdefault(signal, []).append(bodies[handler])
    return out


def extract_handlers(folder, handler, variable, switch_on, signal_map):
    result = {}
    for name in PEER_FILES[1:]:
        for method, _, body in methods(read(os.path.join(folder, name))):
            if method != handler:
                continue
            for codes, block in case_blocks(body, switch_on):
                keys = read_keys(block)
                signals = sorted(set(re.findall(r'Invoke\("([^"]+)"', block)))
                for signal in signals:
                    # Handlers that receive the whole response/event read more keys.
                    if re.search(r'Invoke\("%s",\s*new DataMessage\(%s' % (re.escape(signal), variable), block):
                        for extra in signal_map.get(signal, []):
                            for k, t in read_keys(extra).items():
                                if k not in keys or keys[k] in (None, "byte[]"):
                                    keys[k] = t or keys.get(k)
                entry = {
                    "params": [{"key": k, "type": t or "?"} for k, t in sorted(keys.items())],
                    "signals": signals,
                    "returnCodes": sorted(set(int(x) for x in re.findall(r"ReturnCode [!=]= (-?\d+)", block)) - {0}),
                    "decoders": sorted(set(re.findall(r"(\w+)\.BuildFromHashTable", block))),
                }
                for code in codes:
                    result.setdefault(code, {})[name[:-3]] = entry
    return result


def main():
    folder, out_dir = sys.argv[1], sys.argv[2]
    op_names = consts(os.path.join(folder, "OperationCode.cs"))
    event_names = consts(os.path.join(folder, "EventCode.cs"))
    param_names = consts(os.path.join(folder, "ParameterCode.cs"))
    implemented = set()
    if len(sys.argv) > 3:
        implemented = set(int(x) for x in re.findall(r"^\s+case (\d+):", read(sys.argv[3]), re.M))

    signal_map = listeners(folder)
    requests = extract_requests(folder)
    responses = extract_handlers(folder, "OnOperationResponse", "operationResponse", "operationResponse.OperationCode", signal_map)
    events = extract_handlers(folder, "OnEvent", "photonEvent", "photonEvent.Code", signal_map)

    def label(params):
        for p in params:
            p["name"] = param_names.get(p["key"], "")
        return params

    operations = []
    for code in sorted(set(r["code"] for r in requests) | set(responses) | set(op_names), reverse=True):
        operations.append({
            "code": code,
            "name": op_names.get(code, ""),
            "offlineServer": code in implemented,
            "requests": [{"method": r["method"], "peer": r["peer"], "encrypted": r["encrypted"], "params": label(r["params"])}
                         for r in requests if r["code"] == code],
            "responses": {peer: dict(e, params=label(e["params"])) for peer, e in responses.get(code, {}).items()},
        })
    event_list = []
    for code in sorted(set(events) | set(event_names), reverse=True):
        event_list.append({"code": code, "name": event_names.get(code, ""),
                           "handlers": {peer: dict(e, params=label(e["params"])) for peer, e in events.get(code, {}).items()}})

    manifest = {"parameterCodes": {str(k): v for k, v in sorted(param_names.items())}, "operations": operations, "events": event_list}
    os.makedirs(out_dir, exist_ok=True)
    with open(os.path.join(out_dir, "protocol.json"), "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=1)

    def table(params):
        if not params:
            return "_no parameters_\n"
        rows = ["| key | name | type |", "| --- | --- | --- |"]
        for p in params:
            rows.append("| %d | %s | `%s`%s |" % (p["key"], p["name"], p["type"], " (optional)" if p.get("optional") else ""))
        return "\n".join(rows) + "\n"

    md = ["# Guns and Robots message catalogue", "",
          "Generated by `tools/protocol_extract.py` from the client code. See PROTOCOL.md for the",
          "transport, connection flow and conventions. `proto<T>`, `lz4<T>` and `bzip2<T>` are",
          "`T` from junkwars.proto, as raw protobuf, LZ4-stream or BZip2 compressed bytes.",
          "`?` means the client reads the key but its type could not be inferred.", "",
          "## Operations (client -> server, with the server's response)", ""]
    for op in operations:
        md.append("### %d %s%s" % (op["code"], op["name"], "  (offline server: yes)" if op["offlineServer"] else ""))
        md.append("")
        for r in op["requests"]:
            md.append("**Request** `%s.%s`%s" % (r["peer"], r["method"], ", encrypted" if r["encrypted"] else ""))
            md.append("")
            md.append(table(r["params"]))
        if not op["requests"]:
            md.append("_The client never sends this operation._\n")
        for peer, e in op["responses"].items():
            extra = []
            if e["returnCodes"]:
                extra.append("special return codes: " + ", ".join(str(c) for c in e["returnCodes"]))
            if e["signals"]:
                extra.append("raises: " + ", ".join("`%s`" % s for s in e["signals"]))
            md.append("**Response** handled by `%s`%s" % (peer, " (" + "; ".join(extra) + ")" if extra else ""))
            md.append("")
            md.append(table(e["params"]))
    md += ["## Events (server -> client)", ""]
    for ev in event_list:
        md.append("### %d %s" % (ev["code"], ev["name"]))
        md.append("")
        if not ev["handlers"]:
            md.append("_Named in the client but not handled._\n")
        for peer, e in ev["handlers"].items():
            extra = []
            if e["signals"]:
                extra.append("raises: " + ", ".join("`%s`" % s for s in e["signals"]))
            if e["decoders"]:
                extra.append("payload decoded by: " + ", ".join("`%s`" % s for s in e["decoders"]))
            md.append("Handled by `%s`%s" % (peer, " (" + "; ".join(extra) + ")" if extra else ""))
            md.append("")
            md.append(table(e["params"]))
    with open(os.path.join(out_dir, "MESSAGES.md"), "w", encoding="utf-8") as f:
        f.write("\n".join(md))

    sent = sum(1 for o in operations if o["requests"])
    print("operations: %d named/seen, %d sent by client, %d with a response handler, %d answered by the offline server" % (
        len(operations), sent, sum(1 for o in operations if o["responses"]), sum(1 for o in operations if o["offlineServer"])))
    print("events: %d named/seen, %d handled" % (len(event_list), sum(1 for e in event_list if e["handlers"])))
    print("request variants: %d, untyped keys: %d" % (len(requests), sum(1 for o in operations for e in o["responses"].values() for p in e["params"] if p["type"] == "?")
          + sum(1 for ev in event_list for e in ev["handlers"].values() for p in e["params"] if p["type"] == "?")))


if __name__ == "__main__":
    main()
