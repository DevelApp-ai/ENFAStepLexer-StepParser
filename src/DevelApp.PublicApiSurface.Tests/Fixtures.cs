using System;
using System.Collections.Generic;

namespace DevelApp.PublicApiSurface.Tests.Fixtures;

#pragma warning disable CS0067, CS0169, CS0649 // fixtures intentionally vary visibility and shape

public interface ISampleContract
{
    int TokenCount { get; }
}

public abstract class SampleBase
{
    public abstract void Reset();
}

/// <summary>
/// Fixture exercising every surface rule the extractor must implement.
/// Only members asserted in <see cref="ApiSurfaceTests"/> matter; the
/// internal and private members must NOT appear in any extracted line.
/// </summary>
public class VisibleClass : SampleBase, ISampleContract
{
    public const int MaxTokenLength = 128;
    public static readonly string DefaultPattern = "";

    public int PublicField;
    internal int InternalField;
    protected int ProtectedField;
    private int _privateField;

    public string Name { get; set; } = "";
    public int TokenCount
    {
        get => PublicField;
        private set { }
    }

    internal string InternalProperty { get; set; } = "";

    public event EventHandler? SomethingHappened;
    internal event EventHandler? InternalEvent;
    protected event EventHandler? ProtectedEvent;

    public void Add(int a, int b) { }
    public TResult Convert<TInput, TResult>(TInput input) where TInput : notnull => throw new NotSupportedException();
    public void WithParams(params string[] names) { }
    public void WithDefaults(int count = 3, string label = "x") { }
    public bool ByReference(ref int value, out bool flag)
    {
        flag = false;
        return false;
    }

    internal void InternalMethod() { }
    protected void ProtectedMethod() { }
    private void PrivateMethod() { }

    public override void Reset() { }

    public sealed class NestedPublic { }
    protected class NestedProtected { }
    internal class NestedInternal { }
    private class NestedPrivate { }
}

internal class InternalClass
{
    public void PublicMethodOnInternalType() { }
}
