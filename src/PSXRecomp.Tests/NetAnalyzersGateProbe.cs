using System;

namespace PSXRecomp.Tests;

// Temporary verification fixture for Issue #102 (.NET Analyzers quality gate).
// Intentionally violates CA2201 / CA2200 / CA1069 / CA1816.
// This file must be removed after gate verification.
public class NetAnalyzersGateProbe : IDisposable
{
    public enum ProbeKind
    {
        First = 0,
        Second = 0,
    }

    public void ThrowReservedExceptionType()
    {
        throw new Exception("CA2201 probe");
    }

    public void RethrowLosingStackTrace()
    {
        try
        {
            ThrowReservedExceptionType();
        }
        catch (Exception ex)
        {
            throw ex;
        }
    }

    public void Dispose()
    {
    }
}
