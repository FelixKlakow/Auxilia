# Auxilia.Slots.Email.Tests

Unit tests for `Auxilia.Slots.Email` — the email work-items slot (`EmailWorkItemsSlotHandler` registration and `EmailWorkItemAccess` behaviour).

## Special Rules
`[NonParallelizable]`: `EmailWorkItemAccess` reads the current work item from `WORKFLOW_CONTEXT__*` process environment variables, so tests set them and clear them again in `TearDown`. The mailbox is faked by assigning `_handler.MailboxClientFactory`, which also captures the mapped `EmailTaskSourceSettings`; registration uses a real `ServiceCollection`.

## File / Folder Map
```
Auxilia.Slots.Email.Tests/
└── UnitTests/
    └── EmailWorkItemsSlotHandlerTests.cs   # slot registration → IWorkItemAccess; slot-settings → EmailTaskSourceSettings mapping + defaults; PostCommentAsync replies to original sender; GetWorkItem(s)Async assembled from env context
```
