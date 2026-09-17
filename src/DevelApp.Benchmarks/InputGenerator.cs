using System.Text;

namespace DevelApp.Benchmarks
{
    /// <summary>
    /// Deterministic test-input generation shared by the benchmark classes.
    /// A fixed seed is used so that benchmark results are reproducible.
    /// </summary>
    public static class InputGenerator
    {
        /// <summary>
        /// Generate source text consisting of <paramref name="tokenCount"/> space-separated
        /// number tokens, e.g. "12 345 6 ...".
        /// </summary>
        public static string GenerateNumberSource(int tokenCount, int seed = 42)
        {
            var random = new Random(seed);
            var sb = new StringBuilder(tokenCount * 5);
            for (int i = 0; i < tokenCount; i++)
            {
                if (i > 0)
                {
                    sb.Append(' ');
                }
                sb.Append(random.Next(1, 100_000));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Generate source text with a realistic mix of identifiers, numbers,
        /// operators and whitespace for lexer benchmarks.
        /// </summary>
        public static string GenerateMixedSource(int tokenCount, int seed = 42)
        {
            var random = new Random(seed);
            var sb = new StringBuilder(tokenCount * 6);
            for (int i = 0; i < tokenCount; i++)
            {
                if (i > 0)
                {
                    sb.Append(i % 7 == 0 ? "\n" : " ");
                }

                switch (i % 4)
                {
                    case 0:
                        sb.Append("value").Append(i);
                        break;
                    case 1:
                        sb.Append(random.Next(1, 100_000));
                        break;
                    case 2:
                        sb.Append(i % 3 == 0 ? "+" : "-");
                        break;
                    default:
                        sb.Append("name").Append(i);
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Generate an array of codepoints with a deterministic mix of ASCII letters,
        /// ASCII digits, Latin-Extended, Greek, Cyrillic and CJK characters.
        /// </summary>
        public static int[] GenerateMixedCodepoints(int count, int seed = 42)
        {
            var random = new Random(seed);
            var codepoints = new int[count];
            for (int i = 0; i < count; i++)
            {
                switch (i % 6)
                {
                    case 0:
                        codepoints[i] = 'A' + random.Next(26);
                        break;
                    case 1:
                        codepoints[i] = '0' + random.Next(10);
                        break;
                    case 2:
                        codepoints[i] = 0x0100 + random.Next(0x7F);
                        break;
                    case 3:
                        codepoints[i] = 0x03B0 + random.Next(0x30);
                        break;
                    case 4:
                        codepoints[i] = 0x0410 + random.Next(0x40);
                        break;
                    default:
                        codepoints[i] = 0x4E00 + random.Next(0x100);
                        break;
                }
            }
            return codepoints;
        }

        /// <summary>
        /// Encode a set of codepoints as UTF-8 bytes.
        /// </summary>
        public static byte[] ToUtf8(int[] codepoints)
        {
            var sb = new StringBuilder(codepoints.Length);
            foreach (var codepoint in codepoints)
            {
                sb.Append(char.ConvertFromUtf32(codepoint));
            }
            return Encoding.UTF8.GetBytes(sb.ToString());
        }
    }
}
