# ntfy setup

SyncOTP relays messages through an ntfy topic. Anything that speaks ntfy works, the public server
at `ntfy.sh`, someone else's hosted instance, or your own. Pick one of the three options below and
fill the result into `%APPDATA%\SyncOTP\config.json`.

| Config key | What goes in it |
| --- | --- |
| `ntfy.server` | Base URL, e.g. `https://ntfy.sh` or `https://ntfy.example.com` |
| `ntfy.topic` | Topic name, e.g. `syncotp-3f9c1a...` |
| `ntfy.username` / `ntfy.password` | Only if the topic requires auth; leave empty otherwise |

## Option 1: public ntfy.sh, no account

The quickest path, and the default. Topics on `ntfy.sh` are created by publishing to them and have
no access control, so the name is the only secret. Generate a random one:

```powershell
'syncotp-' + [Guid]::NewGuid().ToString('N')
```

Set `server` to `https://ntfy.sh` and `topic` to that string. Leave `username` and `password` empty;
SyncOTP then sends no `Authorization` header at all, and the Shortcut needs none either.

Understand the trade: anyone who learns the topic name can read every message you forward, and they
sit in the server's cache for 12 hours. Do not reuse the name anywhere, and consider the keyword
filters in [iphone-shortcut.md](iphone-shortcut.md) so only likely 2FA messages ever leave the
phone.

## Option 2: ntfy.sh with an account

A free account lets you authenticate, and a paid plan lets you *reserve* a topic, which is what
actually stops other people from reading it. Create the account at
[ntfy.sh/account](https://ntfy.sh/account), reserve the topic there, then put the same username and
password into `config.json`. SyncOTP switches to HTTP Basic auth as soon as either field is filled
in.

## Option 3: self-hosted

Reference deployment lives in [`server/ntfy`](../server/ntfy): a
[docker-compose.yml](../server/ntfy/docker-compose.yml) and a
[server.yml](../server/ntfy/server.yml) with the settings SyncOTP depends on. Set `base-url` to your
own hostname and put it behind TLS, a reverse proxy or a Cloudflare Tunnel both work.

Create a user with access to just this one topic:

```bash
# Inside the container, or with the ntfy CLI pointed at your server config.
ntfy user add sync-otp                     # prompts for the password
ntfy access sync-otp syncotp rw            # read + write on this one topic only
ntfy access                                # confirm: everything else stays denied
```

Keep the default access at `deny-all` if the server is reachable from the internet, so credentials
are actually required.

## The Authorization header

Skip this section on an unauthenticated topic.

ntfy accepts standard HTTP Basic auth, and the header value is the same string on both the phone and
the PC:

```
Authorization: Basic base64(username + ":" + password)
```

Shortcuts cannot compute base64 in a header field, so the value gets pasted in literally. SyncOTP
builds it for you once the credentials are in `config.json`:

- Tray menu, **Copy Shortcut auth header**. Copies the value and writes it to the log.
- Or from a terminal:

```powershell
& "$env:LOCALAPPDATA\Programs\SyncOTP\SyncOTP.exe" --print-auth-header
```

Treat that string as the password itself, because it is trivially reversible.

## Testing

Substitute your own server and topic throughout. Publish a message:

```bash
curl -d "Your Chase code is 482913" https://ntfy.sh/YOUR-TOPIC
```

With credentials, either form works, the header form is what the phone actually sends:

```bash
curl -u USER:PASSWORD -d "Your Chase code is 482913" https://ntfy.example.com/syncotp
curl -H "Authorization: Basic BASE64" -d "Your Chase code is 482913" https://ntfy.example.com/syncotp
```

Watch the stream the way SyncOTP does:

```bash
curl "https://ntfy.sh/YOUR-TOPIC/json?since=10m"
```

You should see an `open` event, then one `message` object per line, then a `keepalive` roughly every
45 seconds. If the keepalives are missing, the connection will not survive an idle proxy in front of
the server.

On an authenticated topic, verify auth is actually enforced. This must fail:

```bash
curl "https://ntfy.example.com/syncotp/json?poll=1"     # expect 403
```

## Long-lived connections

SyncOTP holds one subscription open indefinitely, and anything between it and the server that times
out idle connections will cut it. Cloudflare, for instance, closes a proxied connection after 100
seconds of inactivity; ntfy's `keepalive-interval` is the only thing holding it open, so keep it at
the default 45 seconds and well under 90, which is where SyncOTP's watchdog gives up and reconnects.

Expect the connection to be recycled from time to time regardless. That is not a failure: SyncOTP
reconnects with `?since=<last message id>` and picks up anything sent during the gap. Those
reconnects are logged at debug level only.

Do not put an interactive login (Cloudflare Access and friends) in front of the hostname. Shortcuts
cannot complete one, and a service token would be a second credential to manage for no gain over the
Basic auth already in place.

## What the server sees

Every text message the iPhone receives is forwarded, not just the ones with codes, because the
filtering happens on the PC. Codes and message bodies sit in the ntfy cache, 12 hours on `ntfy.sh`,
`cache-duration` on your own server. Shortening that reduces the exposure, at the cost of a smaller
window to recover from a PC that was offline.
