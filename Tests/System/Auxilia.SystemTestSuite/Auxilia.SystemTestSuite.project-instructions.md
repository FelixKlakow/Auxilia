# Auxilia.SystemTestSuite

Platform-wide, end-to-end system tests (all fixtures carry `[Category("System")]`). Each scenario runs against **real infrastructure in Docker via Testcontainers** — MongoDB, RabbitMQ, mail (GreenMail), LDAP, git servers, and the platform's own images rebuilt from local source before containers start. Only cost-generating third-party calls (Azure DevOps, AI providers) are stubbed. Every scenario lives in its own namespace with a namespace-scoped `[SetUpFixture]` `*Environment` class that builds images, creates a Docker network, starts the containers, and exposes a live `IMessageBusClient` (and other handles) to the test classes sharing that namespace.

## File / Folder Map
```
Tests/System/Auxilia.SystemTestSuite/
├── MongoDb/                                 # MongoDB container: CRUD round-trips via MongoDbEfDataAccess
│   ├── MongoDbEnvironment.cs
│   └── MongoDbSystemTests.cs
├── Messaging/                               # Real-RabbitMQ routing (reuses the WorkflowDispatch environment)
│   ├── MessageBusFanoutSystemTests.cs
│   └── MessageBusTopicRoutingSystemTests.cs
├── WorkflowDispatch/                        # RabbitMQ + runner: workflow run-command dispatch smoke test (no Mongo)
│   ├── WorkflowDispatchEnvironment.cs
│   └── WorkflowDispatchSystemTests.cs
├── CoreApiDispatch/                         # Core.Api → runner dispatch, JIT credential resolution, workspace mounts (per-run repository, empty workspace, setup scripts)
│   ├── CoreApiDispatchEnvironment.cs
│   ├── CoreApiDispatchSystemTests.cs
│   ├── CredentialResolutionSystemTests.cs
│   └── RepositoryWorkspaceSystemTests.cs
├── EmailAdapter/                            # GreenMail (IMAP/SMTP): email task-source login + auto-created recipient accounts
│   ├── EmailAdapterEnvironment.cs
│   └── EmailAdapterSystemTests.cs
├── IdentityImport/                          # OpenLDAP + MongoDB: identity import (Governance runs in-process)
│   ├── IdentityImportEnvironment.cs
│   └── IdentityImportSystemTests.cs
├── Failover/                                # Two runners on one command queue + Core.Api heartbeat-driven failover (RunnerHeartbeat + FailoverMonitor); dispatch via the Core Run API
│   ├── FailoverEnvironment.cs
│   └── FailoverSystemTests.cs
├── ReAdoption/                              # Runner restart mid-run: container re-adoption, real exit collection, clean-kill of unknown labeled containers
│   ├── ReAdoptionEnvironment.cs
│   └── ReAdoptionSystemTests.cs
├── DispatchTimeout/                         # Dispatched-but-never-claimed sweep (fixture owns its own setup — no *Environment class)
│   └── DispatchTimeoutSystemTests.cs
├── CoreClientSurface/                       # ICoreClient against the real Dockerized Core: SSE snapshot/keepalive/reconnect, run surface
│   ├── CoreClientEnvironment.cs
│   ├── CoreClientSurfaceSystemTests.cs
│   ├── CoreClientStreamSystemTests.cs
│   └── ClientStreamProbe.cs
├── EndToEnd/                                # Whole-platform acceptance: GreenMail + RabbitMQ + Mongo + runner + Core.Api + TriggerHost + AdminConsole; mail → email adapter → Core Run API on-behalf-of → Code Review; plus the Claude Code workflow (headless + console-mode terminal), the implementation workflow, and the email plugin-dependency proof
│   ├── EndToEndEnvironment.cs
│   ├── EndToEndSystemTests.cs
│   ├── ClaudeCodeWorkflowSystemTests.cs
│   ├── ConsoleSessionTerminalSystemTests.cs
│   ├── EmailPluginDependencySystemTests.cs
│   ├── ImplementationWorkflowSystemTests.cs
│   └── CodeReviewDispatchSystemTests.cs
└── GitServer/                               # Shared Dockerfile + httpd.conf for the authenticated git server used by repository scenarios
    ├── Dockerfile
    └── httpd.conf
```
