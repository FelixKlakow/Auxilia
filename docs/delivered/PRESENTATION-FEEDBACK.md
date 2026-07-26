# Auxilia — Pre-Presentation Feedback Sheet

> Fill in the **Feedback** blocks under each screen/element. All must be done today.

---

## 0. Global / cross-cutting

The left **sidebar** is the top-level menu. It has four groups:

- **(main)** — Dashboard · Workflows · Flows · Runs (with live-now badge) · My slots
- **Operator** — Slots
- **Administration** — Principals · Identity sources · Provider catalog · Audit
- **Footer** — signed-in user + Log out (or Login)

Cross-cutting things to judge: brand/logo, color scheme, typography, spacing, empty-states,
loading states, responsiveness, terminology consistency (e.g. "slot", "provider", "principal", "flow").

**Feedback (global):** 
- Menu is good

---

## 1. Dashboard  `/`

**Subtitle:** *"What is running right now, plus the views you pinned from workflow runs — updated live."*

Sections on the page:

| # | Section | What it shows / does |
|---|---------|----------------------|
| 1 | **Getting started** | Onboarding card (shown when little is configured) |
| 2 | **Needs attention** | Runs that failed / need action |
| 3 | **Live now** | Currently-running workflows; per-run **Cancel** button; empty-state "Nothing running right now" |
| 4 | **Recent runs** | Latest executions list |
| 5 | **Pinned views** | Views pinned from run detail; each has **Open run** + **Unpin**; empty-state "Nothing pinned yet" |

**Feedback (Dashboard):** 
-
-
-

---

## 2. Workflows  `/workflows`

**Subtitle:** *"Every configured workflow on this platform: what it does, where its work comes from, and how its runs are triggered."*

Each workflow card shows: **DisplayName** + type chip, **Slots**, **Trigger(s)**, **Last run**, and actions:
**Run**, **Edit**, **Open flow**, enable/disable toggle, **Delete** (with confirm).
Empty-state: "No workflows configured yet".

> ⚠️ Standing rule: a **Run** from here/Dashboard must always require an instruction input — never fire blank.

**Feedback (Workflows):** 
-
-

---

## 3. Workflow editor  `/workflows/new` · `/workflows/{id}/edit`

**Subtitle:** *"Three steps: pick a workflow, fill its capability slots with providers, and wire a trigger. No JSON required."*

Three-step wizard:

| Step | Title | Contents |
|------|-------|----------|
| 1 | **Basics** | Display name, pick workflow type |
| 2 | **Slots & providers** | One row per capability slot; **Bind** a provider, remove binding, **+ Add another slot**, create a **New reusable instance** inline |
| 3 | **Triggers** | Add trigger: **Schedule**, **Artifact chain**, **Mailbox**; remove trigger |

Footer: **Save**.

**Feedback (Workflow editor):**
- Triggers are determined by the Workflow only, a workflow can never be triggered unless the workflow itself supports beeing triggered by it. For workflow triggers we can have this view indeed.
- When no workflow is selected it doesn't make sense to configure anything, be it triggers or slots & providers.
- When a workflow can be triggered manually ( like the coding example ) i would wish we have the n8n like view on how to trigger it ( use a existing trigger or configure a new one, could be also a button ).
- For the claude-code-cli:
  - I would like to have a way to connect to antrophic over my account, this includes preassigned api keys by an administrator. Also adminstrators can disable the slot option to use a external api key and would only allow a signin with the preassigned.
  - Signin via claude webpage is also possible, then a account gets connected
  - Multiple accounts possible to select from ( company, personal )
  - API Key should be fallback, not default
  - Maximum turns (?), is this even supported? It doesn't make sense for us to add this, we might add more options later but at least for the presentation it doesn't make sense.
  - For "repository" we can bind multipole repositories which is fine, but inline configuration only makes sense when creating a new slot ( and configuring it ), when i created the slot i can only configure them outside then
  - New reusable instance is fine, but do we have scoped instances too?
  - We should have either New reusable instance or configure inline ( advanced ), but not both. I would prefer new instance and then i can select the type and continue configuring the ones i created there.
  - Claude CLI: I cannot configure the claude code itself, just the repository. When having a workflow which has a fixed purpose we might want to have the configuration not optional and always bound without slot
  - work-items can bind to multiple things which are not even are mailbox, i could bind workspaces too.


---

## 4. Flows  `/flows`

