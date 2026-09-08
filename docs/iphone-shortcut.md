# iPhone setup

One Shortcuts automation forwards every incoming text message to your ntfy topic. All the code
detection happens on the PC, so the automation itself stays trivial and never needs changing.

## Before you start

Have your topic URL to hand, `https://ntfy.sh/<your-topic>` on the public server, or the equivalent
on your own. See [ntfy-server.md](ntfy-server.md) if you have not picked one yet.

If your topic needs credentials, also get the Authorization header value from the PC: in the SyncOTP
tray menu choose **Copy Shortcut auth header**, then send it to yourself somewhere you can paste
from on the phone. It looks like:

```
Basic c3luYy1vdHA6eW91cnBhc3N3b3Jk
```

On an unauthenticated topic there is no header, and step 9 below is one line shorter.

## Creating the automation

1. Shortcuts app, **Automation** tab, **+**.
2. Choose **Message**.
3. **Sender:** leave as Any.
4. **Message contains:** type a single space.
5. **Run Immediately**, and turn **Notify When Run** off.
6. **Next**, then **New Blank Automation**.
7. Add the action **Get Contents of URL**.
8. URL: your topic URL, e.g. `https://ntfy.sh/syncotp-3f9c1a...`
9. Tap the arrow to expand the action:
   - **Method:** POST
   - **Headers:** if the topic needs auth, add `Authorization` with the value you copied above.
     Optionally add `Title` and set it to the **Sender** variable, which makes the PC notification
     say who the code came from.
   - **Request Body:** Text (or File), and set the content to the **Shortcut Input** variable.
10. Done.

## About the "Message contains" filter

Shortcuts requires either a sender or a content filter, so a true catch-all is not offered. A single
space is the closest thing available, and it matches essentially every real SMS, since a message
with no space at all is rare and a code-only message like `123456` is uncommon in practice.

**Verify this on your device before relying on it.** Have someone text you, and confirm the message
arrives on the PC.

If the space filter does not match reliably, fall back to several automations, one per keyword. Each
one is identical apart from the trigger text:

`code`, `verification`, `verify`, `OTP`, `passcode`, `security`, `login`, `one-time`

That covers most senders. It is less complete than the space filter, but each automation is a
30-second duplicate of the first.

## Things to check on the device

- **Does it run while locked?** Automations set to Run Immediately generally do, but confirm it,
  since a 2FA code usually arrives while you are mid-login on the PC.
- **Is the sender available?** If the `Title` header comes through empty, drop the header. The code
  still works, the PC notification just says "unknown" instead of the sender.
- **Low Power Mode** can delay or skip background automations.
- **Focus modes** do not block automations, but do check if messages seem to stop arriving.

## Testing

With the PC app running, use its tray menu item **Send test message**. That publishes a synthetic
message to the same topic, so it verifies the PC end without involving the phone.

To test the phone end, text yourself something like `Your code is 123456` from another device and
watch the PC.

## Privacy note

This forwards the full text of every message you receive, not just the ones containing codes. They
pass through the ntfy server and sit in its cache for a while, 12 hours on `ntfy.sh`, whatever you
configured on your own server. On a public topic, anyone who knows the topic name can read them. If
that is more than you want, use the keyword automations above instead of the space filter so only
likely 2FA messages leave the phone, and consider self-hosting.
