# Provenance notes — `Forge.Core`

This file records third-party designs adapted inside `Forge.Core` and the license each one came from. It is a provenance
note, not a license notice: no upstream source file is copied verbatim into this project, with one exception: the
schedule engine in `src/Forge.Core/Automations/AutomationSchedule.cs` is a close adaptation of CodeAlta's (BSD-2-Clause;
notice reproduced below). The one artifact that does carry
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

## Automation schedule — CodeAlta

- Project: `CodeAlta` (<https://github.com/CodeAlta/CodeAlta>, BSD-2-Clause).
- What was adapted, in `src/Forge.Core/Automations/AutomationSchedule.cs`: the bitset-rule representation of a trigger
  (minutes, hours, days, months, weekdays), the cron field parser (`*`, values, ranges, lists, steps, English month and day
  names, Sunday as 0 or 7, "or" when both day fields are restricted), the field-by-field walk of the local clock with an
  8-year horizon, and the DST handling (a skipped local time fires when reached, a repeated one fires once). Adapted to
  Forge's `AutomationTrigger` contract, made public, with validation messages and a next-N preview helper added.
  The scheduler loop (`AutomationEngine`) is Forge's own design.

```
Copyright (c) CodeAlta contributors
All rights reserved.

Redistribution and use in source and binary forms, with or without modification, are permitted provided that the
following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this list of conditions and the following
   disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following
   disclaimer in the documentation and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES,
INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```
