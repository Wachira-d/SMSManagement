using Microsoft.Extensions.Options;

namespace SMSManagement.Tests;

internal static class TestOptionsMonitor
{
    public static IOptionsMonitor<T> Of<T>(T value) => new Static<T>(value);

    private sealed class Static<T> : IOptionsMonitor<T>
    {
        public Static(T value) => CurrentValue = value;
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