**Subtitle:** *"The whole pipeline at a glance: every workflow configuration and how artifacts chain them together. Open a workflow's own flow to wire new chains."*

Read-only overview of the whole pipeline. Empty-state: "No workflow configurations yet".

**Feedback (Flows):** 
-
-

---

## 5. Single workflow flow  `/workflows/{id}/flow`

**Title:** *"Flow of \<name\>"* — **Subtitle:** *"What starts runs, what they produce, and which workflows consume the outputs. Drag a workflow from the palette onto an output to chain it…"*

Interactive: drag from palette onto an output to chain; **Remove this chaining** button per consumer.

**Feedback (Workflow flow):** 
-
-

---

## 6. Runs  `/runs`

**Subtitle:** *"Every workflow execution on this platform — click a run to inspect its views."*

State filter chips (all/queued/running/…); per-run **Rerun** and **Cancel**; "Load more" paging.
Empty-states: "No workflow runs yet" / "No runs for this configuration yet".

**Feedback (Runs):** 
-
-

---

## 7. Run detail  `/runs/{id}`

Header shows state/mode; actions: **Rerun this workflow**, **Cancel this run**.
Body renders the run's **views** (custom renderers, incl. live-session terminal + agent chat).
Each view: **Pin to dashboard** / **Unpin from dashboard**.
Empty-state: "This run declares no views".

> This is where the **live coding-session terminal** appears ("Open live session").

**Feedback (Run detail):** 
-
-

---

## 8. My slots  `/my-slots`

**Subtitle:** *"Your personal slot instances: credentials only you (and workflows you configure) can bind…"*

Sections: **Personal instances** (list + **New personal instance**, edit, delete/rotate);
draft form "New personal instance" / "Edit \<name\>" with **Save** / **Cancel**.

**Feedback (My slots):** 
- Workspace slot: There is no way to set any proper workspace, let's start with a simple github slot for now

---

## 9. Operator ▸ Slots  `/operator/slots`

**Subtitle:** *"Reusable slot instances: configure a provider once (the team mailbox, a license seat …) and bind it from any number of workflow configurations."*

Sections: **Slot instances** (grouped by category, list + **New slot instance**, edit, delete);
draft form with **Save**/**Cancel**; **Dependency slot wirings** sub-section.

**Feedback (Operator slots):** 
- We do not need to display settings in the slots, rather we would display them when editing ( with rights )
- 

---

## 10. Administration ▸ Principals  `/admin`

**Subtitle:** *"Everyone — and everything — that can act on this platform: people, services, and AI agents."*

Sections: **Principals** list (assign/revoke role chips, **Disable**);
**Create human principal**; **Create AI principal**.

**Feedback (Principals):** 
-
-

---

## 11. Administration ▸ Identity sources  `/admin/identity-sources`

**Subtitle:** *"Import users from existing systems — an LDAP / Active Directory server or a CSV export…"*

Sections: **Configured sources** (test/import/edit/delete per source);
add/edit form with **Roles for imported users** mapping table (**+ Map a group to a role**), **Save**/**Cancel**.

**Feedback (Identity sources):** 
-
-

---

## 12. Administration ▸ Provider catalog  `/admin/provider-catalog`

**Subtitle:** *"Which registered slot providers may be picked when configuring slot instances and workflows. Availability is deny-by-default…"*

Section: **Registered providers** (enable/disable availability per provider).

**Feedback (Provider catalog):** 
- Too crowded, the user doesn't want to see the contracts the provider fulfills, rather he want to see for which allowed workflows we can configure them ( Also showing when no workflows are allowed. )
- 

---

## 13. Administration ▸ Audit  `/audit`

**Subtitle:** *"Every policy decision and administrative change, newest first. Entries are immutable."*

Filter + **Apply**; paged log ("Load more"); empty-state "No audit entries".

**Feedback (Audit):** 
-
-

---

## 14. Login  `/login`

Login form + **Sign in**. (Custom `LoginLayout`, no sidebar.)

**Feedback (Login):** 
-
-

---

## 15. Anything not covered / new asks

Presentation is **coding-session-workflow only**, manually configured but easy, controllable from the dashboard.
List anything missing, any story-flow concern, or net-new items here.

**Feedback (other):** 
- The administrator has no way to enable or disable workflows.
- Proper workspaces like a github workspace
- Connector like github connector, mail connectors etc, it must be usable by a non-technical user
- Less crowded UI, think more like claude.ai for a different purpose, easy to setup, it must convince users that it's easy to use ( Applies to all menus ).
