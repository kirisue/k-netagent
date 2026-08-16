# 自迭代指引

每个 `.md` 文件描述一次允许由 Agent 提议的代码迭代。程序只会把 `Targets` 中明确列出的相对路径交给模型，模型试图修改其他文件时提案会被拒绝。

```markdown
# 标题
Goal: 一句话、可验证的目标
Targets:
- src/TestAgent.Core/SomeFile.cs
- tests/TestAgent.Tests/SomeFileTests.cs

## Requirements
- 行为要求

## Acceptance
- 可观察的验收标准
```

工作流：生成提案 → 临时副本执行 `dotnet build` 和 `dotnet test` → 用户审阅并勾选批准 → 应用到真实工作区 → 再次构建测试 → 失败自动回滚。请把每次指引控制在少量相关文件内。
