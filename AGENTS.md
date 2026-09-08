# Rules for AI agents working in this repository

These apply to every agent, assistant, or automation that edits this repository, regardless of vendor.

## Attribution and privacy

- NEVER put session links, conversation URLs, tracking IDs, or tool-generated trailers in commit messages, pull request descriptions, code comments, or documentation. Lines such as `Claude-Session: ...`, `Generated with ...`, `Co-Authored-By: <assistant>`, or any `claude.ai`, `chatgpt.com`, or similar URL are forbidden.
- NEVER list yourself, your model, or your vendor in a commit message or PR description. Commits are authored by the human who owns the repository; the message describes the change and nothing else.
- Do not write anything into the repository that identifies the account, machine, or session the work came from.

## Pushing

- NEVER `git push` unless the repository owner explicitly asks for a push in the current conversation. Committing locally is fine when asked; publishing is a separate decision.
- Before committing or pushing a large change set (a new subsystem, many files, new dependencies, anything touching networking, authentication, file access, or process launching), run a security review and an architecture review of the diff, fix the findings, and only then commit.
- Before pushing anything, re-read the outgoing commit messages (`git log origin/main..HEAD`) and confirm none of them break the attribution rules above.

## Working conventions

- Comments are budgeted: one-line XML docs, no narration, no banner dividers.
- New editor buttons and menu labels carry no trailing "...".
- Engine code (`Voltage.Engine`) must not depend on editor types (`Voltage.Editor`).
- The Editor Gateway (`Voltage.Editor/Gateway`, `Voltage.Cli`, `docs/gateway/README.md`) is the supported way for agents to drive a running editor.
