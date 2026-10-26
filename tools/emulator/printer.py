#!/usr/bin/env python3
"""A pretend Bambu Lab P2S for developing Bambu Bar on Windows.

A self-contained port of scripts/emulator/printer.py from the macOS repo: same report, same
delta stream, same commands — but no mosquitto binary required. The broker is amqtt (Python),
the printer's pub/sub uses paho-mqtt, and the self-signed certs are generated with
`cryptography`, so only Python is needed.

Runs a TLS MQTT broker on 127.0.0.1:8883 that accepts user `bblp` with access code
`12345678`, answers `pushall` with a real P2S report (p2s-sample.json, from ha-bambulab) and
then streams deltas like the printer does: progress, layers, temperatures, fans. Pause,
resume, stop, speed and the chamber light work.

    python tools/emulator/printer.py             # then:
    BambuBar.exe --connect 127.0.0.1 22E00EMULATOR0 12345678 --insecure-tls
    BambuBar.exe --probe 127.0.0.1 22E00EMULATOR0 12345678 10

Needs: pip install amqtt paho-mqtt cryptography
"""
import asyncio, copy, json, os, random, signal, ssl, sys, threading, time
from datetime import datetime, timedelta, timezone

HERE = os.path.dirname(os.path.abspath(__file__))
SERIAL = "22E00EMULATOR0"
CODE = "12345678"
PORT = int(os.environ.get("PORT", "8883"))
CERTS = os.path.join(HERE, "certs")
SPEED = float(os.environ.get("SPEED", "1"))  # >1 prints faster


def make_certs():
    """CA + server cert (CN = the printer serial), like the macOS emulator's openssl steps."""
    os.makedirs(CERTS, exist_ok=True)
    server = os.path.join(CERTS, "server.pem")
    if os.path.exists(server):
        return

    from cryptography import x509
    from cryptography.x509.oid import NameOID
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.primitives.asymmetric import rsa

    now = datetime.now(timezone.utc) - timedelta(days=1)
    later = now + timedelta(days=3650)
    ca_key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    ca_name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "Emulator CA")])
    ca_cert = (
        x509.CertificateBuilder()
        .subject_name(ca_name).issuer_name(ca_name)
        .public_key(ca_key.public_key())
        .serial_number(x509.random_serial_number())
        .not_valid_before(now).not_valid_after(later)
        .add_extension(x509.BasicConstraints(ca=True, path_length=None), critical=True)
        .sign(ca_key, hashes.SHA256())
    )
    key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    cert = (
        x509.CertificateBuilder()
        .subject_name(x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, SERIAL)]))
        .issuer_name(ca_name)
        .public_key(key.public_key())
        .serial_number(x509.random_serial_number())
        .not_valid_before(now).not_valid_after(later)
        .sign(ca_key, hashes.SHA256())
    )
    with open(os.path.join(CERTS, "ca.pem"), "wb") as f:
        f.write(ca_cert.public_bytes(serialization.Encoding.PEM))
    with open(os.path.join(CERTS, "server.key"), "wb") as f:
        f.write(key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, serialization.NoEncryption()))
    with open(server, "wb") as f:
        f.write(cert.public_bytes(serialization.Encoding.PEM))


def make_password_file():
    """The broker's user database (amqtt's FileAuthPlugin; argon2-hashed, like mosquitto_passwd)."""
    from pwdlib import PasswordHash
    from pwdlib.hashers.argon2 import Argon2Hasher

    passwd = os.path.join(CERTS, "passwd")
    with open(passwd, "w") as f:
        f.write(f"bblp:{PasswordHash((Argon2Hasher(),)).hash(CODE)}\n")
    return passwd


async def run_broker(passwd):
    from amqtt.broker import Broker

    config = {
        "listeners": {
            "default": {
                "type": "tcp",
                "bind": f"127.0.0.1:{PORT}",
                "ssl": True,
                "certfile": os.path.join(CERTS, "server.pem"),
                "keyfile": os.path.join(CERTS, "server.key"),
            }
        },
        # The TLS listener is exactly like the printer's: no anonymous clients, bblp + access code only.
        "plugins": {
            "amqtt.plugins.authentication.FileAuthPlugin": {"password_file": passwd},
            "amqtt.plugins.authentication.AnonymousAuthPlugin": {"allow_anonymous": False},
        },
    }
    broker = Broker(config)
    await broker.start()
    print(f"Broker on 127.0.0.1:{PORT} (TLS, user bblp)", flush=True)
    await asyncio.Event().wait()


def merge(base, delta):
    """Applies a delta the way clients do: nested objects merge, arrays of objects merge by id."""
    for k, v in delta.items():
        if isinstance(v, dict) and isinstance(base.get(k), dict):
            merge(base[k], v)
        elif isinstance(v, list) and isinstance(base.get(k), list) and v and all(isinstance(i, dict) and "id" in i for i in v):
            for item in v:
                match = next((b for b in base[k] if isinstance(b, dict) and b.get("id") == item["id"]), None)
                if match is None:
                    base[k].append(item)
                else:
                    merge(match, item)
        else:
            base[k] = v


