using System.Collections;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;

namespace PSXRecomp.Analyzer.Tests.TestInfrastructure;

// Replacement for the obsolete Microsoft.CodeAnalysis.Testing.Verifiers.XUnit.XUnitVerifier,
// whose assertion types are binary-incompatible with xunit 2.9 (MissingMethodException on failure).
public sealed class XUnit29Verifier : IVerifier
{
    private readonly string? _context;

    public XUnit29Verifier()
    {
    }

    private XUnit29Verifier(string? context)
    {
        _context = context;
    }

    public IVerifier PushContext(string context)
    {
        return new XUnit29Verifier(context);
    }

    public void Empty<T>(string collectionName, IEnumerable<T> collection)
    {
        True(!collection.Any(), $"Expected '{collectionName}' to be empty but was not.");
    }

    public void Equal<T>(T expected, T actual, string? message = null)
    {
        True(
            EqualityComparer<T>.Default.Equals(expected, actual),
            $"{message} Expected: {Format(expected)}. Actual: {Format(actual)}.");
    }

    public void True(bool assert, string? message = null)
    {
        if (!assert)
        {
            Fail(message ?? "Assertion failed.");
        }
    }

    public void False(bool assert, string? message = null)
    {
        if (assert)
        {
            Fail(message ?? "Expected false but was true.");
        }
    }

    [DoesNotReturn]
    public void Fail(string? message = null)
    {
        Xunit.Assert.Fail(AppendContext(message ?? "Verification failed."));
    }

    public void LanguageIsSupported(string language)
    {
        True(language == LanguageNames.CSharp, $"Language '{language}' is not supported.");
    }

    public void NotEmpty<T>(string collectionName, IEnumerable<T> collection)
    {
        True(collection.Any(), $"Expected '{collectionName}' to be non-empty but was empty.");
    }

    public void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, IEqualityComparer<T>? equalityComparer, string? message = null)
    {
        var comparer = equalityComparer ?? EqualityComparer<T>.Default;
        var expectedItems = expected.ToList();
        var actualItems = actual.ToList();

        var equal = expectedItems.Count == actualItems.Count
            && expectedItems.Zip(actualItems, (e, a) => comparer.Equals(e, a)).All(static entry => entry);

        True(equal, $"{message} Expected: [{string.Join(", ", expectedItems)}]. Actual: [{string.Join(", ", actualItems)}].");
    }

    private static string Format<T>(T value)
    {
        return value is null ? "<null>" : value.ToString() ?? "<null>";
    }

    private string AppendContext(string message)
    {
        return _context is null ? message : $"{message} Context: {_context}";
    }
}
