# Security Policy

## Reporting a vulnerability

If you discover a security issue (credential leakage, MITM vector, unsafe
deserialization, etc.), please **do not** open a public issue.

Instead:

1. Open a **private** draft issue via the "Security" tab → "Report a
   vulnerability" (enable it in Settings → Features if it isn't already), or
2. Email the maintainer directly (see repository owner profile).

Include:

- Affected component / file
- Steps to reproduce or PoC
- Severity estimate (CVSS if possible)

## Response time

I aim to acknowledge within **7 days** and patch within **30 days** for
confirmed issues.

## Scope

| In scope | Out of scope |
|----------|-------------|
| BambuBar.App / BambuBar.Core source | Bambu Lab firmware / cloud |
| Local credential handling | Other tools on the same LAN |
| TLS / MQTT protocol handling | Network-level attacks (ARP spoofing, etc.) |