class Printer:
    def __init__(self, pub):
        sample = json.load(open(os.path.join(HERE, "p2s-sample.json")))
        self.report = sample["pushall"]["print"]
        self.version = sample["get_version"]
        self.report.update({"gcode_state": "PREPARE", "stg_cur": 7, "mc_percent": 0, "layer_num": 0, "mc_remaining_time": 95,
                            "subtask_name": "Planetary Gearbox", "fun": "60029FD183FF9CB7"})  # Developer Mode on
        self.report["device"]["extruder"]["info"][0]["temp"] = (220 << 16) | 150
        self.report["device"]["bed"]["info"]["temp"] = (55 << 16) | 52
        self.progress = 0.0
        self.lock = threading.Lock()
        self.pub = pub

    def publish(self, payload):
        self.pub.publish(f"device/{SERIAL}/report", json.dumps(payload), qos=0)

    def push_full(self):
        with self.lock:
            report = copy.deepcopy(self.report)
        report.update({"command": "push_status", "msg": 0})
        self.publish({"print": report})

    def delta(self, fields):
        with self.lock:
            merge(self.report, fields)
        self.publish({"print": {"command": "push_status", "msg": 1, **fields}})

    def handle(self, message):
        print(f"[printer] request: {message[:120]}", flush=True)
        try:
            data = json.loads(message)
        except ValueError:
            return
        if data.get("pushing", {}).get("command") == "pushall":
            self.push_full()
        elif data.get("info", {}).get("command") == "get_version":
            self.publish(self.version)
        elif "print" in data:
            cmd = data["print"].get("command")
            seq = data["print"].get("sequence_id", "0")
            state = self.report["gcode_state"]
            if cmd == "pause" and state in ("RUNNING", "PREPARE"):
                self.delta({"gcode_state": "PAUSE", "stg_cur": 16})
            elif cmd == "resume" and state == "PAUSE":
                self.delta({"gcode_state": "RUNNING", "stg_cur": 0})
            elif cmd == "stop":
                self.delta({"gcode_state": "FAILED", "stg_cur": 255, "print_error": 50348044})
            elif cmd == "print_speed":
                self.delta({"spd_lvl": int(data["print"].get("param", "2"))})
            self.publish({"print": {"command": cmd, "sequence_id": seq, "result": "success", "reason": ""}})
        elif data.get("system", {}).get("command") == "ledctrl":
            mode = data["system"].get("led_mode", "on")
            self.delta({"lights_report": [{"node": "chamber_light", "mode": mode}, {"node": "work_light", "mode": "flashing"}]})
            self.publish({"system": {**data["system"], "result": "success"}})

    def tick(self):
        """One second of printing."""
        r = self.report
        state = r["gcode_state"]
        nozzle = r["device"]["extruder"]["info"][0]["temp"]
        cur, tgt = nozzle & 0xFFFF, nozzle >> 16
        if state == "PREPARE":
            cur = min(tgt, cur + 6)
            fields = {"device": {"extruder": {"info": [{"id": 0, "temp": (tgt << 16) | cur}]}}, "nozzle_temper": float(cur)}
            if cur >= tgt:
                fields.update({"gcode_state": "RUNNING", "stg_cur": 0, "mc_percent": 1})
            self.delta(fields)
        elif state == "RUNNING":
            self.progress = min(100.0, self.progress + 0.25 * SPEED)
            pct = int(self.progress)
            total = r.get("total_layer_num", 305)
            jitter = (tgt << 16) | (tgt + random.choice([-1, 0, 0, 1]))
            fields = {"mc_percent": pct, "layer_num": int(total * self.progress / 100),
                      "mc_remaining_time": max(0, int(95 * (1 - self.progress / 100))),
                      "device": {"extruder": {"info": [{"id": 0, "temp": jitter}]},
                                 "airduct": {"parts": [{"id": 16, "state": random.choice([80, 90, 100])}]}}}
            if pct >= 100:
                fields.update({"gcode_state": "FINISH", "stg_cur": 255, "mc_remaining_time": 0})
            self.delta(fields)
        else:
            self.delta({"wifi_signal": f"-{random.randint(48, 56)}dBm"})


def make_paho_client(client_id, on_message=None, subscribe_topic=None):
    """A paho client that talks to the emulator broker the way the app does (TLS 1.2+, bblp + code)."""
    import paho.mqtt.client as pmqtt

    c = pmqtt.Client(pmqtt.CallbackAPIVersion.VERSION2, client_id=client_id)
    c.tls_set(cert_reqs=ssl.CERT_NONE)  # self-signed emulator CA
    c.username_pw_set("bblp", CODE)
    c.on_connect = lambda _c, _u, _f, reason, properties=None: (
        print(f"[{client_id}] connected rc={getattr(reason, 'value', reason)}", flush=True),
        (print(f"[{client_id}] subscribe {subscribe_topic} rc={_c.subscribe(subscribe_topic)[1]}", flush=True) if subscribe_topic else None),
    )
    c.on_disconnect = lambda _c, _u, _f, reason, properties=None: print(f"[{client_id}] DISCONNECTED rc={getattr(reason, 'value', '?')}", flush=True)
    c.connect("127.0.0.1", PORT)
    if on_message is not None:
        c.on_message = lambda _c, _u, message: on_message(message.payload.decode())
    c.loop_start()
    return c


def main():
    make_certs()
    passwd = make_password_file()

    broker_thread = threading.Thread(target=lambda: asyncio.run(run_broker(passwd)), daemon=True)
    broker_thread.start()
    time.sleep(1.5)

    pub = make_paho_client("emulator-pub")
    printer = Printer(pub)
    sub = make_paho_client("emulator-sub", printer.handle, subscribe_topic=f"device/{SERIAL}/request")
    print(f"Emulated P2S {SERIAL} on 127.0.0.1:{PORT}, access code {CODE}", flush=True)

    def stop(*_):
        sys.exit(0)

    signal.signal(signal.SIGINT, stop)
    signal.signal(signal.SIGTERM, stop)

    while True:
        time.sleep(1)
        printer.tick()


if __name__ == "__main__":
    main()
