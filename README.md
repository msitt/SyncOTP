# SyncOTP

macOS quietly reaches over to a paired iPhone and fills in your SMS two-factor code for you.
Windows just sits there. SyncOTP closes that gap: your iPhone forwards incoming texts to a ntfy
topic, a tray app on the PC watches that topic, pulls the code out of whatever arrives, drops it on
your clipboard, and tells you it's ready, so by the time you've switched windows to paste, it's
already there.

Any ntfy server works: the public one at `ntfy.sh`, or your own.

## Releases

Prebuilt binaries are on the [Releases page](../../releases). Each release has two downloads:

- **`SyncOTP-<version>-win-x64-selfcontained.zip`** — bundles the .NET runtime. Larger download,
  nothing to install first, just unzip and run `SyncOTP.exe`.
- **`SyncOTP-<version>-win-x64-framework-dependent.zip`** — small download, but needs the
  [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) installed already.

Either way, unzip somewhere permanent (e.g. `%LOCALAPPDATA%\Programs\SyncOTP`) and run the exe.
Updating is unzipping a newer release over the old one; config and logs live elsewhere and are
untouched (see [Where things live](#where-things-live)).

If you'd rather build from source, or are developing on the app, use `scripts/install.ps1` below.

## Setup

1. **Pick a topic.** On the public server, a topic is created by publishing to it and the name is
   the only thing keeping other people out, so generate a long random one rather than choosing
   something memorable:

   ```powershell
   'syncotp-' + [Guid]::NewGuid().ToString('N')
   ```

   Treat it as a password. Anyone who knows it can read every message you forward. If you would
   rather have real access control, run your own server or use a reserved topic on an ntfy paid
   plan, see [docs/ntfy-server.md](docs/ntfy-server.md), which covers both credentials and
   self-hosting.
2. **PC.** Either grab a prebuilt binary from the [Releases page](../../releases) (see
   [Releases](#releases) above), or build and install from source. Building compiles from source,
   so it needs the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), installing the
   runtime alone is not enough:

   ```powershell
   .\scripts\install.ps1 -Startup
   ```

   The app creates `%APPDATA%\SyncOTP\config.json` on first run and opens it. Fill in `ntfy.topic`
   (and `ntfy.server` if you are not using `https://ntfy.sh`, plus `ntfy.username` and
   `ntfy.password` if your topic requires auth), then choose **Reload config** from the tray menu.
3. **iPhone.** Create the forwarding automation. See
   [docs/iphone-shortcut.md](docs/iphone-shortcut.md). If you configured credentials, the tray
   menu's **Copy Shortcut auth header** gives you the Authorization value to paste in; without
   them the Shortcut is just a POST to the topic URL.

Confirm it works with **Send test message** in the tray menu, which publishes a synthetic code
through the real server.

## How it behaves

**Clipboard.** A code lands on the clipboard the moment it arrives, so you can paste straight into
the login form. Two minutes later it is wiped, unless you have already copied something else, in
which case it is left alone. Codes are flagged to stay out of Windows clipboard history (`Win+V`)
and cloud clipboard, so they do not linger where you would not expect them.

**Notification.** A toast shows the code, who it came from, and a **Copy again** button. If toast
registration is unavailable the app falls back to a tray balloon. The last five codes stay in the
tray menu under **Recent codes**, which is the escape hatch when the clipboard gets clobbered.

**Stale codes.** After a reconnect, ntfy replays whatever it cached. Anything older than three
minutes goes into Recent codes but never touches the clipboard, since silently pasting an expired
code is worse than doing nothing.

**Duplicates.** The same message is ignored if its id repeats, or if identical text arrives twice
within 90 seconds. Beyond that window identical text is treated as a genuinely new code, which is
what happens when you request one again.

## Code detection

The extractor masks the things that are never codes first (phone numbers, prices, times, durations,
dates, order numbers, URLs, carrier boilerplate), then scores what is left by pattern quality and by
how close a verification keyword sits. It recognises the common shapes: `G-123456`, `123-456`, bare
4 to 8 digit runs, and alphanumeric codes when a keyword backs them up.

Confidence comes out as High, Medium, or Low. Low means no verification keyword anywhere, just a
lone number in a short message. Those are still copied, and the notification says "probable code".
Set `extractor.acceptLowConfidence` to `false` to skip them.

The whole extractor lives in [`CodeExtractor.cs`](src/SyncOTP.Core/CodeExtractor.cs) with no
dependencies, and is covered by a corpus of real message shapes in
[`CodeExtractorTests.cs`](tests/SyncOTP.Core.Tests/CodeExtractorTests.cs). If a message from some
service is missed, add it to the corpus and adjust from there.

## Configuration

`%APPDATA%\SyncOTP\config.json`:

```json
{
  "ntfy": {
    "server": "https://ntfy.sh",
    "topic": "",
    "username": "",
    "password": "",
    "lastId": ""
  },
  "clipboard": {
    "autoClearSeconds": 120,
    "staleAfterSeconds": 180
  },
  "notifications": {
    "toast": true,
    "sound": false
  },
  "extractor": {
    "acceptLowConfidence": true
  },
  "verboseLogging": false,
  "messageSnippetChars": 120
}
```

Leave `username` and `password` empty for an unauthenticated topic; if either is set, SyncOTP sends
HTTP Basic auth on every request. `autoClearSeconds: 0` disables the wipe. `lastId` is the resume
cursor and is maintained by the app.
`verboseLogging` writes whole message bodies to the log, which means the codes themselves, so leave
it off unless you are debugging a message that was not detected.

`messageSnippetChars` is how much of the message text is shown in the log line and the toast. The
sender an SMS gateway reports is usually an anonymous five-digit short code, so the body is what
tells you which service the code is for. Set it to `0` to leave the text out.

## Where things live

| | |
| --- | --- |
| Executable | `%LOCALAPPDATA%\Programs\SyncOTP\SyncOTP.exe` |
| Config | `%APPDATA%\SyncOTP\config.json` |
| Logs | `%LOCALAPPDATA%\SyncOTP\logs\` |
| Startup entry | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\SyncOTP` |

Re-running `install.ps1` is the update path and leaves config and logs alone.

## Security

Every message you receive is forwarded, so the codes and the message bodies pass through the ntfy
server and sit in its cache for a while, 12 hours on `ntfy.sh`, whatever you configure on your own
server.

On a public topic there is no access control: the topic name is the whole secret, so use a random
one and do not paste it anywhere. On an authenticated topic the Basic auth header is reversible to
the password, so treat it as the password wherever it is stored.

Either way, the ntfy operator can see the messages. If that matters, self-host. To narrow the
exposure regardless, use per-keyword automations on the phone instead of the catch-all filter, so
only likely 2FA messages ever leave the device.

## Development

```powershell
dotnet build
dotnet test
dotnet run --project src\SyncOTP.App
```

- `src/SyncOTP.Core`, extraction, dedupe, config, logging. No UI, fully testable.
- `src/SyncOTP.App`, WinForms tray shell, ntfy client, clipboard and notifications.
- Message transports sit behind `IMessageSource`, so a second one (a direct listener on the LAN,
  say) is a new class plus a config block rather than a change to the pipeline.

## Requirements

- Windows 10 build 19041 or later
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) to install or update, since
  `install.ps1` runs `dotnet publish` against this repo. It is also what you need for development.
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) to run the installed
  app (the build is framework-dependent). The SDK includes it, so a machine that built the app
  already has it.
