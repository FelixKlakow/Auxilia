# Auxilia.Adapters.Email

The v1 task-source integration adapter (ARCHITECTURE §4, docs/delivered/goal-v1.md): a generic IMAP
mailbox adapter that turns incoming mail into work items and dispatches the configured
workflow per message. Gmail is a configuration of this adapter (IMAP + app password), not a
separate implementation.

## Architecture

- `EmailTaskSourceAdapter` (hosted by `Auxilia.TriggerHost`) polls each MAILBOX (slot
  instance) that an enabled trigger references — once per poll, at the pace of its most
  frequent trigger — and evaluates EVERY enabled trigger of that mailbox against each unseen
  message: each matching trigger dispatches its configuration (context `WorkItemId` = stable
  hash of the Message-Id, `Title`, `From`, `Body`, `MailUid`) on behalf of its run-as
  principal, so dispatches pass the same policy checks as manual triggers. A message is
  marked \Seen only after all of its triggers were evaluated and every matching dispatch was
  accepted (a mail no trigger wants is marked seen without a dispatch) — one trigger's filter
  never steals mail from another on the same mailbox. The IMAP \Seen flag is the idempotency
  guard, matching the platform's at-least-once + idempotent-writes contract; health is still
  written per trigger. Only the host's own token stops the adapter — a slow Core (unary
  `TimeoutException`) is a per-mailbox health error and retries next interval.
- `IMailboxClient` isolates the protocol: `MailKitMailboxClient` is the real IMAP
  implementation; unit tests fake the interface; system tests run the real client against a
  containerized GreenMail server (real protocol, no cost — the actual-Gmail path is manual
  pre-release verification only, per TestStrategy).
- Mailbox credentials come from configuration today; they move to an Account Bundle when
  bundle CRUD lands with the dashboard.

## File / Folder Map
```
Source/Libraries/Auxilia.Adapters.Email/
├── EmailTaskSourceSettings.cs   # Host/port/SSL, credentials, folder, poll interval, target workflow
├── IMailboxClient.cs            # FetchUnseen / MarkSeen / SendReply abstraction + InboundMail record
├── MailKitMailboxClient.cs      # Real IMAP (+SMTP reply) implementation
└── EmailTaskSourceAdapter.cs    # Polling BackgroundService: mail → RunWorkflowCommand + audit
```

## Special rules
- Never log message bodies or credentials; audit records reference messages by their stable work-item id.
