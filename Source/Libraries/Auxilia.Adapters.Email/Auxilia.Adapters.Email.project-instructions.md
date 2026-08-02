# Auxilia.Adapters.Email

The v1 task-source integration adapter (ARCHITECTURE §4, docs/delivered/goal-v1.md): a generic IMAP
mailbox adapter that turns incoming mail into work items and dispatches the configured
workflow per message. Gmail is a configuration of this adapter (IMAP + app password), not a
separate implementation.

## Architecture

- `EmailTaskSourceAdapter` (hosted by the Backend Service) polls the mailbox for unseen
  messages on an interval. Each message becomes one dispatch: `RunWorkflowCommand` with
  context `WorkItemId` (stable hash of the Message-Id), `Title` (subject), `From`, `Body`,
  carrying the configured run-as principal so dispatches pass the same policy checks as
  manual triggers. Processed messages are marked seen — the IMAP \Seen flag is the
  idempotency guard, matching the platform's at-least-once + idempotent-writes contract.
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
