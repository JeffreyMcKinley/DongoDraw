# Triage Labels

The skills speak in terms of five canonical triage roles. This file maps those roles to the actual
label strings on this repo's GitHub issues.

| Label in mattpocock/skills | Label in our tracker | Meaning                                  |
| -------------------------- | -------------------- | ---------------------------------------- |
| `needs-triage`             | `needs-triage`       | Maintainer needs to evaluate this issue  |
| `needs-info`               | `needs-info`         | Waiting on reporter for more information |
| `ready-for-agent`          | `ready-for-agent`    | Fully specified, ready for an AFK agent  |
| `ready-for-human`          | `ready-for-human`    | Requires human implementation            |
| `wontfix`                  | `wontfix`            | Will not be actioned                     |

When a skill mentions a role (e.g. "apply the AFK-ready triage label"), use the corresponding string
from this table.

## How a label is applied here

```
gh issue edit <number> --add-label ready-for-agent --remove-label needs-triage
```

One state role at a time — an issue carries exactly one. An issue with no state label counts as
`needs-triage`. Removing a role means replacing it with the one that now applies.

There is no `shipped` label. Shipped work is a **closed issue**; close it with a comment saying what
it left behind.

The category labels `/triage` applies alongside the state — `bug` and `enhancement` — are GitHub's
defaults and already exist. The orthogonal `context:*` labels are described in
[issue-tracker.md](issue-tracker.md#labels); they are not triage state and `/triage` does not touch
them.

All labels above exist on the repo. `gh issue create --label <missing>` fails rather than creating a
label, so run `gh label create` first if this table ever grows.

Edit the right-hand column above if the vocabulary ever changes.
