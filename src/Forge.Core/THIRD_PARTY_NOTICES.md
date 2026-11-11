# Provenance notes — `Forge.Core`

This file records third-party designs adapted inside `Forge.Core` and the license each one came from. It is a provenance
note, not a license notice: no upstream source file is copied verbatim into this project. The one artifact that does carry
copied material is `src/Forge.App/THIRD_PARTY_NOTICES.md` (Fluent System Icons geometry).

## Model provider layer — microsoft/agent-framework and Microsoft.Extensions.AI

- Projects: `microsoft/agent-framework` (<https://github.com/microsoft/agent-framework>, MIT) and
  `Microsoft.Extensions.AI` (<https://github.com/dotnet/extensions>, MIT).
- What was adapted, in `src/Forge.Core/Providers/Internal/ChatProviderPipeline.cs`: the *middleware-around-a-chat-client*
  composition shape (`IChatClient` + `ChatClientBuilder.Use(...)`) and the split it implies — the endpoint owns the wire
  format, decorators own retry and cross-cutting policy. The implementations are Forge's own: the streaming contract is
  `IChatProvider`/`ChatStreamEvent` (`Forge.Abstractions`), and retry is `RetryingChatProvider` (`Forge.Core.Providers`),
  which predates this adaptation.
- What was **not** taken: the OpenAI/Anthropic official SDK clients agent-framework's providers wrap, the
  `Microsoft.Extensions.AI` content model, and its function-invocation client. Forge's provider adapters speak the wire
  protocols directly (`OpenAiCompatibleProvider`, `AnthropicProvider`), so the SDK layer has nothing Forge needs and would
  add a dependency outside the approved package list.

## Provider endpoints and authentication

The URLs, header names, OAuth client id, device-flow endpoints and the "first system block must be the Claude Code
identity sentence" rule in `ClaudeSubscriptionCredentials.cs` / `CopilotAuthentication.cs` come from the providers' own
public documentation, not from a copied source file. They are recorded in `forge-core`'s SPEC and `src/Forge.App/SPEC.md`
because they are behavior contracts the tests pin, not because of a license.
