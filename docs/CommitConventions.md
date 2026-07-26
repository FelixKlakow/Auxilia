# Commit Conventions

This project follows [Conventional Commits 1.0.0](https://www.conventionalcommits.org/en/v1.0.0/).

## Format

```
<type>(optional scope)!: <description>

[optional body]

[optional footer(s)]
```

The `!` suffix denotes a **breaking change**.

## Allowed Types

| Type       | When to use                                                                      |
|------------|----------------------------------------------------------------------------------|
| `feat`     | Implementing a new feature or user-visible behaviour                             |
| `fix`      | Repairing broken functionality                                                   |
| `refactor` | Changing internal architecture without altering external behaviour               |
| `plan`     | Adding or updating an implementation plan (`.prompt.md`) for a user story or bug |
| `docs`     | Documenting existing functionality (READMEs, architecture docs, comments)        |
| `style`    | Formatting, whitespace, naming — no logic change                                 |
| `merge`    | Merging a pull request (created by the merge tooling)                            |
| `revert`   | Reverting a previous commit                                                      |

## Scope (optional)

Use the component name in parentheses to narrow the context:

```
feat(messaging): add SubscribeAsync overload with error handler
fix(backend-service): prevent duplicate queue declaration on reconnect
```

## Breaking Changes

Append `!` to the type, and include a `BREAKING CHANGE:` footer:

```
refactor!: rename IMessageBusClient.PublishAsync signature

BREAKING CHANGE: second parameter is now the topic, not the message.
```

## Ticket References

When working on a branch named `feature/12345/some-description` (or any branch containing a
4+-digit ticket number), the `commit-msg` hook **automatically appends** a `Refs:` footer:

```
feat: add identification request handler

Refs: #12345
```

You never need to add this manually — the hook does it for you.

## Examples

```
feat: create QueueInitializer hosted service
fix(messaging): handle connection reset during BasicPublish
refactor(backend-service): extract ServiceInfo into its own class
docs: document RabbitMQ configuration options
plan: add implementation plan for core-runner queue setup
style: apply EditorConfig formatting to Messaging project
merge: PR #42 – feature/1001/queue-initializer into main
revert: revert "feat: experimental AMQP 1.0 support"
```
