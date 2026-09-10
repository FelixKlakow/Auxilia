# Auxilia.Messaging.Tests

Unit tests for `Auxilia.Messaging` — the parts of `RabbitMqClient` that are testable without a
broker: the subscription handles against Moq'd `IChannel`/`IConnection`.

## Special Rules
- No broker here. Everything that needs RabbitMQ semantics (publisher confirmations, delivery
  ack/nack, topic routing, connection recovery) lives in the Docker system suite under
  `Tests/System/Auxilia.SystemTestSuite/Messaging/` — do not fake a broker to test it.
- The handles are `internal` (InternalsVisibleTo) precisely so their dispose ordering can be
  pinned: every channel is released even when the courtesy consumer cancel throws.

## File / Folder Map
```
Tests/Libraries/Auxilia.Messaging.Tests/
└── UnitTests/
    └── SubscriptionHandleDisposeTests.cs   # SubscriptionHandle / TopicSubscriptionHandle dispose: channel + bind channel released when BasicCancel / channel dispose throws
```
