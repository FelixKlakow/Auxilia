# Auxilia.SystemTestSuite

Platform-wide, end-to-end system tests (all fixtures carry `[Category("System")]`). Each scenario runs against **real infrastructure in Docker via Testcontainers** — MongoDB, RabbitMQ, mail (GreenMail), LDAP, git servers, and the platform's own images rebuilt from local source before containers start. Only cost-generating third-party calls (Azure DevOps, AI providers) are stubbed. Every scenario lives in its own namespace with a namespace-scoped `[SetUpFixture]` `*Environment` class that builds images, creates a Docker network, starts the containers, and exposes a live `IMessageBusClient` (and other handles) to the test classes sharing that namespace.

## File / Folder Map
```
Auxilia.SystemTestSuite/
├── MongoDb/                                 # MongoDB container: CRUD round-trips via MongoDbEfDataAccess
│   ├── MongoDbEnvironment.cs
│   └── MongoDbSystemTests.cs
├── Messaging/                               # Real-RabbitMQ fanout routing (reuses another namespace's environment)
│   └── MessageBusFanoutSystemTests.cs
├── WorkflowDispatch/                        # RabbitMQ + runner: workflow run-command dispatch smoke test (no Mongo)
│   ├── WorkflowDispatchEnvironment.cs
│   └── WorkflowDispatchSystemTests.cs
├── CoreApiDispatch/                         # Core.Api → runner dispatch, JIT credential resolution, per-run repository workspace
│   ├── CoreApiDispatchEnvironment.cs
│   ├── CoreApiDispatchSystemTests.cs
│   ├── CredentialResolutionSystemTests.cs
│   └── RepositoryWorkspaceSystemTests.cs
├── CodeReviewWorkflow/                      # Baked code-review workflow image driven over the bus (happy + edge instances)
│   ├── CodeReviewWorkflowEnvironment.cs
│   └── CodeReviewWorkflowSystemTests.cs
├── ImplementationWorkflow/                  # Baked implementation workflow image driven over the bus (happy + edge instances)
│   ├── ImplementationWorkflowEnvironment.cs
│   └── ImplementationWorkflowSystemTests.cs
├── EmailAdapter/                            # GreenMail (IMAP/SMTP): email task-source login + auto-created recipient accounts
│   ├── EmailAdapterEnvironment.cs
│   └── EmailAdapterSystemTests.cs
├── IdentityImport/                          # OpenLDAP + MongoDB: identity import (Governance runs in-process)
│   ├── IdentityImportEnvironment.cs
│   └── IdentityImportSystemTests.cs
├── Failover/                                # Two runners on one command queue + Core.Api heartbeat-driven failover (RunnerHeartbeat + FailoverMonitor); dispatch via the Core Run API
│   ├── FailoverEnvironment.cs
│   └── FailoverSystemTests.cs
├── EndToEnd/                                # goal-v1 whole-platform acceptance: GreenMail + RabbitMQ + Mongo + runner + Core.Api + WorkflowStudio; mail → Studio email adapter → Core Run API on-behalf-of → Code Review; plus the Claude Code workflow
│   ├── EndToEndEnvironment.cs
│   ├── EndToEndSystemTests.cs
│   └── ClaudeCodeWorkflowSystemTests.cs
└── GitServer/                               # Shared Dockerfile + httpd.conf for the authenticated git server used by repository scenarios
    ├── Dockerfile
    └── httpd.conf
```
