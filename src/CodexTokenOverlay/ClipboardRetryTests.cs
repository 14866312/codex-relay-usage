namespace CodexTokenOverlay;

// Deterministic coverage for the clipboard probe's retry wrapper. The real probe
// depends on the machine-wide clipboard, so these checks drive the loop with
// synthetic attempt delegates instead of touching clipboard state.
internal static class ClipboardRetryTests
{
    internal static void Run(Action<string, bool> check)
    {
        var clean = Result(passed: 8, failed: 0);
        var flaky = Result(passed: 7, failed: 1);

        var singleFlakeCalls = 0;
        var singleFlake = ClipboardUiTests.RunWithRetry(_ => ++singleFlakeCalls == 1 ? flaky : clean, 3);
        check("single flake retries once and reports clean", singleFlake.Failed == 0 && singleFlakeCalls == 2 && singleFlake.Passed == 8);

        var persistentCalls = 0;
        var persistent = ClipboardUiTests.RunWithRetry(_ => { persistentCalls++; return flaky; }, 3);
        check("persistent failure stops after max attempts", persistent.Failed == 1 && persistentCalls == 3);

        var cleanCalls = 0;
        var firstClean = ClipboardUiTests.RunWithRetry(_ => { cleanCalls++; return clean; }, 3);
        check("clean first attempt is not retried", firstClean.Failed == 0 && cleanCalls == 1);

        var boundedCalls = 0;
        var bounded = ClipboardUiTests.RunWithRetry(_ => { boundedCalls++; return flaky; }, 1);
        check("attempt bound is honoured", bounded.Failed == 1 && boundedCalls == 1);
    }

    private static DiagnosticsRunner.SelfTestResult Result(int passed, int failed) =>
        new(passed, failed, []);
}

