# Commercial License for Auxilia

Auxilia is distributed under the **Business Source License 1.1** (see `LICENSE`).
That license permits **non-production use** free of charge — evaluation,
testing, development, CI, proof-of-concept work, teaching, and personal
experimentation.

**Any production use requires a commercial license.**

## What counts as production use?

Production use means running Auxilia — or a derivative of it — as part of
your normal business operations, or in any environment that serves real
users, real data, or real workloads. This includes internal tools used by
your staff, not just customer-facing systems.

**Clients count.** Auxilia is a client-server platform, and the clients are
part of the Licensed Work: the client libraries (`Auxilia.Core.Contracts`,
`Auxilia.Core.Client`, `Auxilia.Workflows.Client`), the Admin Console, the
Trigger Host, and the MCP surface. Using an Auxilia deployment productively
**through any of these — or through applications built on them — is
production use of Auxilia**, regardless of which machine the server process
runs on. A hundred people working productively against one Auxilia server is
a hundred people using Auxilia in production; it is covered by (and priced
through) the license of the legal entity those people work for — not
side-stepped by pointing at a single licensed server box.

**One license per using entity.** A commercial license covers production use
by and for **the licensed legal entity** (its employees and contractors
working on its behalf, at any scale — that is what the company-size bands
price). It does **not** extend to other legal entities: if you operate an
Auxilia deployment that other companies use productively (hosting, a managed
service, a shared platform for your corporate group's separate entities),
each using entity needs its own license, or you need a service-provider
agreement — talk to me.

If you are unsure which side of the line your use falls on, please just ask.

## What a commercial license gives you

The right to use Auxilia in production, without the restrictions of the
BUSL. The license covers usage rights only; support agreements can be
discussed separately.

## Pricing

A flat rate per legal entity and year, banded by company size:

| Company size (employees) | Per year |
|---|---|
| individuals (personal use) | **free** |
| up to 10 | EUR 1,200 |
| 11 – 50 | EUR 5,000 |
| 51 – 250 | EUR 15,000 |
| 251 – 1,000 | EUR 35,000 |
| above 1,000 | contact for a quote |

**Personal use is free**, including production: a natural person using
Auxilia for themselves — their own projects, their own agents, their own
infrastructure — needs no commercial license and no paperwork. The line is
drawn at working on behalf of a legal entity: the moment Auxilia runs for a
company's benefit (including a one-person company's client work), the
company bands apply.

Non-profits and academic institutions: please get in touch, there is room
to talk.

## How to obtain one

Open an issue or discussion on the Auxilia repository on GitHub with:

1. The name and country of the legal entity that will use Auxilia
2. A short description of the intended use
3. Approximate scale (users, instances, or whatever fits your case)

You will receive a written license agreement. Use in production begins once
that agreement is signed.

## The Change Date

On **2030-07-28**, this version of Auxilia becomes available under the
**Apache License 2.0** and no commercial license is required for it any more.
Later versions carry their own, later Change Date.
