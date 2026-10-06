# Agent instructions

## Required reading

Read these before proposing or changing anything in this repository:

1. [README.md](README.md): the goals, the transport decisions, the baselines and the open design questions.
2. [CALL-INTERFACE.md](CALL-INTERFACE.md): the draft call interface between hosts, the core and service methods.

If a change conflicts with either document, raise the conflict instead of working around it. When a design question is decided, record the decision in the document that holds the question.

## Reference material

- [RESEARCH-MEMORY.md](RESEARCH-MEMORY.md): research on buffers, arenas, zero-allocation marshalling and the FASTER/Garnet techniques, with recommendations for the open questions. Read it before working on the core's buffer handling, the codec or message types. Its source notes are in [research/memory](research/memory).

## Rules

- **No AI attribution.** Commits, pull request descriptions, issues, code, comments and documentation carry no AI attribution. That means no `Co-Authored-By` trailers for AI tools, no "Generated with" lines, and no model or tool names.
- **Minimal dependencies.** Each new package dependency needs a stated reason in the pull request that adds it.
- **Measured performance claims.** A claim about speed cites a benchmark run whose raw data is published, measured on the same host and calls as its baseline.
