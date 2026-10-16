# Auxilia.Messaging

The [Auxilia](https://github.com/FelixKlakow/Auxilia) message-bus abstraction: every
Auxilia component talks to the broker through `IMessageBusClient` — queues, topic
exchanges, typed publish/subscribe — with the RabbitMQ implementation and an injectable
fake for tests. Publishes are broker-confirmed (a publish completes only once RabbitMQ
acknowledged it). Referenced by the workflow SDK (`Auxilia.Workflows`); pure Core clients
never need it (they use the Core's filtered event streams instead).

Every publish carries the broker-validated publisher identity (`user_id`). Consumers drop an
`IWorkloadScopedMessage` whose publisher is a workload identity other than the instance it
names, and any `IPlatformOnlyMessage` published by a workload or unattributed —
`WorkloadBusIdentity` defines the naming. `IBrokerIdentityIssuer`
(`RabbitMqManagementIdentityIssuer`) mints and revokes narrowly scoped per-workload broker
users; a client created with `queuePublishExchange` routes queue publishes through that
(ingress) exchange, the only route a workload identity has.

## License

Business Source License 1.1 — free for personal production use; commercial use requires a
license. See the packaged LICENSE file and the repository for terms.
