using System.Reflection;

using Kautuka.Gateway.Domain.Execution;

namespace Kautuka.IntegrationTests;

public sealed class HelperRunScopeTests
{
    private static object Identity(HelperRunScope scope) => typeof(HelperRunScope)
        .GetField("state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scope)!;

    [Fact]
    public async Task RecursiveAsyncIteratorsShareLiveAdmissionButIndependentRootsDoNot()
    {
        object? root = null;
        object? nested = null;
        async IAsyncEnumerable<int> Run(int depth)
        {
            using HelperRunScope scope = await HelperRunScope.AcquireAsync(TestContext.Current.CancellationToken);
            scope.Enter();
            if (depth == 0)
            {
                root = Identity(scope);
                await foreach (int value in Run(1)) yield return value;
            }
            else
            {
                nested = Identity(scope);
                await Task.Yield();
                yield return 1;
            }
        }
        await foreach (int _ in Run(0)) { }
        Assert.Same(root, nested);
        using HelperRunScope independent = await HelperRunScope.AcquireAsync(TestContext.Current.CancellationToken);
        Assert.NotSame(root, Identity(independent));
    }
}
