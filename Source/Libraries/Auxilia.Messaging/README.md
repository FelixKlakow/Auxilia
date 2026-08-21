# Auxilia.Messaging

The [Auxilia](https://github.com/FelixKlakow/Auxilia) message-bus abstraction: every
Auxilia component talks to the broker through `IMessageBusClient` — queues, topic
exchanges, typed publish/subscribe — with the RabbitMQ implementation and an injectable
fake for tests. Referenced by the workflow SDK (`Auxilia.Workflows`); pure Core clients
never need it (they use the Core's filtered event streams instead).

## License

Business Source License 1.1 — free for personal production use; commercial use requires a
license. See the packaged LICENSE file and the repository for terms.
