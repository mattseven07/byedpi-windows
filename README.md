# ByeDPI for Windows

A Windows port of [ByeDPI for Android](https://github.com/dovecoteescapee/ByeDPIAndroid) 1.2.0.
It runs the same DPI-bypass engine ([byedpi](https://github.com/hufrea/byedpi) v0.13, the
version inside `byedpi-1.2.0.apk`) as a local SOCKS proxy, and either routes the whole PC
through it (**VPN mode**) or points the Windows proxy settings at it (**Proxy mode**).

No installation is needed. Windows 10/11 x64 is required, along with .NET Framework 4.8, which ships with Windows.

This is an **unofficial** port. It is not affiliated with the authors of ByeDPIAndroid or byedpi.

Download `ByeDPI-windows-x64.zip` from the **Releases** page.

## Quick start

1. Unzip `ByeDPI-windows-x64.zip` anywhere and run `ByeDPI.exe`.
2. Open **Settings** (gear icon):
   - **Mode**: `VPN` (whole PC, recommended for Discord) or `Proxy`.
   - **DNS**: used in VPN mode. In Turkey the ISP resolver answers blocked names with
     `195.175.254.2`, so use an independent resolver such as `94.140.15.15` (AdGuard).
   - **Use command line settings** → **Command line editor** → enter byedpi arguments,
     or leave it off and use the **UI editor** (same options as the Android app).
3. Press **Connect**. VPN mode asks for administrator rights (the Windows equivalent of
   Android's VPN permission prompt); ByeDPI restarts elevated and connects.

Closing the window while connected keeps ByeDPI running in the tray. Use the tray icon or
**⋮ → Exit** to stop it. **⋮ → Save logs** writes the app, byedpi and tunnel logs to a file.

`ByeDPI.exe --connect` connects on launch, and `--tray` starts hidden in the tray.

## Command line strategies on Windows

The arguments are the same as on Android (byedpi v0.13,
[documentation](https://github.com/hufrea/byedpi/blob/v0.13/README.md)), with one important difference:

**Fake packets (`-f`, `--fake`) do not work reliably on Windows.** byedpi implements them
on Windows with a `TransmitFile` retransmission trick that current Windows 11 builds don't
honour, so connections stall. This also affects upstream's official Windows builds. A strategy
such as `-s1 -At -d2 -At -f-1 -r1+s -An` that works on Android will hang on Windows at the `-f` step.

Strategies verified on Türk Telekom (Discord, gateway.discord.gg, Roblox and Wattpad all load):

| Preset | Arguments |
|---|---|
| Split + TLS record split | `-s1 -r1+s` |
| TLS record split at SNI | `-r1+s` |
| Disorder + TLS record split | `-d1 -r1+s` |
| Disorder at SNI + TLS record split | `-d1+s -r1+s` |

**Avoid out-of-band strategies (`-o`, `-q`).** They pass Cloudflare-hosted sites such as discord.com,
but Google-hosted servers silently drop out-of-band data. Those include the Discord app's update
server (`stable.dl2.discordapp.net`), so the app hangs at "Downloading update" or "Checking for updates".

They are available under **⋮** in the command line editor. Results depend on the ISP, so if
one stops working, try the next, or let ByeDPI find one.

### Find a working strategy

**Settings → Command line editor → Find a working strategy** tests a list of strategies
against a list of sites and ranks the results. Selecting a result saves it as your command line.

- Each strategy runs in its own temporary ciadpi instance. ByeDPI resolves each site with
  the **DNS** setting (so ISP block pages don't skew the result), does a real TLS handshake
  with the site's name as SNI (the part DPI inspects), checks the certificate, and sends
  one HTTP request.
- The default sites include the Discord app's update server, so strategies that break Discord
  updates don't rank as working.
- **Without bypass** is tested too, so you can see which sites are actually blocked on your network.
- Failures are labelled `blocked` (DPI cut the handshake), `timeout` (silently dropped),
  `bad certificate`, and so on.
- **Sites** and **Strategies** are editable, one per line. **⋮ → Restore default sites and
  strategies** resets them. Your current command line is always tested as well.
- A run takes 10–60 seconds. Stop ByeDPI before testing (settings are only available
  while it is stopped), so the results reflect your real connection.

## Modes

**VPN mode** (needs administrator rights) works like the Android VPN mode. A virtual network
adapter ([Wintun](https://www.wintun.net/), driven by
[hev-socks5-tunnel](https://github.com/heiher/hev-socks5-tunnel), the same tunnel used on
Android) captures all IPv4 traffic of every app, including Discord voice (UDP), and sends
it through byedpi. While connected, ByeDPI:

- creates the `ByeDPI` adapter (198.18.0.1) with routes `0.0.0.0/1` and `128.0.0.0/1`
- sets the chosen DNS server on it and adds an NRPT rule (`.` → DNS server) so Windows
  doesn't also ask the router's resolver
- adds a host route to the DNS server through your router (Windows uses a new UDP port for
  every lookup, and tunnelling each one separately would exhaust the proxy)
- adds a Windows Firewall rule that blocks LAN discovery traffic (SSDP, LLMNR, NetBIOS,
  broadcasts) from the tunnel address; Windows floods every new adapter with it
- pins byedpi's own connections to your real network adapter so they don't loop back into the tunnel

Everything is removed on disconnect. The adapter and its routes disappear even if ByeDPI
is killed. The NRPT rule, DNS route and firewall rule are removed the next time ByeDPI
starts with administrator rights.

**Proxy mode** (no administrator rights needed) starts byedpi on `127.0.0.1:1080` and, if
**Set as system proxy** is on, points the Windows proxy setting at it (`socks=127.0.0.1:1080`).
Edge, Chrome, Opera, the Discord app and other programs that follow the Windows proxy
settings use it. Your previous proxy settings are restored on stop, and also after a crash.
In this mode name resolution uses the system DNS, so set an independent DNS server in
Windows or in the browser (secure DNS) if your ISP's DNS is censored.

## Differences from the Android app

| Android | Windows |
|---|---|
| VPN permission dialog | UAC prompt, ByeDPI restarts elevated |
| Notification / quick tile | Tray icon |
| byedpi listens on `0.0.0.0` in command line mode | Defaults to `127.0.0.1` unless `-i` is given, so the proxy is not exposed to the LAN |
| — | VPN mode raises byedpi's connection limit to 2048 unless `-c` is given (a PC keeps far more connections open than a phone) |
| TCP Fast Open, Drop SACK | Linux-only in byedpi, shown as unavailable |
| Fake packets work | Unreliable on Windows (see above) |
| IPv6 switch | Same; IPv6 is tunnelled only if the network actually has IPv6 |

Settings are stored in `%APPDATA%\ByeDPI\settings.ini` and use the same keys as the Android
app. **⋮ → Reset** on the Settings screen restores the defaults.

## Building from source

```
.\build.ps1 -Zig C:\path\to\zig.exe
```

- `core\byedpi` contains byedpi v0.13 (commit `078842b`, the version bundled in the APK) with
  Windows patches; `core\patches\byedpi-v0.13-windows.patch` shows every change against upstream:
  - backport of upstream's Windows `--fake` fix (2e244fd, v0.13.1) and temp-file hotfix (41ffee5)
  - backport of the UDP-associate NULL-pointer crash fix (663cbf8) and "pool is full" logging
  - new Windows-only `--out-if <index>` option: pins outgoing sockets to an interface
    (`IP_UNICAST_IF`), using plain IPv4 sockets for IPv4 because Windows does not pin
    IPv4 traffic on dual-stack sockets
  - larger listen backlog, and reset/aborted `accept()` no longer stops the server
- `ciadpi.exe` is built with [Zig](https://ziglang.org/) 0.17 (`zig cc`, MinGW target).
- `ByeDPI.exe` (`src\ByeDPI`) is C# 5 / WinForms, compiled with the `csc.exe` that ships
  with .NET Framework 4.8, so no SDK is needed.
- `third_party\hev-socks5-tunnel` is the official hev-socks5-tunnel 2.18.0 `win64` release
  (with `msys-2.0.dll` and the WireGuard-signed `wintun.dll`).
- `tools\` contains the icon converter and test harnesses (UI snapshots, service tests,
  headless strategy tester).

## Licenses

The Windows port follows ByeDPIAndroid's GPL-3.0 (`LICENSE`). Bundled components:
byedpi (MIT), hev-socks5-tunnel (MIT), Wintun (prebuilt binaries license), msys2 runtime
(LGPL-3.0). See `licenses\`.

Source of the bundled third-party binaries:
- `hev-socks5-tunnel.exe`: [heiher/hev-socks5-tunnel 2.18.0](https://github.com/heiher/hev-socks5-tunnel/tree/2.18.0)
- `msys-2.0.dll` (3.6.9): [msys2/msys2-runtime @ aa532e7](https://github.com/msys2/msys2-runtime/tree/aa532e7bbf59a3ec1e29e13c9e8515d545cb2e16)
- `wintun.dll`: [wintun.net](https://www.wintun.net/), signed by WireGuard LLC
