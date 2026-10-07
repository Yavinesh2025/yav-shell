# Agent adapters

YAV Shell does not contain a coding agent. It drives the agents you have installed through their
documented programmatic interfaces and reuses their tool execution and reasoning loops.

Facts in this document were verified on **2026-09-29 and 2026-09-30** against the installed programs,
their generated schemas and the providers' published documentation, and, for one pair of models, in
two runs with real models. Provider features change; `/doctor` reports what is true on your machine.

## Overview

| | Codex (app server) | Codex (exec) | Claude Code |
| --- | --- | --- | --- |
| Adapter id | `codex-app-server` | `codex-exec` | `claude-cli` |
| Interface | `codex app-server`, JSON lines over standard input/output | `codex exec --json` | `claude -p` with `stream-json` input and output |
| Provider's own status | **Experimental** | Stable | Stable |
| Tested with | Codex CLI 0.158.x and 0.159.x | Codex CLI 0.158.x and 0.159.x | Claude Code 2.1.284 and 2.1.285; 2.1.259 is the oldest that is accepted |
| Streaming events | yes | yes | yes |
| Interactive approvals | yes | **no** | yes |
| Interrupt a turn | yes (`turn/interrupt`) | only by ending the process | yes (control request) |
| Steer a running turn | yes (`turn/steer`) | no | no |
| Resume a conversation | yes, by thread id | yes, by explicit session id | yes, by session id |
| Structured review output | yes (`outputSchema`) | yes (`--output-schema`) | yes (`--json-schema`) |
| Lists models without inference | yes (`model/list`) | **no** | yes (`initialize` control request) |
| Reports model and effort in effect | yes | **no** | yes (model: `init` message; effort: answer to `get_settings`) |
| Token usage | yes, cumulative per conversation | yes, per turn | yes, per turn |
| Charges | not reported | not reported | Claude Code's own estimate |
| Rate-limit indicators | yes | no | no |
| Read-only enforcement for Model B | Codex sandbox | Codex sandbox | tool restriction |
| Provider speed | tier `priority` (shown as "Fast") | no | fast mode, where the model supports it |

"no" in this table is a limitation YAV reports, not something it works around.

## Codex through the app server

* OpenAI documents the app server as experimental and not supported for production workloads. YAV
  labels the adapter **Experimental** everywhere and never presents it as production-supported. Its
  protocol can change with any Codex release; a version outside `0.158.x` and `0.159.x` produces a
  warning.
* One `codex app-server` process serves all Codex conversations of a YAV session. It is started the first
  time it is needed. Listing models, reading the account and reading rate limits send no inference request.
* YAV passes its role instructions as `developerInstructions`. Because that parameter replaces the
  `developer_instructions` of your own Codex configuration, YAV reads yours first and puts them in front
  of its own. Codex's native instructions and the project's `AGENTS.md` stay in effect. When your
  configuration cannot be read, the conversation runs without your own developer instructions and
  says so in a warning; the next conversation reads them again.
* Effort is requested with `config.model_reasoning_effort` and read back from the answer to
  `thread/start`.
* Model B: `sandbox: read-only`, `approvalPolicy: never`.
* **Who decides about access.** Every conversation is opened with `approvalsReviewer: "user"`. A
  conversation is refused - nothing is sent in it - when Codex does not say who decides, names
  anybody else, or reports that its own reviewer takes part (`item/autoApprovalReview/*`,
  `autoApprovalReview/strictReviewRequired`, `guardianWarning`), also for one of its sub-agents.
  What reaches a refused conversation afterwards is answered with cancel.
* **What the sandbox reaches.** A conversation is refused when Codex names no sandbox, or a
  `readOnly` or `workspaceWrite` sandbox whose network access or writable folders cannot be read.
  Folders outside the workspace that the sandbox may write stop a run while Quality Lock is on;
  network access is reported and said once per model.
* **The account.** Every conversation asks `account/read` and reports the route it finds
  ("ChatGPT plan (…)", "OpenAI API key", "Amazon Bedrock", "Not signed in"; never an address or an
  organization). When Codex says that the account changed (`account/updated`), the conversation
  reports it again and it is compared again; another kind of route stops the run.
* **Warnings that name no conversation** - also those Codex gives right after it starts, before any
  conversation exists, and the one that says that its Windows sandbox cannot protect folders
  everybody may write to - are kept, the last 20 of each Codex process, and shown once in every
  conversation opened later.
* **What YAV does not grant.** Questions of MCP servers (elicitations) are declined, requests of
  sub-agents are refused, and requests this version does not handle are refused; each is said in a
  warning. Extra permissions are granted for the current turn only. An approval of a kind YAV does not
  know, or for a change whose files are not all named (more than 20), needs the word `allow` and
  cannot be given for the conversation.
* A conversation is resumed without its history (`excludeTurns`), and a conversation Codex closes
  ends and can be resumed. After a crash, a turn counts as running only when Codex reports its thread
  active; when Codex cannot be asked, that is reported as the failure it is.
