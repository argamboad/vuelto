using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Vuelto.Api.Tests.Infrastructure;

/// <summary>One call a <see cref="Recording{T}"/> saw: the interface method and the arguments it was given.</summary>
public sealed record RecordedCall(string Method, IReadOnlyList<object?> Args);

/// <summary>
/// A recording test double (v4 audit T25, R151): wraps the REAL implementation of an interface, forwards every
/// call to it unchanged, and writes down what was asked. Outcome tests cannot tell a narrow read from a broad one
/// that happens to return the same answer; a test holding the record can assert exactly which tenants a
/// pre-auth, cross-tenant code path touched — and that a collaborator was never consulted at all.
/// </summary>
public class Recording<T> : DispatchProxy where T : class
{
    private T _inner = null!;
    private List<RecordedCall> _calls = null!;

    /// <summary>Wraps <paramref name="inner"/>; every call through the returned proxy is appended to <paramref name="calls"/>.</summary>
    public static T Wrap(T inner, List<RecordedCall> calls)
    {
        var proxy = Create<T, Recording<T>>();
        var recording = (Recording<T>)(object)proxy;
        recording._inner = inner;
        recording._calls = calls;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        _calls.Add(new RecordedCall(targetMethod!.Name, args ?? []));
        try
        {
            return targetMethod.Invoke(_inner, args);
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(e.InnerException).Throw(); // the real exception, as the caller would see it
            throw;
        }
    }
}
