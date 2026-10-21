using AgentX.Core;
using Xunit.Abstractions;

namespace AgentX.Core.Tests;

/// <summary>
/// Live model/thinking contract against the real Pi engine: list models and
/// thinking levels, prove rejected changes throw without touching engine
/// state, and switch models with a revert. Isolated temp manager roots;
/// loud skip without the shim. Never touches user sessions.
/// </summary>
public sealed class EngineLiveTests
{
    private const string PiShim = @"D:\AgentTools\bin\pi.cmd";
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(5);

    private readonly ITestOutputHelper _output;

    public EngineLiveTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private bool PiAvailable()
    {
        if (File.Exists(PiShim))
        {
            return true;
        }

        _output.WriteLine($"SKIP: Pi shim not found at {PiShim}; live engine test cannot run here.");
        return false;
    }

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ax-engine-it-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string ProjectDir(string root, string name)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string root)
    {
        try { Directory.Delete(root, recursive: true); }
        catch (Exception) { /* never fails the test */ }
    }

    [Fact]
    public async Task ListModels_ReturnsPiReportedOptions()
    {
        if (!PiAvailable())
        {
            return;
        }

        var root = TempRoot();
        using var budget = new CancellationTokenSource(Budget);
        var ct = budget.Token;
        try
        {
            await using var manager = new PiWorkerManager(root);
            var worker = manager.CreateWorker(ProjectDir(root, "proj"), "Agent ONE");
            await worker.ConnectAsync(ct);
            var models = await worker.GetModelsAsync(ct);
            Assert.NotNull(models);
            foreach (var m in models)
            {
                _output.WriteLine($"MODEL provider={m.Provider} id={m.Id} name={m.Name}");
            }

            var levels = await worker.GetThinkingLevelsAsync(ct);
            Assert.NotNull(levels);
            _output.WriteLine($"THINKING: {string.Join(",", levels)}");
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task RejectedChanges_ThrowWithoutTouchingState()
    {
        if (!PiAvailable())
        {
            return;
        }

        var root = TempRoot();
        using var budget = new CancellationTokenSource(Budget);
        var ct = budget.Token;
        try
        {
            await using var manager = new PiWorkerManager(root);
            var worker = manager.CreateWorker(ProjectDir(root, "proj"), "Agent ONE");
            await worker.ConnectAsync(ct);
            var before = await worker.GetStateAsync(ct);
            await Assert.ThrowsAnyAsync<Exception>(() =>
                worker.SetModelAsync("no-such-provider", "no-such-model", ct));
            await Assert.ThrowsAnyAsync<Exception>(() =>
                worker.SetThinkingLevelAsync("no-such-level", ct));
            var after = await worker.GetStateAsync(ct);
            Assert.Equal(before.ModelId, after.ModelId);
            Assert.Equal(before.ThinkingLevel, after.ThinkingLevel);
            _output.WriteLine($"STATE-PRESERVED model={after.ModelId} thinking={after.ThinkingLevel}");
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task SwitchModel_ConfirmsAndReverts()
    {
        if (!PiAvailable())
        {
            return;
        }

        var root = TempRoot();
        using var budget = new CancellationTokenSource(Budget);
        var ct = budget.Token;
        try
        {
            await using var manager = new PiWorkerManager(root);
            var worker = manager.CreateWorker(ProjectDir(root, "proj"), "Agent ONE");
            await worker.ConnectAsync(ct);
            var original = await worker.GetStateAsync(ct);
            var models = await worker.GetModelsAsync(ct);
            var target = models.FirstOrDefault(m => m.Id != original.ModelId);
            if (target is null)
            {
                _output.WriteLine($"SKIP-SWITCH: only one model reported ({original.ModelId}); list path already proven.");
                return;
            }

            var switched = await worker.SetModelAsync(target.Provider, target.Id, ct);
            Assert.Equal(target.Id, switched.ModelId);
            var levels = await worker.GetThinkingLevelsAsync(ct);
            Assert.NotNull(levels);
            _output.WriteLine($"SWITCHED to {target.Id}; thinking levels now: {string.Join(",", levels)}");
            var restored = await worker.SetModelAsync(
                original.Provider ?? target.Provider, original.ModelId ?? target.Id, ct);
            Assert.Equal(original.ModelId, restored.ModelId);
            _output.WriteLine($"REVERTED to {restored.ModelId}");
        }
        finally
        {
            Cleanup(root);
        }
    }
}
