using ValorantOptimizer.Core;

namespace ValorantOptimizer.TestSupport;

/// <summary>Records process launches and returns queued/handler results.</summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Queue<ProcessResult> _queue = new();

    public List<(string FileName, IReadOnlyList<string> Arguments)> Invocations { get; } = [];

    public Func<string, IReadOnlyList<string>, ProcessResult>? Handler { get; set; }

    public void Enqueue(ProcessResult result) => _queue.Enqueue(result);

    public void EnqueueSuccess(string stdout = "") => _queue.Enqueue(new ProcessResult(0, stdout, string.Empty));

    public void EnqueueFailure(string stderr = "", int exitCode = 1) => _queue.Enqueue(new ProcessResult(exitCode, string.Empty, stderr));

    public Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Invocations.Add((fileName, arguments.ToList()));

        if (Handler is not null)
        {
            return Task.FromResult(Handler(fileName, arguments));
        }

        return Task.FromResult(_queue.Count > 0
            ? _queue.Dequeue()
            : new ProcessResult(0, string.Empty, string.Empty));
    }
}
