"""Milestone 1 PoC: drive roslyn-language-server over stdio, open a solution, ask for completion."""
import json, os, subprocess, sys, threading, time, queue, pathlib

server, solution, file_path = sys.argv[1], sys.argv[2], sys.argv[3]
logdir = os.path.join(os.path.dirname(__file__), "logs")
proc = subprocess.Popen([server, "--stdio", "--telemetryLevel", "off", "--logLevel", "Information", "--extensionLogDirectory", logdir],
                        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=open(os.path.join(os.path.dirname(__file__), "stderr.log"), "wb"))
inbox = queue.Queue()
t0 = time.time()
def log(*a): print(f"[{time.time()-t0:6.2f}s]", *a, flush=True)

def reader():
    out = proc.stdout
    while True:
        headers = {}
        while True:
            line = out.readline()
            if not line: inbox.put(None); return
            line = line.decode().strip()
            if not line: break
            k, v = line.split(":", 1); headers[k.lower()] = v.strip()
        body = out.read(int(headers["content-length"]))
        inbox.put(json.loads(body))
threading.Thread(target=reader, daemon=True).start()

next_id = 0
def send(msg):
    data = json.dumps(msg).encode()
    proc.stdin.write(b"Content-Length: %d\r\n\r\n" % len(data) + data); proc.stdin.flush()
def request(method, params):
    global next_id; next_id += 1
    send({"jsonrpc": "2.0", "id": next_id, "method": method, "params": params}); return next_id
def notify(method, params): send({"jsonrpc": "2.0", "method": method, "params": params})

def pump(until, timeout=300):
    """Handle server messages until `until(msg)` is true; answer server requests."""
    end = time.time() + timeout
    while time.time() < end:
        try: msg = inbox.get(timeout=0.5)
        except queue.Empty: continue
        if msg is None: log("server exited"); sys.exit(1)
        if "method" in msg and "id" in msg:   # server -> client request
            m = msg["method"]
            result = None
            if m == "workspace/configuration":
                result = [None] * len(msg["params"]["items"])
                log("configuration asked:", len(msg["params"]["items"]), [i.get("section") for i in msg["params"]["items"]][:8])
            send({"jsonrpc": "2.0", "id": msg["id"], "result": result})
            if m not in ("window/workDoneProgress/create", "client/registerCapability", "workspace/configuration"): log("server request", m)
        elif "method" in msg:
            m = msg["method"]
            if m == "window/logMessage" and any(k in msg["params"]["message"] for k in ("ompletion", "xception", "signatureHelp")): log("dbg:", msg["params"]["message"][:700])
            elif m == "window/logMessage" and msg["params"]["type"] <= 2: log("log:", msg["params"]["message"][:900])
            elif m == "window/_roslyn_showToast": log("toast:", json.dumps(msg["params"])[:400])
            elif m == "$/progress":
                v = msg["params"]["value"]
                if v.get("kind") in ("begin", "end"): log("progress", v.get("kind"), v.get("title") or v.get("message") or "")
            elif m not in ("window/logMessage", "textDocument/publishDiagnostics"): log("notification", m)
        if until(msg): return msg
    raise TimeoutError

def rss(pid):
    total = 0
    for p in [pid] + [int(c) for c in subprocess.run(["pgrep", "-P", str(pid)], capture_output=True, text=True).stdout.split()]:
        try: total += int([l for l in open(f"/proc/{p}/status") if l.startswith("VmRSS")][0].split()[1])
        except Exception: pass
        for c in subprocess.run(["pgrep", "-P", str(p)], capture_output=True, text=True).stdout.split():
            try: total += int([l for l in open(f"/proc/{c}/status") if l.startswith("VmRSS")][0].split()[1])
            except Exception: pass
    return total // 1024

root = pathlib.Path(solution).parent.as_uri()
rid = request("initialize", {
    "processId": os.getpid(), "rootUri": root,
    "workspaceFolders": [{"uri": root, "name": "ws"}],
    "capabilities": {
        "general": {"positionEncodings": ["utf-16"]},
        "window": {"workDoneProgress": True},
        "workspace": {"configuration": True, "workspaceFolders": True},
        "textDocument": {
            "synchronization": {"didSave": True},
            "completion": {"completionItem": {"snippetSupport": False, "resolveSupport": {"properties": ["documentation", "detail", "additionalTextEdits"]}},
                           "completionList": {"itemDefaults": ["commitCharacters", "editRange", "insertTextFormat", "data"]}},
            "signatureHelp": {"signatureInformation": {"parameterInformation": {"labelOffsetSupport": True}, "activeParameterSupport": True}},
            "hover": {"contentFormat": ["markdown", "plaintext"]},
            "publishDiagnostics": {}, "diagnostic": {},
        }},
    "initializationOptions": {"autoLoadProjects": 0},
})
init = pump(lambda m: m.get("id") == rid)
caps = init["result"]["capabilities"]
log("initialized; server", init["result"].get("serverInfo"), "encoding", caps.get("positionEncoding"),
    "sync", caps.get("textDocumentSync"), "completion triggers", caps.get("completionProvider", {}).get("triggerCharacters"))
