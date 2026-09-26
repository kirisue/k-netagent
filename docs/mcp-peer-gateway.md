# MCP Peer Gateway

K.netagent remains one central Agent. MCP Peers are user-connected external capability providers, not automatically spawned sub-Agents.

## Supported peers

### Codex

1. Install and sign in to the Codex CLI separately.
2. In K.netagent, select `＋` → `外部 Agent / MCP` → `添加 Codex…`.
3. Select the real Codex CLI `.exe`, not a shortcut or a file inside the workspace.
4. Click `连接`. K.netagent starts the fixed command `codex mcp-server` as its child process.
5. Refresh tools and explicitly allow `codex`.

Calls are forced to the active K.netagent workspace with `sandbox=read-only` and `approval-policy=never`. The external Codex session may return analysis, evidence or a proposed patch, but it cannot directly modify the workspace through this gateway.

Official reference: [Run Codex as an MCP server](https://learn.chatgpt.com/docs/mcp-server).

### Claude Code

1. Install and sign in to Claude Code separately.
2. Select `添加 Claude Code…` and choose its real `.exe`.
3. Click `连接`. K.netagent starts the fixed command `claude mcp serve`.
4. Refresh tools and allow only the displayed read tools you need.

The first version hard-blocks mutation and shell tools. Only `Read`, `View`, and `LS` are callable, and explicit paths must remain relative to the K.netagent workspace without reparse-point traversal.

Official reference: [Claude Code MCP](https://docs.anthropic.com/en/docs/claude-code/mcp).

### Custom local HTTP peer

Start the MCP service yourself, then add its Streamable HTTP endpoint. Only loopback `http` or `https` endpoints without credentials, query strings, or fragments are accepted. Remote authentication and OAuth are intentionally not implemented in this version.

## Approval sequence

```text
User adds profile
→ User explicitly connects peer
→ K.netagent discovers tools
→ User allows individual tools
→ Agent may propose one peer call
→ User approves that exact peer/tool/task
→ bounded untrusted output returns to K.netagent
→ K.netagent independently verifies before any local write
```

Refresh, chat startup, tool discovery and model self-assessment never start a peer. If no peer is connected, the model receives `peer_not_connected` and cannot connect it by itself.

## Escalation Evaluator

Automatic escalation is opt-in per Codex profile. Turn on `允许评估不足时提出委托（仍逐次审批）`, connect that profile, and allow the `codex` tool. The toggle is disabled for Claude and custom HTTP profiles.

The evaluator runs after the local draft and uses deterministic evidence rather than hidden model confidence:

- explicit request to consult Codex/Claude/another Agent;
- bounded tool loop exhausted;
- repeated identical tool calls;
- tool failures and timeouts;
- implementation requested without a successful workspace write;
- validation requested without a successful verification command;
- empty or explicitly uncertain draft;
- successful write and verification reduce the score.

The default threshold is `50`. Cancellation, a prior user rejection, an explicit instruction not to use another Agent, image input, zero eligible Codex peers, or multiple eligible Codex peers suppress escalation. The evaluator itself only returns a decision. A normal `call_mcp_peer_tool` approval is still required, and declining it preserves the original draft.

When the call succeeds, its bounded text is supplied as untrusted evidence to one tool-free synthesis request. Only the synthesized final answer is persisted; raw Peer output is not added to chat history. Each Agent run can make at most one evaluator-triggered Peer call and never uses `codex-reply` automatically.

## Persistence and limits

- Profiles: `%LOCALAPPDATA%\TestAgent\mcp-peers\`
- No bearer token, OAuth credential, arbitrary environment variable, argument list, or shell command is stored.
- At most three simultaneous peers.
- One operation at a time per peer.
- Connect timeout: 5–60 seconds.
- Tool timeout: 5–120 seconds.
- Output: text only, bounded; images, audio, structured content and oversized data are omitted.
- STDIO child processes are closed when disconnected or when K.netagent exits.

## VS Code boundary

VS Code uses MCP servers as a client but does not generically expose every built-in or extension tool as an MCP server for K.netagent. The current K.netagent VS Code bridge remains the supported integration for active editor, diagnostics, task metadata and extension metadata. A future bridge version can deliberately expose a reviewed subset as MCP tools.
