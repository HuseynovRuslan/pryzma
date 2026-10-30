namespace Meydan.Api.Tests.Infrastructure;

/// <summary>
/// A phone number nobody else in the test run has: one phone, one account (D12) holds for test accounts too.
/// </summary>
public static class TestPhones
{
    private static int _next = Random.Shared.Next(1_000_000, 4_000_000);

    public static string Next() => $"+994 70 {Interlocked.Increment(ref _next):D7}";
}
