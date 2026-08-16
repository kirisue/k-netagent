# 改进回答自审质量
Goal: 让回答自审准确发现明显遗漏，同时避免无意义改写已经正确的回答。
Targets:
- src/TestAgent.Core/AgentRuntime.cs
- tests/TestAgent.Tests/AgentRuntimeTests.cs

## Requirements

- 保持最多一轮自审，禁止无限递归。
- 自审失败或返回无效 JSON 时保留原回答。
- 不展示或保存隐藏推理过程。
- 增加覆盖“接受原回答”和“返回修订回答”的自动测试。

## Acceptance

- `dotnet build TestAgent.slnx` 成功。
- `dotnet test tests/TestAgent.Tests/TestAgent.Tests.csproj` 成功。
- 不改变公开 Provider 和存储接口。
