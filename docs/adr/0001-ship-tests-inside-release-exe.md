# 合成测试与自检编入发布 EXE

`*Tests.cs` 与合成自检不放在独立测试工程或 `#if DEBUG` 之后，而是编译进发布 EXE，由 `--self-test` 与 `Verify.ps1` 驱动。发布验证校验的就是用户实际拿到的二进制，WinForms 行为也能在发布路径上被覆盖；代价是发布产物包含测试代码。若要改动这一点，必须保留一条等价的发布时验证路径。

来源：AGENTS.md（Testing）；提交 b60d3e7；CHANGELOG 1.3.7 验证段。
