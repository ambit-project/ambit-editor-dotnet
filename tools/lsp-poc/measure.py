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
samples = []
def sampler():
    while proc.poll() is None:
        samples.append((round(time.time()-t0), rss(proc.pid))); time.sleep(2)
threading.Thread(target=sampler, daemon=True).start()
pump(lambda m: m.get("method") == "workspace/projectInitializationComplete", timeout=900)
log("projects loaded; RSS server MB:", rss(proc.pid))

import re
text = pathlib.Path(file_path).read_text()
uri = pathlib.Path(file_path).as_uri()
notify("textDocument/didOpen", {"textDocument": {"uri": uri, "languageId": "csharp", "version": 1, "text": text}})
m = re.search(r"\b(_\w+|\w+)\.(\w+)\(", text)
off = m.start(2); before = text[:off].split("\n")
pos = {"line": len(before)-1, "character": len(before[-1])}
log("probe", m.group(0), pos)
hid = request("textDocument/hover", {"textDocument": {"uri": uri}, "position": {"line": pos["line"], "character": pos["character"] - 3}})
log("hover:", json.dumps(pump(lambda m: m.get("id") == hid)["result"])[:300])
did = request("textDocument/diagnostic", {"textDocument": {"uri": uri}})
d = pump(lambda m: m.get("id") == did).get("result") or {}
log("diagnostics:", len(d.get("items", [])), [x["message"][:90] for x in d.get("items", [])][:5])
def comp(position, ctx):
    c = request("textDocument/completion", {"textDocument": {"uri": uri}, "position": position, "context": ctx})
    r = pump(lambda m: m.get("id") == c)["result"]
    return None if r is None else len(r["items"] if isinstance(r, dict) else r)
log("invoked at dot:", comp(pos, {"triggerKind": 1}))
log("invoked inside word:", comp({"line": pos["line"], "character": pos["character"] + 4}, {"triggerKind": 1}))
lines = text.split("\n"); lines.insert(pos["line"] + 1, "    void Probe() { TimeSpan. }")
notify("textDocument/didChange", {"textDocument": {"uri": uri, "version": 2}, "contentChanges": [{"text": "\n".join(lines)}]})
log("new line 'TimeSpan.' trigger:", comp({"line": pos["line"] + 1, "character": len("    void Probe() { TimeSpan.")}, {"triggerKind": 2, "triggerCharacter": "."}))
for i in range(0):
    t = time.time()
    cid = request("textDocument/completion", {"textDocument": {"uri": uri}, "position": pos, "context": {"triggerKind": 2, "triggerCharacter": "."}})
    res = pump(lambda m: m.get("id") == cid, timeout=120)["result"]
    if i == 0: log("raw:", json.dumps(res)[:600])
    items = (res or {}).get("items", []) if isinstance(res, dict) or res is None else res
    log(f"completion {i+1}: {len(items)} items in {(time.time()-t)*1000:.0f} ms", [x["label"] for x in items][:8])
log("RSS server MB 20 s after first completion:", rss(proc.pid))
log("peak RSS MB:", max(r for _, r in samples), "samples:", samples[::5])
time.sleep(15)
log("RSS MB at end:", rss(proc.pid))
sd = request("shutdown", None); pump(lambda m: m.get("id") == sd); notify("exit", None)
proc.wait(30); log("exit code", proc.returncode)
