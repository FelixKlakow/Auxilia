# Security Policy

Auxilia moves credentials, runs AI-driven workflows in containers, and brokers
access to real repositories and services — security reports are taken
seriously and handled with priority.

## Reporting a vulnerability

**Please do not open a public issue for security problems.**

Use GitHub's private vulnerability reporting: **Security → Report a
vulnerability** on this repository. The report stays private between you and
the maintainer while it is investigated and fixed.

Include what you can: affected component (Core.Api, Core.Runner, a client
library, a workflow image), reproduction steps, and impact as you understand
it. You will get an initial response within a few days.

## Scope

The platform services, the client libraries, the bundled workflow images, and
the credential/trust model (just-in-time slot credentials, egress policy,
signed workflow packages) are all in scope. Vulnerabilities in third-party
dependencies are best reported upstream, but a heads-up here is welcome when
Auxilia's usage amplifies the impact.
