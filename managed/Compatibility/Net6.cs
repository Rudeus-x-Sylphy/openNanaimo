#if NET6_0
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.All, Inherited = false)]
    internal sealed class RequiredMemberAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.All, Inherited = false, AllowMultiple = true)]
    internal sealed class CompilerFeatureRequiredAttribute : Attribute
    {
        public CompilerFeatureRequiredAttribute(string featureName) => FeatureName = featureName;
        public string FeatureName { get; }
        public bool IsOptional { get; init; }
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.Constructor, Inherited = false)]
    internal sealed class SetsRequiredMembersAttribute : Attribute { }
}

namespace System.IO
{
    public static class Net6StreamExtensions
    {
        public static Task<string?> ReadLineAsync(this StreamReader reader, CancellationToken token) => reader.ReadLineAsync().WaitAsync(token);

        public static async ValueTask ReadExactlyAsync(this Stream stream, Memory<byte> buffer, CancellationToken token = default)
        {
            while (!buffer.IsEmpty)
            {
                int read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException();
                buffer = buffer[read..];
            }
        }
    }
}

namespace System.Threading
{
    public static class Net6CancellationExtensions
    {
        public static Task CancelAsync(this CancellationTokenSource source) => Task.Run(() => source.Cancel());
    }
}

namespace System
{
    public static class Net6CollectionExtensions
    {
        public static void Shuffle<T>(this Random random, T[] values)
        {
            for (int i = values.Length - 1; i > 0; i--)
            {
                int other = random.Next(i + 1);
                (values[i], values[other]) = (values[other], values[i]);
            }
        }

        public static int IndexOfAnyExcept(this Span<byte> values, byte value) => ((ReadOnlySpan<byte>)values).IndexOfAnyExcept(value);

        public static int IndexOfAnyExcept(this ReadOnlySpan<byte> values, byte value)
        {
            for (int i = 0; i < values.Length; i++) if (values[i] != value) return i;
            return -1;
        }
    }
}
#endif
