# Auxilia.Slots.Email

Slot provider `email-work-items` for the `work-items` slot (category `task-source`): backs an
`IWorkItemAccess` with an IMAP/SMTP mailbox via `Auxilia.Adapters.Email` — the triggering mail
is the work item and comments become mail replies.

Builds `Auxilia.Slots.Email.slothandler.dll` with its `*.slothandler.manifest.json` sidecar —
the naming contract of `FileSystemPluginDiscovery`. Manifest settings: IMAP/SMTP host + port,
`Username`, `Password` (a `Secret` with `ConnectFlow: google-gmail`), `UseSsl`, `Folder`.

## Special Rules

- Work-item id/title/from/body come from `WORKFLOW_CONTEXT__*` launch-context env vars, NOT a
  live IMAP fetch; only `GetAttachmentsAsync` re-fetches the mail, by `WORKFLOW_CONTEXT__MAILUID`.
- Replies carry no `In-Reply-To` (no Message-Id in the launch context); a `[work-item: <id>]`
  body marker keeps the thread traceable. `PostCommentAsync` throws if `…__FROM` is unset.
- `MailboxClientFactory` is an internal test seam (`InternalsVisibleTo` `Auxilia.Slots.Email.Tests`);
  the default client is `MailKitMailboxClient`.

## File / Folder Map
```
Source/Slots/Auxilia.Slots.Email/
├── EmailWorkItemsSlotHandler.cs                    # ISlotHandler; maps slot settings → mailbox-backed IWorkItemAccess
├── EmailWorkItemAccess.cs                          # IWorkItemAccess over the mailbox (env-var context; SMTP replies)
└── Auxilia.Slots.Email.slothandler.manifest.json   # provider descriptor + IMAP/SMTP settings schema
```