* The working directory Codex reports is the one it was given, junctions and short names as they are.
* When a turn does not end after it was asked to, the app-server process is ended (it is YAV's own child).
  Other Codex conversations end with it and are resumed by thread id in a new process.
* **Seen in the first run with real models, on Windows:** inside the sandbox of Codex, `python` and `py`
  were not found, although Python is on PATH. It is installed for the user
  (`%LOCALAPPDATA%\Programs\Python`). Codex then asked to run the project's tests outside the sandbox.
  YAV passes such a question on to you and grants nothing by itself; `yav run` ends as Approval
  Required. Why the sandbox does not reach that Python was not investigated.

### Effort values (observed 2026-09-29, account with a ChatGPT Pro plan)

`low`, `medium`, `high`, `xhigh`, `max` and, for some models, `ultra`. The provider describes them as:

| Value | Provider's description |
| --- | --- |
| `max` | Maximum reasoning depth for the hardest problems |
| `ultra` | Maximum reasoning with automatic task delegation |

`ultra` is not simply "more than `max`": it adds delegation to other agents. YAV therefore **does not
choose between them for you**. When a model lists a value YAV cannot rank, "maximum" is not resolved and
the run does not start until you set the exact value: the shell asks for it when a request needs it, and
`/effort` sets it at any time. YAV has not verified how delegated agents inherit the sandbox of a
conversation.

### Provider speed

The model catalog lists a service tier with id `priority`, name "Fast", described as "2x speed,
increased usage". Earlier catalogs used the id `fast`. YAV treats these two ids, and no others, as the
separately metered faster tier. It is never enabled without `/speed provider` and your authorization, and
a conversation that reports a tier you did not authorize is blocked.

## Codex through `codex exec`

A compatibility path for non-interactive runs.

* It cannot ask for approval. An action that needs one ends the turn as **Approval Required**. YAV never
  passes a flag that broadens permissions to make this path work.
* It does not report the model or the effort in effect, so both stay **Requested / Unverified**. Under
  strict policy such a run is blocked before it starts (`settings-unverifiable`).
* It cannot list models. YAV uses the list from the app server adapter when that is available and labels
  the source.
* The questions of a first request list the models of the app server, and those of `codex exec` only when
  the app server cannot be used. `/models a|b codex-exec <model>` chooses it on purpose.
* Each turn is a process. Resuming uses `codex exec resume <session-id>` with the sandbox passed as
  `-c sandbox_mode=...`, because `resume` has no `-s` option.

## Claude Code

* Started as `claude -p --output-format stream-json --input-format stream-json --verbose`.
* The `init` message, which Claude Code sends when a turn begins, carries `model`, `tools`,
  `permissionMode`, `apiKeySource` and `fast_mode_state`. It does **not** carry the effort when Claude
  Code is driven this way, although the published types of the Agent SDK have a field for it: the
  installed program describes that field as one that only some hosts publish. Version 0.1.0 of YAV
  read it, and so could not confirm any effort.
* The effort **in effect** is read from the answer to the control request `get_settings`
  (`applied.effort`), which Claude Code describes as what it will send with its next request, after
  everything that can lower it. YAV asks before every prompt and waits for the answer before it
  sends the prompt. No model is asked for it, and it takes no time that could be measured. Claude
  Code drops an effort silently for a model that takes none: `--model haiku --effort max` is
  answered with `effort: null`, which YAV reports as `none` and treats as a lower effort than the
  one requested.
* With a schema for the result (`--json-schema`), Claude Code gives the conversation one tool more,
  `StructuredOutput`, through which the model hands the result back. YAV counts it among the tools
  that change nothing and does not show it as work of the agent.
* **The account the conversation works with** is read from the answer to the control request
  `initialize`, which YAV sends together with `get_settings`, before every prompt. The route that is
  shown before a run comes from `claude auth status`, which is another process, started in another
  way: a review is started with `--restricted`, and then Claude Code loads no settings at all, the
  user's included (`get_settings` answers with an empty list of sources). A key or a cloud provider
  that is set in the user's settings is therefore what `claude auth status` shows and not what a
  review works with. What the conversation says decides: when it is another kind of route than the
  one that was shown, nothing is sent and the run stops, under every policy. What the installed
  Claude Code 2.1.284 answered, asked without a prompt:

  | Started with | `account` in the answer |
  | --- | --- |
  | the sign-in to claude.ai | `apiProvider: firstParty`, `subscriptionType: Claude Max`, and the address and organization of the user, which YAV does not read |
  | `ANTHROPIC_API_KEY` | `apiKeySource: ANTHROPIC_API_KEY`, `apiProvider: firstParty`, `tokenSource: claude.ai`, no plan |
  | `ANTHROPIC_AUTH_TOKEN` | `apiProvider: firstParty`, `tokenSource: ANTHROPIC_AUTH_TOKEN`. No route follows from that; it is shown and not called verified |
  | `CLAUDE_CODE_USE_BEDROCK=1` | `apiProvider: bedrock` |

  A version that does not answer leaves the route unconfirmed and says so in a warning. The name of
  a key in the `init` message is still compared; that no key is named there confirms nothing.
* **What YAV sends is marked `client_composed` when it quotes what nobody checked**: every review
  (it quotes the candidate) and every repair (it quotes what the reviewer found and what the checks
  wrote). Claude Code then takes the text as it is written. Without the mark it takes `@path` in a
  text for a file to attach and a line that begins with `/` for a command; that is what the
  installed program says about the field. Whether a file that is attached in this way passes the
  same checks as a file the agent reads with a tool was not tried. Your own request is not marked,
  because with the mark Claude Code also leaves out what it attaches to a prompt by itself, such as
  the rules files of the project.
* Model A: `--permission-mode acceptEdits` with `--permission-prompt-tool stdio`, so edits inside the
  workspace proceed and everything else is asked of you through YAV. The question lists **everything
  the tool is given**, a line for each field and a line for each line of a text, because what a tool
  sends away can be in any field. What a shell is given beside its command is listed too; the
  description the agent gives of its own command is marked as the agent's words. The question shows
  24 of these lines and counts the rest; a request of which not everything is shown can be allowed
  only with the word `allow` (the adapter itself cuts at 400 lines and at 2000 characters a line,
  and says so). A request Claude Code marks as one that needs the user's own answer in a window of
  Claude Code is declined, because that window is not there.
* **The working directory** Claude Code reports is the workspace with every junction resolved (seen
  with 2.1.284: started in a junction, it names the directory the junction leads to). YAV resolves
  junctions and symbolic links as well before it compares the two. A drive made with `subst` is not
  resolved, so a workspace reached through one would be taken for another directory, and the run stops.
* **A Claude Code that ends while it is asked** what it works with is not started again for that
  turn: nothing is sent, and the run says what Claude Code reported before it ended.
* Model B: see [security-boundaries.md](security-boundaries.md).
* Reported cost (`total_cost_usd`) is Claude Code's estimate. YAV shows it as *estimated*, never as a
  billing statement.
* **Tested with the releases that were tried.** Claude Code changes what it says from one release to
  the next: the effort left the `init` message between the published types and 2.1.284. YAV
  therefore calls only 2.1.284 and 2.1.285 tested, the two that ran with real models, and warns for
  every other version. Versions before 2.1.259 are refused, because
  they lack the options that keep a reviewer read-only. A later version is used; what it does not say
  any more is shown as not confirmed, which stops a run under strict policy.

### Authentication

Anthropic's policy (`code.claude.com/docs/en/legal-and-compliance`, read 2026-09-29) says that
third-party developers may not offer Claude.ai login or route requests through subscription credentials
on behalf of their users. YAV therefore:

* never offers a Claude.ai login of its own, never reads or relays tokens, and never starts an OAuth flow
  of its own. The first request does not offer to sign Claude Code in: it says how to sign in with Claude
  Code itself. `/login claude` starts Claude Code's own `claude auth login` only when you ask for it with
  that command, and after you typed `yes`;
* starts the unmodified `claude` program that you installed and signed in to yourself;
* shows the account route that program reports (subscription, API key, cloud provider) and its billing
  kind before any work;
* requires a one-time acknowledgement **per route** that you have read the provider's terms for it, which
  the shell asks for when a request needs it and `/login` asks for as well;
* supports an API key as the explicitly configured, separately billed route (`/login claude --api-key`).

Whether your subscription covers use through a program like YAV is a question of the provider's terms for
your account. YAV cannot verify entitlement and does not claim it.

### The credential boundary

An API key is given to Claude Code through the environment of its process. Commands that Claude Code
runs inherit it. YAV refuses Claude Code as **Model A** with an API key in a project you have not
trusted. As **Model B** it has no tool that runs code.

## Billing routes

| Route | Shown as | Billing kind |
| --- | --- | --- |
| ChatGPT plan through Codex | `ChatGPT plan (<plan>)` | Included in subscription, counted against plan limits |
| OpenAI API key through Codex | `OpenAI API key` | Pay per token |
| Claude subscription through Claude Code | `Claude subscription (<type>)` | Included in subscription |
| Anthropic API key | `Anthropic API key` | Pay per token |
| Cloud provider (Bedrock, Vertex, Foundry) | the provider's name | Billed by the cloud provider |

YAV never changes the account, buys credits, enables paid overage or moves a run to another provider.
When a provider reports a limit, the run stops in the state **Rate Limited**.

## Adding an adapter

An adapter implements `IAgentAdapter` and `IAgentSession` from `Yav.Core.Agents` and declares its
capabilities honestly. The coordinator decides from the capabilities and from what a session reports, not
from the adapter's name. A feature an agent does not have is declared as missing, which produces a visible
limitation instead of an emulation.
