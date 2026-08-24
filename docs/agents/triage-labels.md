# Triage Labels

The skills speak in terms of five canonical triage roles; this repo adds one local string, `shipped`.
This file maps those roles to the actual strings used in this repo's issue tracker.

| Label in mattpocock/skills | Label in our tracker | Meaning                                  |
| -------------------------- | -------------------- | ---------------------------------------- |
| `needs-triage`             | `needs-triage`       | Maintainer needs to evaluate this issue  |
| `needs-info`               | `needs-info`         | Waiting on reporter for more information |
| `ready-for-agent`          | `ready-for-agent`    | Fully specified, ready for an AFK agent  |
| `ready-for-human`          | `ready-for-human`    | Requires human implementation            |
| `wontfix`                  | `wontfix`            | Will not be actioned                     |
| —                          | `shipped`            | Built and merged; the ticket is a record |

When a skill mentions a role (e.g. "apply the AFK-ready triage label"), use the corresponding string
from this table.

## How a label is applied here

This repo's tracker is markdown files, not an API with a label field. Applying a label means editing
the `Status:` line near the top of the ticket:

```markdown
# FD-012 — Reference thumbnails survive rotation

Status: ready-for-agent
```

One role at a time — the line holds exactly one string. A ticket with no `Status:` line counts as
`needs-triage` — except FD-001..FD-008, which have no ticket file at all and are recorded as shipped
in [the PRD index](../prds/README.md) instead. Removing a label means replacing it with the role that now applies, not deleting the
line.

Edit the right-hand column above if the vocabulary ever changes.