log("signature triggers", caps.get("signatureHelpProvider"))
notify("initialized", {})
notify("solution/open", {"solution": pathlib.Path(solution).as_uri()})
pump(lambda m: m.get("method") == "workspace/projectInitializationComplete")
log("projects loaded; RSS server MB:", rss(proc.pid))

text = pathlib.Path(file_path).read_text()
probe = "\nvar calc = new Sample.Core.Calculator();\ncalc."
text = text.replace("app.Run();", "app.Run();" + probe)
uri = pathlib.Path(file_path).as_uri()
notify("textDocument/didOpen", {"textDocument": {"uri": uri, "languageId": "csharp", "version": 1, "text": text}})
end = text.index("calc.\n") + len("calc."); before = text[:end].split("\n"); line = len(before) - 1; char = len(before[-1])
log("probe position", line, char, repr(text.split("\n")[line]))
did = request("textDocument/documentSymbol", {"textDocument": {"uri": uri}})
log("documentSymbol:", json.dumps(pump(lambda m: m.get("id") == did))[:300])
hid = request("textDocument/hover", {"textDocument": {"uri": uri}, "position": {"line": line - 1, "character": 5}})
log("hover:", json.dumps(pump(lambda m: m.get("id") == hid))[:300])
xid = request("textDocument/completion", {"textDocument": {"uri": uri}, "position": {"line": line, "character": char}, "context": {"triggerKind": 1}})
log("invoked completion:", json.dumps(pump(lambda m: m.get("id") == xid))[:300])
for attempt in range(2):
    t = time.time()
    cid = request("textDocument/completion", {"textDocument": {"uri": uri}, "position": {"line": line, "character": char},
                                               "context": {"triggerKind": 2, "triggerCharacter": "."}})
    raw = pump(lambda m: m.get("id") == cid)
    res = raw.get("result")
    if res is None: log("raw completion response:", json.dumps(raw)[:400]); time.sleep(2); continue
    items = res["items"] if isinstance(res, dict) else res
    if isinstance(res, dict): log("list keys", list(res.keys()), "defaults", json.dumps(res.get("itemDefaults"))[:200], "isIncomplete", res.get("isIncomplete"))
    log(f"completion #{attempt+1}: {len(items)} items in {(time.time()-t)*1000:.0f} ms;", [i["label"] for i in sorted(items, key=lambda i: i.get("sortText", i["label"]))][:12])
    if attempt == 0 and items:
        item = dict([i for i in items if i["label"] == "Sum"][0])
        for k, v in (res.get("itemDefaults") or {}).items():
            if k == "editRange": item.setdefault("textEdit", {"range": v, "newText": item.get("textEditText", item["label"])})
            else: item.setdefault(k, v)
        rid2 = request("completionItem/resolve", item)
        r = pump(lambda m: m.get("id") == rid2)["result"]
        log("resolve:", r.get("detail"), (r.get("documentation") or {}).get("value", "")[:100] if isinstance(r.get("documentation"), dict) else r.get("documentation"))

# signature help inside Sum(
text2 = text[:end] + "Sum(1, " + text[end:]
notify("textDocument/didChange", {"textDocument": {"uri": uri, "version": 2}, "contentChanges": [{"text": text2}]})
lines = text2[:end + len("Sum(1, ")].split("\n")
sid = request("textDocument/signatureHelp", {"textDocument": {"uri": uri}, "position": {"line": len(lines)-1, "character": len(lines[-1])},
                                             "context": {"triggerKind": 2, "triggerCharacter": ",", "isRetrigger": False}})
sh = pump(lambda m: m.get("id") == sid)["result"]
log("signature:", sh and [s["label"] for s in sh["signatures"]], "active param", sh and sh.get("activeParameter"))
log("RSS server MB after features:", rss(proc.pid))

sd = request("shutdown", None); pump(lambda m: m.get("id") == sd); notify("exit", None)
proc.wait(20); log("exit code", proc.returncode)
